using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.Cache;
using TaskbarLyrics.Core.Lyrics;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Playback;
using TaskbarLyrics.Gsmtc;
using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Services;

namespace TaskbarLyrics.App.Services;

internal enum LyricsIdentityLoadKind
{
    Applied,
    Unavailable,
    Stale
}

internal readonly record struct LyricsIdentityLoadResult(
    LyricsIdentityLoadKind Kind,
    string? Error = null, bool NoTimedLyrics = false, LyricsDocument? Document = null);

/// <summary>
/// GSMTC → TrackLookup → multi-phase Search → Cache → Network → LyricsDocument + PlaybackClock.
/// Offsets: effective = Estimated + Global + Track.
/// Auto-bind only on High confidence.
/// </summary>
public sealed class TrackCoordinator : IDisposable
{
    private readonly AppLogger _logger;
    private readonly ConfigService _config;
    private readonly GsmtcSessionService _gsmtc;
    private readonly PlaybackClock _clock = new();
    private readonly TimelineSampleProcessor _timeline;
    private readonly LyricsSynchronizer _synchronizer = new();
    private readonly QQMusicHttpClient _http;
    private readonly QQMusicSearchService _search;
    private readonly ArtistAliasStore _aliases;
    private readonly SongMatcher _matcher;
    private readonly MultiPhaseSongSearch _multiSearch;
    private readonly TrackMatchResolver _matchResolver;
    private readonly QQMusicLyricsService _lyricsService;
    private readonly LyricsCache _cache;
    private readonly TrackLookupCache _lookup;
    private readonly TrackSettingsStore _trackSettings;
    private readonly CacheStats _netStats = new();
    private readonly CancellationToken _appToken;
    private readonly CancellationTokenSource _mediaPollStop;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sessionRefreshGate = new(1, 1);

    private readonly LyricsLoadRequests _lyricsRequests = new();
    private readonly MediaReadState _mediaRead = new();
    private long _generation;
    private TrackIdentity? _track;
    private LyricsDocument _document = LyricsDocument.Empty();
    private PlaybackStatus _status = PlaybackStatus.Closed;
    private bool _lyricsLoading;
    private bool _noTimedLyrics;
    private bool _needsManualRematch;
    private bool _disposed;
    private bool _sessionSelected;
    private string? _currentCacheKey;
    private string _matchSource = "None";
    private QQSongIdentity? _currentSong;

    public event Action? StateChanged;

    public TrackCoordinator(
        AppLogger logger,
        ConfigService config,
        TrackLookupCache lookup,
        TrackSettingsStore trackSettings,
        CancellationToken appToken)
    {
        _logger = logger;
        _config = config;
        _lookup = lookup;
        _trackSettings = trackSettings;
        _appToken = appToken;
        _mediaPollStop = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        _gsmtc = new GsmtcSessionService(logger);
        _http = new QQMusicHttpClient(logger);
        _search = new QQMusicSearchService(_http, logger);
        _aliases = new ArtistAliasStore(logger);
        _matcher = new SongMatcher(_aliases);
        _multiSearch = new MultiPhaseSongSearch(_search, _matcher, _aliases, logger);
        _matchResolver = new TrackMatchResolver(new TrackLookupStoreAdapter(_lookup), _multiSearch);
        _lyricsService = new QQMusicLyricsService(_http, logger);
        _cache = new LyricsCache(logger);
        _timeline = new TimelineSampleProcessor(
            _clock,
            logInfo: m => _logger.Info(m),
            logWarn: m => _logger.Warn(m));

        _gsmtc.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _gsmtc.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _gsmtc.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        _gsmtc.SessionsChanged += OnSessionsChanged;
    }

    /// <summary>Playing ~75ms / Scrubbing ~40ms / Paused ~200ms.</summary>
    public int PreferredPollIntervalMs => _timeline.PreferredPollIntervalMs;

    public bool IsScrubbing => _timeline.IsScrubbing;

    public long Generation
    {
        get { lock (_lock) return _generation; }
    }

    public PlaybackControlCapabilities PlaybackCapabilities
    {
        get
        {
            lock (_lock)
            {
                var session = _gsmtc.Session;
                if (_disposed || !_sessionSelected || _mediaRead.IsPending || session is null || string.IsNullOrWhiteSpace(_track?.Title))
                    return new(false, false, false);
                var controls = GsmtcPlaybackController.ReadCapabilities(session);
                return new(controls.CanPlay, controls.CanPause, controls.CanSeek, controls.CanSkipPrevious, controls.CanSkipNext);
            }
        }
    }

    public async Task<bool> ControlPlaybackAsync(PlaybackControlAction action, TimeSpan? position, long expectedGeneration, CancellationToken cancellationToken)
    {
        var session = _gsmtc.Session;
        if (session is null) return false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mediaPollStop.Token);
        // WinRT requests can synchronously contact the player before returning their async operation.
        // Keep those calls away from the UI thread; the captured session and generation stay fixed.
        var navigatesTrack = action is PlaybackControlAction.Previous or PlaybackControlAction.Next;
        Task? navigationRefresh = null;
        var accepted = await Task.Run(() => PlaybackControlGuard.ExecuteAsync(
            token =>
            {
                lock (_lock)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Current() || !_gsmtc.SelectedSessionStillExists()) return null;
                    var controls = GsmtcPlaybackController.ReadCapabilities(session);
                    return action switch
                    {
                        PlaybackControlAction.Play when controls.CanPlay => GsmtcPlaybackController.PlayAsync(session, token),
                        PlaybackControlAction.Pause when controls.CanPause => GsmtcPlaybackController.PauseAsync(session, token),
                        PlaybackControlAction.Seek when controls.CanSeek && position.HasValue => GsmtcPlaybackController.SeekAsync(session, position.Value, token),
                        PlaybackControlAction.Previous when controls.CanSkipPrevious => GsmtcPlaybackController.PreviousAsync(session, token),
                        PlaybackControlAction.Next when controls.CanSkipNext => GsmtcPlaybackController.NextAsync(session, token),
                        _ => null
                    };
                }
            },
            () => { lock (_lock) return CanComplete() && _gsmtc.SelectedSessionStillExists(); },
            () =>
            {
                lock (_lock)
                {
                    if (!CanComplete() || !_gsmtc.SelectedSessionStillExists()) return;
                    // Only actual Windows readings update status/clock. In particular, a seek
                    // while paused never sends Play and never assumes the player started.
                    OnPlaybackInfoChanged();
                    OnTimelinePropertiesChanged();
                    var refresh = HandleMediaPropertiesAsync(announceChange: false);
                    if (navigatesTrack) navigationRefresh = refresh;
                }
            }, stop.Token)).ConfigureAwait(false);
        // Keep navigation busy until its metadata read completes: the old generation may
        // still be displayed after the player has already moved to the next song.
        return navigationRefresh is null ? accepted
            : await PlaybackControlGuard.AwaitRefreshAsync(accepted, navigationRefresh, stop.Token).ConfigureAwait(false);

        bool SameContext() => !_disposed && _sessionSelected
            && !string.IsNullOrWhiteSpace(_track?.Title)
            && PlaybackControlGuard.IsCurrent(session, _gsmtc.Session, expectedGeneration, _generation);
        bool Current() => SameContext() && !_mediaRead.IsPending;
        // A skip may itself start a metadata-change event before the Windows request
        // acknowledges success. Finish that read instead of reporting a spurious failure.
        bool CanComplete() => navigatesTrack
            ? PlaybackControlGuard.CanCompleteNavigation(session, _gsmtc.Session, _sessionSelected, _disposed)
            : Current();
    }

    public LyricsCache Cache => _cache;
    public TrackLookupCache Lookup => _lookup;
    public TrackSettingsStore TrackSettings => _trackSettings;
    public CacheStats NetworkStats => _netStats;
    public QQMusicSearchService SearchService => _search;
    public SongMatcher Matcher => _matcher;
    public MultiPhaseSongSearch MultiSearch => _multiSearch;
    public bool NeedsManualRematch
    {
        get { lock (_lock) return _needsManualRematch; }
    }

    public TrackIdentity? CurrentTrack
    {
        get { lock (_lock) return _track; }
    }

    public QQSongIdentity? CurrentSong
    {
        get { lock (_lock) return _currentSong; }
    }

    public string MatchSource
    {
        get { lock (_lock) return _matchSource; }
    }

    public string? CurrentCacheKey
    {
        get { lock (_lock) return _currentCacheKey; }
    }

    public long GlobalOffsetMs => _config.Current.Lyrics.GlobalOffsetMs;

    public long TrackOffsetMs
    {
        get
        {
            var key = CurrentCacheKey;
            return key is null ? 0 : _trackSettings.GetTrackOffsetMs(key);
        }
    }

    public long EffectiveOffsetMs => GlobalOffsetMs + TrackOffsetMs;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var ok = await _gsmtc.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            _logger.Error("GSMTC manager init failed.");
            return;
        }

        await RefreshSessionAsync().ConfigureAwait(false);
        _ = Task.Run(PollMediaAsync);
        _ = Task.Run(PollSessionsAsync);
    }

    private async Task PollSessionsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_mediaPollStop.Token).ConfigureAwait(false))
                await RefreshSessionAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_mediaPollStop.IsCancellationRequested) { }
    }

    private async Task RefreshSessionAsync()
    {
        try
        {
            await _sessionRefreshGate.WaitAsync(_mediaPollStop.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || _gsmtc.SelectedSessionStillExists()) return;
                lock (_lock)
                {
                    if (_gsmtc.Session is not null)
                    {
                        _gsmtc.ClearSession();
                        _sessionSelected = false;
                        _generation++;
                        _mediaRead.Complete(_mediaRead.Begin(false));
                        _track = null;
                        _document = LyricsDocument.Empty();
                        _lyricsLoading = _needsManualRematch = _noTimedLyrics = false;
                        _currentSong = null;
                        _currentCacheKey = null;
                        _matchSource = "None";
                        _status = PlaybackStatus.Closed;
                        _clock.Reset();
                        _timeline.Reset();
                        _lyricsRequests.Cancel();
                        _logger.Info("GSMTC session removed; cleared stale playback and lyrics.");
                    }
                }
                RaiseStateChanged();
                await SelectBestSessionAsync(_mediaPollStop.Token).ConfigureAwait(false);
                await BootstrapAsync(_mediaPollStop.Token).ConfigureAwait(false);
            }
            finally { _sessionRefreshGate.Release(); }
        }
        catch (OperationCanceledException) when (_mediaPollStop.IsCancellationRequested) { }
        catch (Exception ex) { _logger.Error("Refresh GSMTC session", ex); }
    }

    private async Task PollMediaAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            await MediaPollLoop.RunAsync(timer.WaitForNextTickAsync,
                _ => HandleMediaPropertiesAsync(announceChange: false), _mediaPollStop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.Error("Media polling stopped", ex); }
    }

    public void PollCalibrate()
    {
        var session = _gsmtc.Session;
        if (session is null)
        {
            return;
        }

        // CapturedAt must be at read time (before any other work).
        var capturedAt = DateTimeOffset.Now;

        if (!GsmtcSnapshotReader.TryReadTimeline(session, out var position, out _, out var lastUpdated, out var duration))
        {
            return;
        }

        if (!GsmtcSnapshotReader.TryReadPlaybackInfo(session, out var status, out var rate))
        {
            status = _status;
            rate = 1.0;
        }

        if (duration.HasValue)
        {
            _clock.SetDuration(duration);
        }

        ApplyTimelineSample(new GsmtcTimelineSample(
            position, lastUpdated, capturedAt, status, rate, TimelineSampleSource.Poll));
    }

    public void NotifyOffsetsChanged() => RaiseStateChanged();

    public OverlayUiState GetUiState()
    {
        lock (_lock)
        {
            var clock = _clock.GetSnapshot();
            var global = _config.Current.Lyrics.GlobalOffsetMs;
            var trackOff = string.IsNullOrEmpty(_currentCacheKey)
                ? 0
                : _trackSettings.GetTrackOffsetMs(_currentCacheKey);
            var effectivePos = clock.EstimatedPosition + TimeSpan.FromMilliseconds(global + trackOff);
            if (effectivePos < TimeSpan.Zero)
            {
                effectivePos = TimeSpan.Zero;
            }

            var lyric = _synchronizer.Resolve(_document.Lines, effectivePos);

            string text;
            var staticOnly = false;
            IReadOnlyList<QrcWord> words = Array.Empty<QrcWord>();
            var wordIndex = lyric.WordIndex;
            var wordProgress = lyric.WordProgress;

            if (_mediaRead.IsPending)
            {
                text = "正在读取歌曲…";
                staticOnly = true;
                wordIndex = -1;
                wordProgress = 0;
            }
            else if (_track is null || string.IsNullOrWhiteSpace(_track.Title))
            {
                text = string.Empty;
                staticOnly = true;
            }
            else if (_document.IsInstrumental)
            {
                text = "♪ 纯音乐";
                staticOnly = true;
            }
            else if (_lyricsLoading && _document.Lines.Count == 0)
            {
                text = "♪ " + _track.Title;
                staticOnly = true;
            }
            else if (!string.IsNullOrEmpty(_document.Error) && _document.Lines.Count == 0)
            {
                text = "♪ " + _track.Title;
                staticOnly = true;
            }
            else if (lyric.LineIndex >= 0
                     && lyric.LineIndex < _document.Lines.Count
                     && !string.IsNullOrEmpty(lyric.LineText))
            {
                text = lyric.LineText!;
                words = _document.Lines[lyric.LineIndex].Words ?? new List<QrcWord>();
            }
            else if (_document.Lines.Count == 0)
            {
                text = "♪ " + _track.Title;
                staticOnly = true;
            }
            else
            {
                text = string.Empty;
                staticOnly = true;
            }

            return new OverlayUiState(
                DisplayText: text,
                Track: _track,
                Status: _status,
                Clock: clock,
                Lyric: lyric,
                Generation: _generation,
                LyricsLoading: _lyricsLoading || _mediaRead.IsPending,
                LyricsSource: _document.Source,
                LyricsMode: _document.Mode,
                CurrentLineWords: words,
                StaticLineOnly: staticOnly,
                WordIndex: wordIndex,
                WordProgress: wordProgress,
                GlobalOffsetMs: global,
                TrackOffsetMs: trackOff,
                EffectiveOffsetMs: global + trackOff,
                SongMid: _document.SongMid ?? _currentSong?.SongMid,
                SongId: _document.SongId ?? _currentSong?.SongId,
                MatchSource: _matchSource,
                CacheKey: _currentCacheKey,
                SessionConnected: _sessionSelected, HasLyrics: _document.Lines.Count > 0,
                IsInstrumental: _document.IsInstrumental, Error: _document.Error, NeedsRematch: _needsManualRematch, NoTimedLyrics: _noTimedLyrics, Document: _mediaRead.IsPending ? null : _document);
        }
    }

    public TrackDiagnostics GetDiagnostics()
    {
        var s = GetUiState();
        return new TrackDiagnostics(
            s.Track?.Title ?? "",
            s.Track?.Artist ?? "",
            s.Track?.DurationSeconds,
            s.SongMid,
            s.SongId,
            s.LyricsSource.ToString(),
            s.LyricsMode.ToString(),
            s.MatchSource,
            s.GlobalOffsetMs,
            s.TrackOffsetMs,
            s.EffectiveOffsetMs,
            s.Generation,
            s.CacheKey);
    }

    public void SetTrackOffsetMs(long ms)
    {
        var key = CurrentCacheKey;
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        _trackSettings.SetTrackOffsetMs(key, ms);
        RaiseStateChanged();
    }

    public bool TrySetTrackOffsetMs(long ms, long generation, string? cacheKey)
    {
        lock (_lock)
        {
            if (_generation != generation || string.IsNullOrEmpty(cacheKey) || cacheKey != _currentCacheKey) return false;
            _trackSettings.SetTrackOffsetMs(cacheKey, Math.Clamp(ms, -5000, 5000), requirePersistence: true);
        }
        RaiseStateChanged();
        return true;
    }

    public void ResetTrackOffset()
    {
        var key = CurrentCacheKey;
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        _trackSettings.ResetTrackOffset(key);
        RaiseStateChanged();
    }

    public Task ReloadCurrentLyricsAsync(long? expectedGeneration = null)
    {
        TrackIdentity? track;
        long gen;
        lock (_lock)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _generation)
                throw new InvalidOperationException("歌曲已变化，请对当前歌曲重试。");
            track = _track;
            if (track is null || string.IsNullOrWhiteSpace(track.Title))
            {
                return Task.CompletedTask;
            }

            _generation++;
            gen = _generation;
            _document = LyricsDocument.Empty();
            _lyricsLoading = true;
        }

        CancelLyricsLoad(gen);
        RaiseStateChanged();
        return LoadLyricsAsync(track, gen, forceNetwork: false);
    }

    public Task RedownloadCurrentLyricsAsync(long? expectedGeneration = null)
    {
        TrackIdentity? track;
        long gen;
        string? key;
        lock (_lock)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _generation)
                throw new InvalidOperationException("歌曲已变化，请对当前歌曲重试。");
            track = _track;
            key = _currentCacheKey;
            if (track is null || string.IsNullOrWhiteSpace(track.Title))
            {
                return Task.CompletedTask;
            }

            _generation++;
            gen = _generation;
            _document = LyricsDocument.Empty();
            _lyricsLoading = true;
        }

        if (!string.IsNullOrEmpty(key))
        {
            try
            {
                CacheStatisticsService.DeleteSongCacheDirectory(key);
                _logger.Info($"Deleted lyrics cache for {key}");
            }
            catch (Exception ex)
            {
                _logger.Error("Delete cache", ex);
            }
        }

        CancelLyricsLoad(gen);
        RaiseStateChanged();
        return LoadLyricsAsync(track, gen, forceNetwork: true);
    }

    public bool ClearCurrentManualMatch(long? expectedGeneration = null)
    {
        bool ok;
        lock (_lock)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _generation)
                throw new InvalidOperationException("歌曲已变化，未清除新歌曲的匹配。");
            if (_track is null) return false;
            ok = _lookup.ClearManual(_track.Title, _track.Artist, _track.DurationSeconds, requirePersistence: true);
            if (ok) _matchSource = "Automatic";
        }
        if (ok) RaiseStateChanged();
        return ok;
    }

    public int ClearAllManualMatches()
    {
        int count;
        lock (_lock)
        {
            count = _lookup.ClearAllManual(requirePersistence: true);
            if (_matchSource == "Manual") _matchSource = "Automatic";
        }
        RaiseStateChanged();
        return count;
    }

    public async Task<bool> ApplyManualMatchAsync(QQSongCandidate candidate, long requestGeneration)
    {
        var identity = ToIdentity(candidate);
        TrackIdentity? track;
        long generation;
        lock (_lock)
        {
            if (_generation != requestGeneration)
            {
                _logger.Warn("Manual match discarded: track changed");
                return false;
            }

            track = _track;
            if (track is null)
            {
                return false;
            }

            // Serialize the persisted binding with the generation transition so a
            // concurrent track change cannot turn this confirmation into current state.
            _lookup.SaveManual(track.Title, track.Artist, track.DurationSeconds, identity, requirePersistence: true);
            _generation++;
            generation = _generation;
            _document = LyricsDocument.Empty();
            _lyricsLoading = true;
            _needsManualRematch = false;
            _currentSong = identity;
            _currentCacheKey = identity.CacheKey;
            _matchSource = nameof(MatchSourceKind.Manual);
        }

        CancelLyricsLoad(generation);
        RaiseStateChanged();
        // Use a fresh owned request and the selected identity immediately. Never re-enter
        // automatic matching or require the user to press Reload after confirming.
        await LoadLyricsAsync(track, generation, forceNetwork: false, manualIdentity: identity).ConfigureAwait(false);
        return !IsStale(generation);
    }

    /// <summary>Auto multi-phase candidates (for diagnostics / initial rematch fill).</summary>
    public async Task<IReadOnlyList<QQSongCandidate>> SearchCandidatesAsync(
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        var decision = await _multiSearch.AutoMatchAsync(
            track.Title,
            track.Artist,
            track.DurationSeconds,
            track.Album,
            cancellationToken).ConfigureAwait(false);
        return decision.RankedCandidates;
    }

    public Task<(IReadOnlyList<QQSongCandidate> Items, bool HasMore, string? Error)> ManualSearchAsync(
        string keyword,
        int page,
        int pageSize,
        TrackIdentity track,
        CancellationToken cancellationToken) =>
        _multiSearch.ManualSearchAsync(
            keyword,
            page,
            pageSize,
            track.Title,
            track.Artist,
            track.DurationSeconds,
            track.Album,
            cancellationToken);

    private async Task SelectBestSessionAsync(CancellationToken cancellationToken)
    {
        var candidates = await _gsmtc.EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            _sessionSelected = false;
            return;
        }

        var best = candidates
            .OrderByDescending(c => c.QqScore)
            .ThenByDescending(c => c.Status.Equals("Playing", StringComparison.OrdinalIgnoreCase))
            .First();

        if (best.QqScore < 10)
        {
            var playing = candidates.FirstOrDefault(c =>
                c.Status.Equals("Playing", StringComparison.OrdinalIgnoreCase));
            if (playing is not null)
            {
                best = playing;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_disposed) return;
            _gsmtc.SelectSession(best.Session);
            _sessionSelected = true;
        }
        _logger.Info($"Selected session: {best.Aumid} (qqScore={best.QqScore})");
    }

    private Task BootstrapAsync(CancellationToken cancellationToken) => HandleMediaPropertiesAsync();

    private void OnSessionsChanged()
    {
        _ = Task.Run(RefreshSessionAsync);
    }

    private void OnMediaPropertiesChanged() => _ = HandleMediaPropertiesAsync();

    private void OnPlaybackInfoChanged()
    {
        var session = _gsmtc.Session;
        if (session is null)
        {
            return;
        }

        var capturedAt = DateTimeOffset.Now;

        if (!GsmtcSnapshotReader.TryReadPlaybackInfo(session, out var status, out var rate))
        {
            return;
        }

        TimeSpan position;
        DateTimeOffset lastUpdated;
        if (!GsmtcSnapshotReader.TryReadTimeline(session, out position, out _, out lastUpdated, out var duration))
        {
            // Status-only: still deliver pause/play. Use last raw; stamp LastUpdated=CapturedAt.
            var snap = _clock.GetSnapshot();
            position = snap.RawPosition;
            lastUpdated = capturedAt;
        }
        else if (duration.HasValue)
        {
            _clock.SetDuration(duration);
        }

        ApplyTimelineSample(new GsmtcTimelineSample(
            position, lastUpdated, capturedAt, status, rate, TimelineSampleSource.PlaybackInfoEvent));
    }

    private void OnTimelinePropertiesChanged()
    {
        var session = _gsmtc.Session;
        if (session is null)
        {
            return;
        }

        var capturedAt = DateTimeOffset.Now;

        if (!GsmtcSnapshotReader.TryReadTimeline(session, out var position, out _, out var lastUpdated, out var duration))
        {
            return;
        }

        if (!GsmtcSnapshotReader.TryReadPlaybackInfo(session, out var status, out var rate))
        {
            status = _status;
            rate = 1.0;
        }

        if (duration.HasValue)
        {
            _clock.SetDuration(duration);
        }

        ApplyTimelineSample(new GsmtcTimelineSample(
            position, lastUpdated, capturedAt, status, rate, TimelineSampleSource.TimelineEvent));
    }

    private async Task HandleMediaPropertiesAsync(bool announceChange = true)
    {
        var session = _gsmtc.Session;
        if (session is null) return;
        long revision;
        lock (_lock) revision = _mediaRead.Begin(announceChange);
        // Clear old words before the asynchronous Windows metadata request completes.
        if (announceChange) RaiseStateChanged();
        var started = System.Diagnostics.Stopwatch.StartNew();
        if (announceChange) _logger.Info($"MEDIA READ start revision={revision}");
        var changed = announceChange;
        try
        {
            var snap = await GsmtcSnapshotReader.ReadAsync(session, _mediaPollStop.Token).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_mediaRead.Complete(revision)) return;
                if (snap is not null && ReferenceEquals(session, _gsmtc.Session))
                {
                    changed |= TrackMatchInputSanitizer.IsDifferentTrack(_track, snap.Track) || _status != snap.Status;
                    ApplySnapshot(snap);
                }
            }
            if (changed) _logger.Info($"MEDIA READ applied revision={revision} source={(announceChange ? "event" : "poll")} elapsed={started.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) when (_mediaPollStop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("HandleMediaPropertiesAsync", ex);
        }
        finally
        {
            bool current;
            lock (_lock) current = _mediaRead.Complete(revision);
            if (current && changed) RaiseStateChanged();
        }
    }

    private void ApplySnapshot(PlaybackSnapshot snap)
    {
        var newTrack = snap.Track;
        var matchTrack = newTrack;
        TrackIdentity? oldTrack;
        long newGen = 0;
        var shouldLoad = false;

        lock (_lock)
        {
            oldTrack = _track;
            _status = snap.Status;

            if (TrackMatchInputSanitizer.IsDifferentTrack(oldTrack, newTrack))
            {
                if (string.IsNullOrWhiteSpace(newTrack.Title)
                    && oldTrack is not null
                    && !string.IsNullOrWhiteSpace(oldTrack.Title))
                {
                    // ignore transient empty
                }
                else if (string.IsNullOrWhiteSpace(newTrack.Title))
                {
                    _track = newTrack;
                    _document = LyricsDocument.Empty();
                    _lyricsLoading = false;
                    _currentSong = null;
                    _currentCacheKey = null;
                    _matchSource = "None";
                }
                else
                {
                    matchTrack = TrackMatchInputSanitizer.ForTrackChange(oldTrack, newTrack);
                    _generation++;
                    newGen = _generation;
                    _track = newTrack;
                    _document = LyricsDocument.Empty();
                    _lyricsLoading = true;
                    _needsManualRematch = false;
                    _noTimedLyrics = false;
                    _currentSong = null;
                    _currentCacheKey = null;
                    _matchSource = "None";
                    _clock.Reset();
                    _timeline.Reset();
                    _clock.SetDuration(newTrack.Duration);
                    shouldLoad = true;
                    _logger.Info($"TRACK CHANGE gen={newGen} New={newTrack.DisplayName}");
                    if (newTrack.Duration.HasValue && !matchTrack.Duration.HasValue)
                    {
                        _logger.Warn(
                            $"MATCH DURATION IGNORED inherited={newTrack.DurationSeconds}s " +
                            $"previous={oldTrack?.DisplayName}");
                    }
                }
            }
            else if (_track is not null && !_track.Duration.HasValue && newTrack.Duration.HasValue)
            {
                _track = _track with { Duration = newTrack.Duration };
                _clock.SetDuration(newTrack.Duration);
            }
        }

        if (shouldLoad)
        {
            CancelLyricsLoad(newGen);
            _ = LoadLyricsAsync(matchTrack, newGen, forceNetwork: false);
            RaiseStateChanged();
        }

        ApplyTimelineSample(new GsmtcTimelineSample(
            snap.RawPosition,
            snap.TimelineLastUpdatedTime,
            snap.CapturedAt,
            snap.Status,
            snap.PlaybackRate,
            TimelineSampleSource.TimelineEvent));
    }

    private void ApplyTimelineSample(GsmtcTimelineSample sample)
    {
        _timeline.Process(sample);
        bool changed;
        lock (_lock)
        {
            changed = _status != sample.Status;
            _status = sample.Status;
        }
        if (changed) RaiseStateChanged();
    }

    private void CancelLyricsLoad(long? expectedGeneration = null)
    {
        lock (_lock)
        {
            if (expectedGeneration.HasValue && expectedGeneration != _generation) return;
            _lyricsRequests.Cancel();
        }
    }

    private async Task LoadLyricsAsync(TrackIdentity track, long generation, bool forceNetwork, QQSongIdentity? manualIdentity = null)
    {
        LyricsLoadRequests.Request? request;
        lock (_lock)
        {
            request = _lyricsRequests.Begin(generation, _generation, _appToken);
            if (request is null) return;
            _lyricsLoading = true;
        }
        using var ownedRequest = request;
        var token = request.Token;
        RaiseStateChanged();
        _logger.Info($"Lyrics load start gen={generation} track={track.DisplayName} forceNet={forceNetwork}");

        try
        {
            // Existing bindings (Manual first) win. A cache miss runs the multi-phase
            // automatic search and persists only a High-confidence unique result.
            var resolution = await _matchResolver.ResolveAsync(
                track, token, confirmedSelection: manualIdentity).ConfigureAwait(false);

            if (token.IsCancellationRequested || IsStale(generation))
            {
                _logger.Info("Lyrics request cancelled due to track change.");
                return;
            }

            if (resolution.Kind == TrackMatchResolutionKind.ManualRequired || resolution.Song is null)
            {
                lock (_lock)
                {
                    if (generation != _generation || token.IsCancellationRequested) return;
                    _needsManualRematch = true;
                    _matchSource = "None";
                    _currentSong = null;
                    _currentCacheKey = null;
                }

                _logger.Warn(
                    "LYRICS MATCH AMBIGUOUS\n" +
                    $"Title: {track.Title}\n" +
                    $"Artist: {track.Artist}\n" +
                    $"Reason: {resolution.Reason}\n" +
                    $"Unique: {resolution.CandidateCount}");

                // Prefer no lyrics over wrong lyrics.
                ApplyDocument(generation, LyricsDocument.Empty(resolution.Reason));
                return;
            }

            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested) return;
                _needsManualRematch = false;
            }

            _logger.Info(
                $"Track match source={resolution.MatchSource} mid={resolution.Song.SongMid} " +
                $"key={resolution.Song.CacheKey}");
            var loadResult = await LoadLyricsForIdentityAsync(
                track,
                resolution.Song,
                generation,
                resolution.MatchSource,
                forceNetwork,
                resolution.MatchScore, token).ConfigureAwait(false);

            if (loadResult.Kind == LyricsIdentityLoadKind.Unavailable
                && resolution.CanRetryAfterMissingLyrics)
            {
                await RetryAutomaticMatchAfterMissingLyricsAsync(
                    track,
                    generation,
                    token,
                    resolution.Song,
                    loadResult.Error).ConfigureAwait(false);
            }
            else if (loadResult.Kind == LyricsIdentityLoadKind.Unavailable)
            {
                ApplyDocument(generation, LyricsDocument.Empty(loadResult.Error ?? "No lyrics"), loadResult.NoTimedLyrics);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Lyrics request cancelled due to track change.");
        }
        catch (Exception ex)
        {
            _netStats.NetworkLyricsFailures++;
            _logger.Error("LoadLyricsAsync", ex);
            if (!IsStale(generation))
            {
                ApplyDocument(generation, LyricsDocument.Empty(ex.Message));
            }
        }
    }

    private async Task RetryAutomaticMatchAfterMissingLyricsAsync(
        TrackIdentity track,
        long generation,
        CancellationToken token,
        QQSongIdentity failedSong,
        string? originalError)
    {
        _logger.Warn(
            $"Automatic binding has no lyrics; retrying search gen={generation} " +
            $"mid={failedSong.SongMid} key={failedSong.CacheKey}");

        var retry = await _matchResolver.ResolveAsync(
            track,
            token,
            retryAutomaticBinding: true).ConfigureAwait(false);

        if (token.IsCancellationRequested || IsStale(generation))
        {
            return;
        }

        var sameSong = retry.Song is not null
            && string.Equals(retry.Song.CacheKey, failedSong.CacheKey, StringComparison.OrdinalIgnoreCase);
        if (retry.Kind == TrackMatchResolutionKind.ManualRequired || retry.Song is null || sameSong)
        {
            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested) return;
                _needsManualRematch = true;
            }

            var error = sameSong
                ? "Automatic rematch found the same version without lyrics — open rematch to choose"
                : retry.Reason;
            ApplyDocument(
                generation,
                LyricsDocument.Empty(string.IsNullOrWhiteSpace(error) ? originalError ?? "No lyrics" : error));
            return;
        }

        lock (_lock)
        {
            if (generation != _generation || token.IsCancellationRequested) return;
            _needsManualRematch = false;
        }

        _logger.Info(
            $"Automatic rematch selected replacement mid={retry.Song.SongMid} key={retry.Song.CacheKey}");
        var replacementResult = await LoadLyricsForIdentityAsync(
            track,
            retry.Song,
            generation,
            retry.MatchSource,
            forceNetwork: false,
            retry.MatchScore, token).ConfigureAwait(false);

        if (replacementResult.Kind == LyricsIdentityLoadKind.Unavailable)
        {
            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested) return;
                _needsManualRematch = true;
            }

            ApplyDocument(
                generation,
                LyricsDocument.Empty(replacementResult.Error ?? originalError ?? "No lyrics"), replacementResult.NoTimedLyrics);
        }
    }

    private async Task<LyricsIdentityLoadResult> LoadLyricsForIdentityAsync(
        TrackIdentity track,
        QQSongIdentity identity,
        long generation,
        string matchSource,
        bool forceNetwork,
        double? matchScore, CancellationToken token)
    {

        lock (_lock)
        {
            if (generation != _generation || token.IsCancellationRequested)
                return new LyricsIdentityLoadResult(LyricsIdentityLoadKind.Stale);
            _currentSong = identity;
            _currentCacheKey = identity.CacheKey;
            _matchSource = matchSource;
        }

        if (!forceNetwork)
        {
            var cacheResult = await _cache.TryLoadAsync(identity, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || IsStale(generation))
            {
                return new LyricsIdentityLoadResult(LyricsIdentityLoadKind.Stale);
            }

            if (cacheResult.Status == LyricsCacheStatus.Hit && cacheResult.Document is not null)
            {
                var doc = cacheResult.Document;
                if (doc.IsInstrumental || doc.LineCount > 0)
                {
                    ApplyDocument(generation, new LyricsDocument
                    {
                        SongId = identity.SongId,
                        SongMid = identity.SongMid,
                        DisplayName = string.IsNullOrEmpty(identity.Artists)
                            ? identity.Title
                            : $"{identity.Title} - {identity.Artists}",
                        MatchScore = matchScore,
                        Mode = doc.Mode,
                        Source = LyricsSourceKind.Cache,
                        Lines = doc.Lines,
                        Translation = doc.Translation,
                        IsInstrumental = doc.IsInstrumental
                    });
                    _logger.Info($"Lyrics CACHE HIT gen={generation} mid={identity.SongMid} lines={doc.LineCount}");
                    return new LyricsIdentityLoadResult(LyricsIdentityLoadKind.Applied);
                }

                // An empty, non-instrumental cache entry is not a successful lyric load.
                // Give the provider one chance before deciding the Automatic binding is stale.
                _logger.Warn($"Lyrics CACHE EMPTY gen={generation} mid={identity.SongMid}; trying network");
            }
        }

        _logger.Info($"Lyrics NETWORK gen={generation} mid={identity.SongMid}");
        var candidate = new QQSongCandidate
        {
            SongId = identity.SongId,
            SongMid = identity.SongMid,
            Title = identity.Title,
            Artists = identity.Artists,
            Album = identity.Album,
            DurationSeconds = identity.DurationSeconds,
            MatchScore = matchScore ?? 0
        };

        var lyrics = await _lyricsService.GetLyricsAsync(candidate, token).ConfigureAwait(false);
        if (token.IsCancellationRequested || IsStale(generation))
        {
            return new LyricsIdentityLoadResult(LyricsIdentityLoadKind.Stale);
        }

        IReadOnlyList<QrcLine> lines;
        LyricsMode mode;
        if (lyrics.ParsedQrcLines.Count > 0)
        {
            lines = lyrics.ParsedQrcLines.OrderBy(l => l.StartMs).ToList();
            mode = LyricsMode.Qrc;
        }
        else if (!string.IsNullOrWhiteSpace(lyrics.Lrc) && !lyrics.IsInstrumental)
        {
            lines = LrcLineParser.Parse(lyrics.Lrc);
            mode = LyricsMode.Lrc;
        }
        else if (lyrics.IsInstrumental)
        {
            try { await _cache.SaveAsync(identity, lyrics, token).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Error("Cache save instrumental", ex); }

            ApplyDocument(generation, new LyricsDocument
            {
                SongId = identity.SongId,
                SongMid = identity.SongMid,
                DisplayName = candidate.DisplayName,
                MatchScore = matchScore,
                Mode = LyricsMode.None,
                Source = LyricsSourceKind.Network,
                IsInstrumental = true,
                Lines = Array.Empty<QrcLine>()
            });
            _netStats.NetworkLyricsLoads++;
            return new LyricsIdentityLoadResult(LyricsIdentityLoadKind.Applied);
        }
        else
        {
            _netStats.NetworkLyricsFailures++;
            return new LyricsIdentityLoadResult(
                LyricsIdentityLoadKind.Unavailable,
                lyrics.QrcDetail.Error ?? lyrics.LrcDetail.Error ?? "No lyrics", lyrics.HasConfirmedNoLyrics);
        }

        if (lines.Count == 0)
        {
            _netStats.NetworkLyricsFailures++;
            return new LyricsIdentityLoadResult(
                LyricsIdentityLoadKind.Unavailable,
                "Lyrics response contained no timed lines", NoTimedLyrics: true);
        }

        try
        {
            await _cache.SaveAsync(identity, lyrics, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error("Cache save", ex);
        }

        if (IsStale(generation))
        {
            return new LyricsIdentityLoadResult(LyricsIdentityLoadKind.Stale);
        }

        _netStats.NetworkLyricsLoads++;
        ApplyDocument(generation, new LyricsDocument
        {
            SongId = identity.SongId,
            SongMid = identity.SongMid,
            DisplayName = candidate.DisplayName,
            MatchScore = matchScore,
            Mode = mode,
            Source = LyricsSourceKind.Network,
            Translation = lyrics.Translation,
            Lines = lines
        });
        _logger.Info($"Lyrics NETWORK applied gen={generation} mode={mode} lines={lines.Count}");
        return new LyricsIdentityLoadResult(LyricsIdentityLoadKind.Applied);
    }

    private static QQSongIdentity ToIdentity(QQSongCandidate c) =>
        new(c.SongId, c.SongMid, c.Title, c.Artists, c.Album, c.DurationSeconds);

    private void ApplyDocument(long generation, LyricsDocument document, bool noTimedLyrics = false)
    {
        lock (_lock)
        {
            if (_generation != generation)
            {
                return;
            }

            _document = document;
            _noTimedLyrics = noTimedLyrics;
            _lyricsLoading = false;
            if (!string.IsNullOrEmpty(document.SongMid) || !string.IsNullOrEmpty(document.SongId))
            {
                try
                {
                    _currentCacheKey = new QQSongIdentity(
                        document.SongId ?? "",
                        document.SongMid ?? "",
                        document.DisplayName ?? "",
                        "",
                        "",
                        0).CacheKey;
                }
                catch
                {
                    // ignore
                }
            }
        }

        RaiseStateChanged();
    }

    private bool IsStale(long generation)
    {
        lock (_lock)
        {
            return _generation != generation;
        }
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { _logger.Error("StateChanged", ex); }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mediaPollStop.Cancel();
        _gsmtc.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _gsmtc.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _gsmtc.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _gsmtc.SessionsChanged -= OnSessionsChanged;
        _lyricsRequests.Dispose();
        _gsmtc.Dispose();
        _http.Dispose();
    }
}

public sealed record OverlayUiState(
    string DisplayText,
    TrackIdentity? Track,
    PlaybackStatus Status,
    PlaybackClockSnapshot Clock,
    CurrentLyricState Lyric,
    long Generation,
    bool LyricsLoading,
    LyricsSourceKind LyricsSource,
    LyricsMode LyricsMode,
    IReadOnlyList<QrcWord> CurrentLineWords,
    bool StaticLineOnly,
    int WordIndex,
    double WordProgress,
    long GlobalOffsetMs = 0,
    long TrackOffsetMs = 0,
    long EffectiveOffsetMs = 0,
    string? SongMid = null,
    string? SongId = null,
    string MatchSource = "None",
    string? CacheKey = null,
    bool SessionConnected = false, bool HasLyrics = false, bool IsInstrumental = false,
    string? Error = null, bool NeedsRematch = false, bool NoTimedLyrics = false, LyricsDocument? Document = null);

public sealed record TrackDiagnostics(
    string Title,
    string Artist,
    int? DurationSeconds,
    string? SongMid,
    string? SongId,
    string LyricsSource,
    string LyricsMode,
    string MatchSource,
    long GlobalOffsetMs,
    long TrackOffsetMs,
    long EffectiveOffsetMs,
    long Generation,
    string? CacheKey);
