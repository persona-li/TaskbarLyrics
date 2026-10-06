// GSMTC manager + session selection adapted from GsmtcProbe.

using TaskbarLyrics.Core.Models;
using Windows.Foundation;
using Windows.Media.Control;

namespace TaskbarLyrics.Gsmtc;

public sealed class GsmtcSessionService : IDisposable
{
    private static readonly string[] QqKeywords = ["qq", "qqmusic", "tencent"];

    private readonly IGsmtcLogger _logger;
    private readonly object _sync = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _sessionAumid = string.Empty;

    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _sessionsChanged;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _mediaChanged;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playbackChanged;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _timelineChanged;

    private bool _managerHooked;
    private bool _sessionHooked;
    private bool _disposed;

    public event Action? SessionsChanged;
    public event Action? MediaPropertiesChanged;
    public event Action? PlaybackInfoChanged;
    public event Action? TimelinePropertiesChanged;

    public GsmtcSessionService(IGsmtcLogger logger)
    {
        _logger = logger;
    }

    public GlobalSystemMediaTransportControlsSessionManager? Manager => _manager;
    public GlobalSystemMediaTransportControlsSession? Session
    {
        get
        {
            lock (_sync)
            {
                return _session;
            }
        }
    }

    public string SessionAumid
    {
        get
        {
            lock (_sync)
            {
                return _sessionAumid;
            }
        }
    }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _logger.Info("Requesting GlobalSystemMediaTransportControlsSessionManager...");
            var manager = await GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (manager is null)
            {
                _logger.Error("RequestAsync() returned null.");
                return false;
            }

            _manager = manager;
            HookManager();
            _logger.Info("GSMTC manager ready.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("GSMTC RequestAsync failed", ex);
            return false;
        }
    }

    public IReadOnlyList<SessionCandidate> EnumerateSessions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_manager is null)
        {
            return Array.Empty<SessionCandidate>();
        }

        List<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = _manager.GetSessions()?.ToList()
                       ?? new List<GlobalSystemMediaTransportControlsSession>();
        }
        catch (Exception ex)
        {
            _logger.Error("GetSessions failed", ex);
            return Array.Empty<SessionCandidate>();
        }

        var list = new List<SessionCandidate>(sessions.Count);
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            string aumid;
            try
            {
                aumid = s.SourceAppUserModelId ?? string.Empty;
            }
            catch
            {
                aumid = string.Empty;
            }

            string title = string.Empty;
            string artist = string.Empty;
            string status = "?";
            try
            {
                var props = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                title = props?.Title ?? string.Empty;
                artist = props?.Artist ?? string.Empty;
            }
            catch
            {
                // ignore
            }

            try
            {
                status = s.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "?";
            }
            catch
            {
                // ignore
            }

            var score = ScoreQqCandidate(aumid, title, artist, status);
            list.Add(new SessionCandidate(i, s, aumid, title, artist, status, score));
        }

        return list;
    }

    public async Task<IReadOnlyList<SessionCandidate>> EnumerateSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_manager is null)
        {
            return Array.Empty<SessionCandidate>();
        }

        List<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = _manager.GetSessions()?.ToList()
                       ?? new List<GlobalSystemMediaTransportControlsSession>();
        }
        catch (Exception ex)
        {
            _logger.Error("GetSessions failed", ex);
            return Array.Empty<SessionCandidate>();
        }

        var list = new List<SessionCandidate>(sessions.Count);
        for (var i = 0; i < sessions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var s = sessions[i];
            string aumid;
            try
            {
                aumid = s.SourceAppUserModelId ?? string.Empty;
            }
            catch
            {
                aumid = string.Empty;
            }

            string title = string.Empty;
            string artist = string.Empty;
            string status = "?";
            try
            {
                var props = await s.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
                title = props?.Title ?? string.Empty;
                artist = props?.Artist ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // ignore
            }

            try
            {
                status = s.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "?";
            }
            catch
            {
                // ignore
            }

            var score = ScoreQqCandidate(aumid, title, artist, status);
            list.Add(new SessionCandidate(i, s, aumid, title, artist, status, score));
        }

        return list;
    }

    public static int ScoreQqCandidate(string aumid, string title, string artist, string status)
    {
        var score = 0;
        var hay = $"{aumid} {title} {artist}".ToLowerInvariant();
        foreach (var k in QqKeywords)
        {
            if (hay.Contains(k, StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        if (status.Equals("Playing", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }
        else if (status.Equals("Paused", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }

    public static bool LooksLikeQqMusic(string? aumid, string? title, string? artist)
    {
        var hay = $"{aumid} {title} {artist}".ToLowerInvariant();
        return QqKeywords.Any(k => hay.Contains(k, StringComparison.Ordinal));
    }

    public bool SelectedSessionStillExists()
    {
        var selected = Session;
        return _manager is not null && SessionPresence.Contains(selected, _manager.GetSessions());
    }

    public void ClearSession()
    {
        lock (_sync)
        {
            UnhookSessionUnlocked();
            _session = null;
            _sessionAumid = string.Empty;
        }
    }

    public void SelectSession(GlobalSystemMediaTransportControlsSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);

        lock (_sync)
        {
            UnhookSessionUnlocked();
            _session = session;
            try
            {
                _sessionAumid = session.SourceAppUserModelId ?? string.Empty;
            }
            catch
            {
                _sessionAumid = string.Empty;
            }

            HookSessionUnlocked(session);
        }

        _logger.Info($"Selected session: {_sessionAumid}");
    }

    private void HookManager()
    {
        if (_manager is null || _managerHooked)
        {
            return;
        }

        _sessionsChanged = (_, _) =>
        {
            try
            {
                SessionsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Error("SessionsChanged handler", ex);
            }
        };

        try
        {
            _manager.SessionsChanged += _sessionsChanged;
            _managerHooked = true;
        }
        catch (Exception ex)
        {
            _logger.Error("Hook manager events failed", ex);
        }
    }

    private void HookSessionUnlocked(GlobalSystemMediaTransportControlsSession session)
    {
        _mediaChanged = (_, _) =>
        {
            try
            {
                MediaPropertiesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Error("MediaPropertiesChanged handler", ex);
            }
        };
        _playbackChanged = (_, _) =>
        {
            try
            {
                PlaybackInfoChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Error("PlaybackInfoChanged handler", ex);
            }
        };
        _timelineChanged = (_, _) =>
        {
            try
            {
                TimelinePropertiesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Error("TimelinePropertiesChanged handler", ex);
            }
        };

        try
        {
            session.MediaPropertiesChanged += _mediaChanged;
            session.PlaybackInfoChanged += _playbackChanged;
            session.TimelinePropertiesChanged += _timelineChanged;
            _sessionHooked = true;
        }
        catch (Exception ex)
        {
            _logger.Error("Hook session events failed", ex);
            _sessionHooked = false;
        }
    }

    private void UnhookSessionUnlocked()
    {
        if (_session is null || !_sessionHooked)
        {
            _sessionHooked = false;
            return;
        }

        try
        {
            if (_mediaChanged is not null)
            {
                _session.MediaPropertiesChanged -= _mediaChanged;
            }

            if (_playbackChanged is not null)
            {
                _session.PlaybackInfoChanged -= _playbackChanged;
            }

            if (_timelineChanged is not null)
            {
                _session.TimelinePropertiesChanged -= _timelineChanged;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Unhook session events failed", ex);
        }
        finally
        {
            _sessionHooked = false;
            _mediaChanged = null;
            _playbackChanged = null;
            _timelineChanged = null;
        }
    }

    private void UnhookManager()
    {
        if (_manager is null || !_managerHooked)
        {
            return;
        }

        try
        {
            if (_sessionsChanged is not null)
            {
                _manager.SessionsChanged -= _sessionsChanged;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Unhook manager failed", ex);
        }
        finally
        {
            _managerHooked = false;
            _sessionsChanged = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            UnhookSessionUnlocked();
            _session = null;
        }

        UnhookManager();
        _manager = null;
    }
}

public sealed record SessionCandidate(
    int Index,
    GlobalSystemMediaTransportControlsSession Session,
    string Aumid,
    string Title,
    string Artist,
    string Status,
    int QqScore);
