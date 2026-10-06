using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.Cache;
namespace TaskbarLyrics.App.Presentation;

public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ConfigService _config;
    private readonly ISettingsBackend _backend;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll, _noticeTimer;
    private CancellationTokenSource _pageCts = new();
    private bool _active, _disposed;
    private PlaybackPresentation _state;
    private readonly NavigationPlaybackLabel _navigationLabel = new();
    public string OverviewPlaybackLabel => _navigationLabel.Read(State, Environment.TickCount64);
    private CacheStatistics? _statistics;
    private long _statisticsRequestVersion;
    private string _notice = "";
    private bool _noticeError, _busy, _playbackControlBusy;
    private bool _customPaletteExpanded, _manualColorsExpanded;
    private long _saveRevision = -1;
    private string _saveText = "已保存", _saveError = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<SettingsPage>? NavigateRequested;
    public event Action? RematchRequested;
    public Func<string, string, bool>? Confirm { get; set; }
    public SettingsPage Page { get; private set; } = SettingsPage.Overview;
    public SettingsViewModel(ConfigService config, ISettingsBackend backend, Dispatcher? dispatcher = null)
    {
        _config = config; _backend = backend; _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _state = PlaybackPresentation.From(backend.ReadState());
        _poll = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _poll.Tick += (_, _) => Refresh();
        _noticeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); if (!NoticeError) { _notice = ""; Raise(nameof(Notice)); Raise(nameof(HasNotice)); } };
        config.ConfigChanged += ConfigChanged; config.SaveStateChanged += SaveChanged; backend.StateChanged += BackendChanged;
        NavigateCommand = new RelayCommand(p => { if (Enum.TryParse<SettingsPage>(p?.ToString(), out var page)) NavigateRequested?.Invoke(SettingsNavigation.Normalize(page)); });
        RematchCommand = new RelayCommand(_ => RematchRequested?.Invoke(), _ => CanOperate);
        OperationCommand = new RelayCommand(async p => { if (Enum.TryParse<SettingsOperation>(p?.ToString(), out var op)) await RunOperationAsync(op); }, _ => !IsBusy);
        RememberNormalColorCommand = new RelayCommand(p => RememberColor(p as string, RecentNormalColors, (g, colors) => g.RecentNormalColors = colors, nameof(RecentNormalColors)));
        RememberHighlightColorCommand = new RelayCommand(p => RememberColor(p as string, RecentHighlightColors, (g, colors) => g.RecentHighlightColors = colors, nameof(RecentHighlightColors)));
        RememberShadowColorCommand = new RelayCommand(p => RememberColor(p as string, RecentShadowColors, (g, colors) => g.RecentShadowColors = colors, nameof(RecentShadowColors)));
        ResetColorsCommand = new RelayCommand(() => { _config.ResetColorsToDefaults(); Notify("已恢复默认配色"); });
        PresetCommand = new RelayCommand(p => { if (p is PalettePreset preset) ApplyPreset(preset); });
        CopyColorCommand = new RelayCommand(p => { if (p is string color) Clipboard.SetText(color); });
        AdjustTimingCommand = new RelayCommand(p => AdjustTiming(p as string));
        TogglePlaybackCommand = new RelayCommand(async _ => await TogglePlaybackAsync(), _ => CanTogglePlayback);
        PreviousTrackCommand = new RelayCommand(async _ => await PreviousTrackAsync(), _ => CanSkipPrevious);
        NextTrackCommand = new RelayCommand(async _ => await NextTrackAsync(), _ => CanSkipNext);
        ReplayFiveSecondsCommand = new RelayCommand(async _ => await ReplayFiveSecondsAsync(), _ => CanSeekPlayback);
        SeekPlaybackCommand = new RelayCommand(async p =>
        {
            if (p is double seconds) await SeekPlaybackAsync(seconds);
        }, _ => CanSeekPlayback);
        ResetAppearanceCommand = new RelayCommand(() => { if (Confirm?.Invoke("恢复默认外观", "恢复字体、颜色和任务栏布局；保留匹配、缓存和全部时间偏移。") == true) { _config.ResetAppearanceToDefaults(); Notify("已恢复"); } });
        RetrySaveCommand = new RelayCommand(() => { try { _config.SaveNow(); } catch (Exception ex) { Notify("保存失败：" + ex.Message, true); } });
        RefreshDataCommand = new RelayCommand(async () => await RefreshStatisticsAsync());
        OpenFolderCommand = new RelayCommand(p => { try { _backend.OpenFolder(p?.ToString() ?? "cache"); } catch (Exception ex) { Notify("无法打开目录：" + ex.Message, true); } });
        CopyDiagnosticsCommand = new RelayCommand(() => { try { Clipboard.SetText(Diagnostics); Notify("已复制"); } catch (Exception ex) { Notify("复制失败：" + ex.Message, true); } });
        SaveChanged(config.SaveState);
    }
    public ICommand NavigateCommand { get; }
    public ICommand RematchCommand { get; }
    public ICommand OperationCommand { get; }
    public ICommand PresetCommand { get; }
    public ICommand CopyColorCommand { get; }
    public ICommand AdjustTimingCommand { get; }
    public ICommand TogglePlaybackCommand { get; }
    public ICommand PreviousTrackCommand { get; }
    public ICommand NextTrackCommand { get; }
    public ICommand ReplayFiveSecondsCommand { get; }
    public ICommand SeekPlaybackCommand { get; }
    public IReadOnlyList<double> TimingSteps { get; } = [50, 100, 500];
    private double _timingStep=100;
    public double TimingStep
    {
        get => _timingStep;
        set
        {
            if (value is not (50 or 100 or 500) || _timingStep == value) return;
            _timingStep = value;
            Raise(); Raise(nameof(TimingEarlierLabel)); Raise(nameof(TimingLaterLabel)); Raise(nameof(TimingStepLabel));
        }
    }
    public string TimingEarlierLabel => $"提前 {TimingStep:0} ms";
    public string TimingLaterLabel => $"延后 {TimingStep:0} ms";
    public string TimingStepLabel => $"每次 {TimingStep:0} ms";
    public string ActualTimingEffect => State.ActualTimingEffect;
    public string GlobalTimingSummary => State.GlobalTimingSummary;
    public bool IsPlaybackControlBusy => _playbackControlBusy;
    public bool CanTogglePlayback => !_disposed && !_playbackControlBusy && State.HasTrack &&
        (PlaybackButtonIsPlaying ? _backend.PlaybackCapabilities.CanPause : _backend.PlaybackCapabilities.CanPlay);
    public bool CanSeekPlayback => !_disposed && !_playbackControlBusy && State.HasTrack &&
        PlaybackDurationSeconds > 0 && _backend.PlaybackCapabilities.CanSeek;
    public bool CanSkipPrevious => !_disposed && !_playbackControlBusy && State.HasTrack && _backend.PlaybackCapabilities.CanSkipPrevious;
    public bool CanSkipNext => !_disposed && !_playbackControlBusy && State.HasTrack && _backend.PlaybackCapabilities.CanSkipNext;
    public string SeekSupportHint => !State.HasTrack ? ""
        : !_backend.PlaybackCapabilities.CanSeek ? "播放器不支持跳转"
        : PlaybackDurationSeconds <= 0 ? "暂未获取歌曲时长" : "";
    public double PlaybackDurationSeconds => State.HasTrack && State.Raw.Track?.Duration is TimeSpan duration && duration > TimeSpan.Zero
        ? duration.TotalSeconds : 0;
    public double PlaybackPositionSeconds => PlaybackDurationSeconds > 0
        ? Math.Clamp(State.Raw.Clock.EstimatedPosition.TotalSeconds, 0, PlaybackDurationSeconds) : 0;
    public bool PlaybackButtonIsPlaying => _navigationLabel.IsPlaying(State, Environment.TickCount64);
    public string PlaybackButtonLabel => PlaybackButtonIsPlaying ? "暂停" : "播放";
    public string PlaybackButtonGlyph => PlaybackButtonIsPlaying ? "\uE769" : "\uE768";
    public Task TogglePlaybackAsync() => CanTogglePlayback
        ? RunPlaybackControlAsync(PlaybackButtonIsPlaying ? PlaybackControlAction.Pause : PlaybackControlAction.Play)
        : Task.CompletedTask;
    public Task PreviousTrackAsync() => CanSkipPrevious
        ? RunPlaybackControlAsync(PlaybackControlAction.Previous) : Task.CompletedTask;
    public Task NextTrackAsync() => CanSkipNext
        ? RunPlaybackControlAsync(PlaybackControlAction.Next) : Task.CompletedTask;
    public Task ReplayFiveSecondsAsync()
    {
        if (!CanSeekPlayback) return Task.CompletedTask;
        var current = _backend.ReadState();
        if (current.Generation != State.Raw.Generation) { Refresh(); return Task.CompletedTask; }
        return SeekPlaybackAsync(current.Clock.EstimatedPosition.TotalSeconds - 5);
    }
    public Task SeekPlaybackAsync(double seconds) => CanSeekPlayback && double.IsFinite(seconds)
        ? RunPlaybackControlAsync(PlaybackControlAction.Seek, TimeSpan.FromSeconds(Math.Clamp(seconds, 0, PlaybackDurationSeconds)))
        : Task.CompletedTask;
    private async Task RunPlaybackControlAsync(PlaybackControlAction action, TimeSpan? position = null)
    {
        if (_disposed || _playbackControlBusy) return;
        var generation = State.Raw.Generation;
        if (_backend.ReadState().Generation != generation) { Refresh(); return; }
        var token = _pageCts.Token;
        if (token.IsCancellationRequested) return;
        var navigating = action is PlaybackControlAction.Previous or PlaybackControlAction.Next;
        if (navigating) _navigationLabel.Begin(State, Environment.TickCount64);
        else _navigationLabel.Clear();
        var navigationAccepted = false;
        _playbackControlBusy = true;
        RaisePlaybackControls();
        try
        {
            // Seeking and calibration never implicitly start playback.
            var accepted = await _backend.ControlPlaybackAsync(action, position, generation, token);
            navigationAccepted = accepted;
            if (_disposed || token.IsCancellationRequested || _backend.ReadState().Generation != generation) return;
            Refresh();
            if (!accepted) Notify(action == PlaybackControlAction.Seek ? "播放器暂未响应跳转，请重试" : "播放器暂未响应播放控制，请重试", true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested && _backend.ReadState().Generation == generation)
                Notify("播放控制失败：" + ex.Message, true);
        }
        finally
        {
            _playbackControlBusy = false;
            if (!navigationAccepted || token.IsCancellationRequested) _navigationLabel.Clear();
            if (!_disposed) Refresh();
        }
    }
    private void RaisePlaybackControls()
    {
        Raise(nameof(OverviewPlaybackLabel));
        Raise(nameof(IsPlaybackControlBusy)); Raise(nameof(CanTogglePlayback)); Raise(nameof(CanSeekPlayback));
        Raise(nameof(CanSkipPrevious)); Raise(nameof(CanSkipNext));
        Raise(nameof(SeekSupportHint));
        Raise(nameof(PlaybackPositionSeconds)); Raise(nameof(PlaybackDurationSeconds));
        Raise(nameof(PlaybackButtonIsPlaying)); Raise(nameof(PlaybackButtonLabel)); Raise(nameof(PlaybackButtonGlyph));
        CommandManager.InvalidateRequerySuggested();
    }
    private void AdjustTiming(string? action)
    {
        if(action is not ("TrackEarlier" or "TrackLater" or "TrackReset" or "GlobalEarlier" or "GlobalLater" or "GlobalReset")) return;
        var track=action.StartsWith("Track",StringComparison.Ordinal);
        if(track && !State.CanAdjustTrack) return;
        var current=track ? TrackOffset : GlobalOffset;
        var next=action.EndsWith("Reset",StringComparison.Ordinal) ? 0
            : Math.Clamp(current+(action.EndsWith("Earlier",StringComparison.Ordinal) ? TimingStep : -TimingStep),-5000,5000);
        if(track) TrackOffset=next; else GlobalOffset=next;
    }
    public double PaletteHue
    {
        get => _config.Current.Display.PaletteHue;
        set
        {
            if (!double.IsFinite(value)) return;
            // Keep the current editing surface open when a drag crosses a preset hue.
            _customPaletteExpanded = true;
            ApplyHue(value);
            Raise(nameof(CustomPaletteExpanded));
        }
    }
    private bool IsGeneratedPalette
    {
        get { var pair = OklchLyricPalette.Generate(PaletteHue); return MatchesPalette(pair.NormalColor, pair.HighlightColor); }
    }
    private PalettePreset? MatchingPreset => PalettePreset.All.FirstOrDefault(p => MatchesPalette(p.NormalColor, p.HighlightColor));
    private bool HasManualPalette => MatchingPreset is null && !IsGeneratedPalette;
    private bool MatchesPalette(string normal, string highlight) =>
        RecentColorHistory.Canonicalize(NormalColor) == normal && RecentColorHistory.Canonicalize(HighlightColor) == highlight;
    public string PaletteName => MatchingPreset?.Name ?? "自定义";
    public IEnumerable<PalettePreset> PalettePresets => PalettePreset.All.Select(p => p with { Selected = MatchesPalette(p.NormalColor, p.HighlightColor) });
    public bool CustomPaletteExpanded
    {
        get => _customPaletteExpanded || MatchingPreset is null || ManualColorsExpanded;
        set
        {
            // A custom selection must remain visible, including manual colors nested inside it.
            if (!value && MatchingPreset is null) { ReassertExpansion(nameof(CustomPaletteExpanded)); return; }
            _customPaletteExpanded = value;
            if (!value) { _manualColorsExpanded = false; Raise(nameof(ManualColorsExpanded)); }
            Raise();
        }
    }
    public bool ManualColorsExpanded
    {
        get => _manualColorsExpanded || HasManualPalette;
        set
        {
            if (!value && HasManualPalette) { ReassertExpansion(nameof(ManualColorsExpanded)); return; }
            _manualColorsExpanded = value;
            Raise();
            Raise(nameof(CustomPaletteExpanded));
        }
    }
    public ICommand RememberNormalColorCommand { get; }
    public ICommand RememberHighlightColorCommand { get; }
    public ICommand RememberShadowColorCommand { get; }
    public IReadOnlyList<string> RecentNormalColors => _config.Current.General.RecentNormalColors;
    public IReadOnlyList<string> RecentHighlightColors => _config.Current.General.RecentHighlightColors;
    public IReadOnlyList<string> RecentShadowColors => _config.Current.General.RecentShadowColors;
    public ICommand ResetColorsCommand { get; }
    public ICommand ResetAppearanceCommand { get; }
    public ICommand RetrySaveCommand { get; }
    public ICommand RefreshDataCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyDiagnosticsCommand { get; }
    public PlaybackPresentation State => _state;
    public Func<PlaybackPresentation> PreviewReader => () => PlaybackPresentation.From(_backend.ReadState());
    public AppConfig PreviewConfig => _config.Current;
    public bool IsBusy => _busy;
    public bool CanOperate => State.HasTrack && !IsBusy;
    public string Notice => _notice;
    public bool HasNotice => _notice.Length > 0;
    public bool NoticeError => _noticeError;
    public string SaveText => _saveText;
    public string SaveError => _saveError;
    public bool SaveFailed => _saveError.Length > 0;
    public string Version => "v" + typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3);
    public string CacheSize => _statistics is null ? "—" : FormatBytes(_statistics.LyricsCacheTotalBytes);
    public string CacheCount => _statistics?.LyricsCacheEntryCount.ToString() ?? "—";
    public string ManualCount => _statistics?.ManualMatchCount.ToString() ?? "—";
    public string OffsetCount => _statistics?.TrackSettingsCount.ToString() ?? "—";
    public string CacheRoot => _statistics?.CacheRoot ?? "尚未读取";
    public string Diagnostics => $"TaskbarLyrics {Version}\n歌曲：{State.Raw.Track?.DisplayName}\n播放：{State.PlaybackLabel}\n歌词：{State.StatusTitle} · {State.ModeLabel} · {State.SourceLabel}\n匹配：{State.MatchLabel}\nSongMid：{State.Raw.SongMid}\nGeneration：{State.Raw.Generation}\nCacheKey：{State.Raw.CacheKey}\n生效偏移：{State.EffectiveLabel}\n错误：{State.Raw.Error ?? "无"}\n配置：{_config.ConfigPath}\n缓存：{CacheRoot}\n\n{_backend.DiagnosticDetails}";
    public bool ShowOverlay { get => _config.Current.General.ShowOverlay; set => Change(ShowOverlay, value, c => c.General.ShowOverlay = value); }
    public bool AnimationsEnabled { get => !ReduceMotion; set => ReduceMotion = !value; }
    public bool ReduceMotion { get => _config.Current.General.ReduceMotion; set => Change(ReduceMotion, value, c => c.General.ReduceMotion = value); }
    public string Theme { get => _config.Current.General.Theme; set => Change(Theme, value, c => c.General.Theme = value); }
    public bool StartupEnabled { get => _backend.StartupEnabled; set { try { _backend.SetStartup(value); _config.Update(c => c.General.StartWithWindows = value); } catch (Exception ex) { Notify("自动启动设置失败：" + ex.Message, true); } Raise(); } }
    public double FontSize { get => _config.Current.Display.FontSize; set => Change(FontSize, value, c => c.Display.FontSize = value); }
    public string ChineseFont { get => _config.Current.Display.Fonts.Chinese; set => Change(ChineseFont, value, c => c.Display.Fonts.Chinese = value); }
    public string LatinFont { get => _config.Current.Display.Fonts.Latin; set => Change(LatinFont, value, c => c.Display.Fonts.Latin = value); }
    public string JapaneseFont { get => _config.Current.Display.Fonts.Japanese; set => Change(JapaneseFont, value, c => c.Display.Fonts.Japanese = value); }
    public string KoreanFont { get => _config.Current.Display.Fonts.Korean; set => Change(KoreanFont, value, c => c.Display.Fonts.Korean = value); }
    public string CyrillicFont { get => _config.Current.Display.Fonts.Cyrillic; set => Change(CyrillicFont, value, c => c.Display.Fonts.Cyrillic = value); }
    public string ArabicFont { get => _config.Current.Display.Fonts.Arabic; set => Change(ArabicFont, value, c => c.Display.Fonts.Arabic = value); }
    public string OtherFont { get => _config.Current.Display.Fonts.Other; set => Change(OtherFont, value, c => c.Display.Fonts.Other = value); }
    public string NormalColor { get => _config.Current.Display.NormalColor; set => ChangeLyricColor(NormalColor, value, c => c.Display.NormalColor = value); }
    public string HighlightColor { get => _config.Current.Display.HighlightColor; set => ChangeLyricColor(HighlightColor, value, c => c.Display.HighlightColor = value); }
    public string ShadowColor { get => _config.Current.Display.ShadowColor; set => Change(ShadowColor, value, c => c.Display.ShadowColor = value); }
    public bool ShadowEnabled { get => _config.Current.Display.ShadowEnabled; set => Change(ShadowEnabled, value, c => c.Display.ShadowEnabled = value); }
    public bool KaraokeEnabled { get => _config.Current.Karaoke.Enabled; set => Change(KaraokeEnabled, value, c => c.Karaoke.Enabled = value); }
    public bool AutoPan { get => _config.Current.Karaoke.AutoPanLongLyrics; set => Change(AutoPan, value, c => c.Karaoke.AutoPanLongLyrics = value); }
    public string Alignment { get => _config.Current.Display.TextAlignment; set => Change(Alignment, value, c => c.Display.TextAlignment = value); }
    public double VerticalOffset { get => _config.Current.Display.VerticalOffsetPx; set => Change(VerticalOffset, value, c => c.Display.VerticalOffsetPx = value); }
    public double WidthRatio { get => _config.Current.Overlay.WidthRatio; set => Change(WidthRatio, value, c => c.Overlay.WidthRatio = value); }
    public double FallbackWidthPercent { get => WidthRatio * 100; set => WidthRatio = value / 100; }
    public bool ShowFallbackWidth => _backend.Layout.UsesFallbackWidth;
    public bool AutoFit { get => _config.Current.Overlay.AutoFit; set => Change(AutoFit, value, c => c.Overlay.AutoFit = value); }
    public bool ShowManualLayout => !AutoFit;
    public double MinWidth { get => _config.Current.Overlay.MinWidthPx; set => Change(MinWidth, value, c => c.Overlay.MinWidthPx = (int)value); }
    public double MaxWidth { get => _config.Current.Overlay.MaxWidthPx; set => Change(MaxWidth, value, c => c.Overlay.MaxWidthPx = (int)value); }
    public double LeftMargin { get => _config.Current.Overlay.LeftMarginPx; set => Change(LeftMargin, value, c => c.Overlay.LeftMarginPx = (int)value); }
    public double RightMargin { get => _config.Current.Overlay.RightSafetyMarginPx; set => Change(RightMargin, value, c => c.Overlay.RightSafetyMarginPx = (int)value); }
    public double GlobalOffset { get => _config.Current.Lyrics.GlobalOffsetMs; set => Change(GlobalOffset, value, c => c.Lyrics.GlobalOffsetMs = (long)Math.Clamp(value, -5000, 5000)); }
    public double TrackOffset
    {
        get => State.Raw.TrackOffsetMs;
        set
        {
            var s = State.Raw;
            try { if (!_backend.SetTrackOffset((long)value, s.Generation, s.CacheKey)) Notify("歌曲已切换，请重新调整", true); }
            catch (Exception ex) { Notify("单曲偏移保存失败，已保留原值：" + ex.Message, true); }
            Refresh();
        }
    }
    private void Change<T>(T old, T value, Action<AppConfig> update) { if (!EqualityComparer<T>.Default.Equals(old, value)) _config.Update(update); }
    private void ChangeLyricColor(string old, string value, Action<AppConfig> update)
    {
        if (old == value) return;
        // Returning to a preset from a picker must not hide the picker mid-edit.
        _customPaletteExpanded = _manualColorsExpanded = true;
        _config.Update(update);
    }
    private void RememberColor(string? color, IReadOnlyList<string> history, Action<GeneralConfig, List<string>> save, string propertyName)
    {
        if (_disposed || RecentColorHistory.Canonicalize(color) is null) return;
        var updated = RecentColorHistory.Remember(history, color);
        if (history.SequenceEqual(updated)) return;
        _config.Update(c => save(c.General, updated));
        // A committed choice must survive restart even before the debounce timer fires.
        try { _config.SaveNow(); }
        catch (Exception ex) { Notify("最近颜色保存失败：" + ex.Message, true); }
        Raise(propertyName);
    }
    private void ApplyHue(double hue)
    {
        if (!double.IsFinite(hue)) return;
        hue = Math.Clamp(hue, 0, 360);
        var pair = OklchLyricPalette.Generate(hue);
        if (PaletteHue == hue && NormalColor == pair.NormalColor && HighlightColor == pair.HighlightColor) return;
        _config.Update(c => { c.Display.PaletteHue = hue; c.Display.NormalColor = pair.NormalColor; c.Display.HighlightColor = pair.HighlightColor; });
    }
    private void ApplyPreset(PalettePreset preset)
    {
        _customPaletteExpanded = CustomPaletteExpanded;
        _manualColorsExpanded = false;
        ApplyHue(preset.Hue);
        // Preserve the visible hue editor, including expansion derived from custom colors.
        Raise(nameof(CustomPaletteExpanded));
        Raise(nameof(ManualColorsExpanded));
    }
    private void ReassertExpansion(string propertyName)
    {
        Raise(propertyName);
        // WPF can ignore a source notification while its TwoWay setter is running.
        // Reassert after that transfer so a required section cannot appear collapsed.
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(DispatcherPriority.DataBind, () => { if (!_disposed) Raise(propertyName); });
    }
    public void Activate(SettingsPage page)
    {
        if (_disposed) return;
        page = SettingsNavigation.Normalize(page);
        Page = page; _active = true; _pageCts.Cancel(); _pageCts.Dispose(); _pageCts = new();
        Raise(nameof(Page));
        Refresh();
        if (page is SettingsPage.Overview or SettingsPage.Appearance or SettingsPage.Synchronization) _poll.Start(); else _poll.Stop();
        if (page == SettingsPage.General) _ = RefreshStatisticsAsync();
    }
    public void Deactivate() { if (_disposed) return; _active = false; _navigationLabel.Clear(); _poll.Stop(); _noticeTimer.Stop(); _pageCts.Cancel(); }
    private void BackendChanged() => Dispatch(() => { if (_active) Refresh(); });
    private void ConfigChanged(AppConfig _) => Dispatch(() => { if (_disposed) return; ThemeManager.Apply(_config.Current.General.Theme); Raise(""); Refresh(); });
    private void SaveChanged(ConfigSaveState s) => Dispatch(() =>
    {
        if (_disposed || s.Revision < _saveRevision) return;
        _saveRevision = s.Revision;
        _saveText = s.Phase switch { SavePhase.Saved => "已保存", SavePhase.Failed => "保存失败", _ => "保存中…" };
        _saveError = s.Phase == SavePhase.Failed ? s.Error ?? "无法写入配置" : "";
        Raise(nameof(SaveText)); Raise(nameof(SaveError)); Raise(nameof(SaveFailed));
    });
    public void Refresh()
    {
        if (_disposed) return;
        _state = PlaybackPresentation.From(_backend.ReadState());
        Raise(nameof(ShowFallbackWidth));
        Raise(nameof(State)); Raise(nameof(CanOperate)); Raise(nameof(TrackOffset)); Raise(nameof(GlobalOffset)); Raise(nameof(Diagnostics));
        Raise(nameof(ActualTimingEffect)); Raise(nameof(GlobalTimingSummary));
        RaisePlaybackControls();
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task RefreshStatisticsAsync()
    {
        if (_disposed || _pageCts.IsCancellationRequested) return;
        var token = _pageCts.Token;
        var requestVersion = ++_statisticsRequestVersion;
        try
        {
            var stats = await _backend.StatisticsAsync(token);
            if (token.IsCancellationRequested || _disposed || requestVersion != _statisticsRequestVersion) return;
            _statistics = stats;
            foreach (var n in new[] { nameof(CacheSize), nameof(CacheCount), nameof(ManualCount), nameof(OffsetCount), nameof(CacheRoot), nameof(Diagnostics) }) Raise(n);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !_disposed && requestVersion == _statisticsRequestVersion)
                Notify("读取统计失败：" + ex.Message, true);
        }
    }
    public async Task RunOperationAsync(SettingsOperation op)
    {
        if (_busy || _disposed) return;
        if (op is SettingsOperation.Reload or SettingsOperation.Redownload or SettingsOperation.ClearCurrentManual && !State.HasTrack) { Notify("请先播放一首歌曲。", true); return; }
        var expectedGeneration = State.Raw.Generation;
        var confirmation = op switch
        {
            SettingsOperation.ClearLyrics => ("清除歌词缓存", "删除下载的歌词文件，下次播放会重新获取。保留手动匹配、单曲校准和全局校准。"),
            SettingsOperation.ClearCurrentManual => ("清除手动匹配（当前歌曲）", "当前歌曲将恢复自动匹配；保留歌词缓存、单曲校准和全局校准。"),
            SettingsOperation.ClearAllManual => ("清除手动匹配（全部歌曲）", "全部歌曲将恢复自动匹配。此操作不能撤销；保留歌词缓存、单曲校准和全局校准。"),
            SettingsOperation.ClearOffsets => ("清除单曲校准（全部歌曲）", "清除全部歌曲的单曲校准；保留全局校准、手动匹配和歌词缓存。"),
            _ => ("", "")
        };
        if (confirmation.Item1.Length > 0 && Confirm?.Invoke(confirmation.Item1, confirmation.Item2) != true) return;
        var token = _pageCts.Token; var trackKey = State.Raw.Track?.TrackKey;
        _busy = true; Raise(nameof(IsBusy)); Raise(nameof(CanOperate));
        Notify("正在处理…");
        try
        {
            await _backend.ExecuteAsync(op, token, expectedGeneration);
            if (token.IsCancellationRequested || _disposed) return;
            Refresh();
            if (op is SettingsOperation.Reload or SettingsOperation.Redownload)
            {
                if (trackKey != State.Raw.Track?.TrackKey) return;
                Notify(State.StatusTitle, State.Health is LyricsHealth.Error or LyricsHealth.NeedsRematch or LyricsHealth.NoLyrics);
            }
            else { Notify("已完成"); await RefreshStatisticsAsync(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Notify("操作失败：" + ex.Message, true); }
        finally { _busy = false; Raise(nameof(IsBusy)); Raise(nameof(CanOperate)); CommandManager.InvalidateRequerySuggested(); }
    }
    public void Notify(string text, bool error = false)
    {
        _notice = text; _noticeError = error; Raise(nameof(Notice)); Raise(nameof(NoticeError)); Raise(nameof(HasNotice));
        _noticeTimer.Stop(); if (!error && _active) _noticeTimer.Start();
    }
    private void Dispatch(Action action) { if (_dispatcher.CheckAccess()) action(); else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action); }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string FormatBytes(long b) => b >= 1048576 ? $"{b / 1048576.0:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0.0} KB" : $"{b} B";
    public void Dispose()
    {
        if (_disposed) return; Deactivate(); _disposed = true; _pageCts.Dispose();
        _config.ConfigChanged -= ConfigChanged; _config.SaveStateChanged -= SaveChanged; _backend.StateChanged -= BackendChanged;
    }
}
