// Overlay positioning adapted from TaskbarOverlayProbe (validated).
// MVP 1.1: karaoke control, transparent bg, dynamic width from config.

using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using System.Windows.Media.Animation;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.Windows;
// OverlayConfig used for MaxTaskbarWidthFraction

namespace TaskbarLyrics.App;

public partial class MainWindow : Window
{
    private const bool EnableMousePassthrough = true;
    // Default poll until Coordinator reports PreferredPollIntervalMs (Playing 75 / Scrub 40 / Paused 200).
    private const int CalibrateIntervalMs = 200;
    private const int RepositionIntervalMs = 250;
    /// <summary>How often to reassert TOPMOST so Start/Search/taskbar clicks cannot bury us.</summary>
    private const int ZOrderIntervalMs = 250;
    /// <summary>Every N z-order ticks, do the NOTOPMOST→TOPMOST toggle (stronger refresh).</summary>
    private const int ForceTopmostEveryNTicks = 8;

    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _calibrateTimer;
    private readonly DispatcherTimer _repositionTimer;
    private readonly DispatcherTimer _zOrderTimer;
    private readonly DispatcherTimer _layoutEventTimer;
    private readonly DispatcherTimer _placementTimer;
    private readonly TaskbarLayoutObserver _layoutObserver;
    private int _layoutEventQueued;
    private bool _placementClosed;
    private OverlayTargetRect _displayedTarget, _placementTo;
    private bool _hasDisplayedTarget;
    private long _placementStarted, _layoutWatchUntil;
    private enum PlacementPhase { Stable, Exiting, Waiting, Entering }
    private PlacementPhase _placementPhase;
    private bool _pendingPlacement;
    private double _exitFromOffset, _exitFromOpacity, _placementTravel;
    private TaskbarLyricSide _exitSide;
    private readonly TaskbarLayoutStability _layoutStability = new();
    private readonly Func<long> _layoutClock;
    private Task _layoutReadTask = Task.CompletedTask;
    private bool _layoutReadPending, _layoutForcePending;
    private string _layoutReason = "layout";
    private long _layoutRevision;
    private TaskbarLyricSide _placementSide;
    private readonly System.Windows.Media.TranslateTransform _placementTranslation = new();
    private TaskbarInterval _placementSafe;


    private IntPtr _hwnd = IntPtr.Zero;
    private HwndSource? _hwndSource;
    private uint _taskbarCreatedMsg;
    private bool _sourceHooked;
    private bool _hasLastPlacement;
    private NativeMethods.RECT _lastTaskbarRect;
    private OverlayTargetRect _lastTarget;
    private AppConfig _config = AppConfig.CreateDefault();
    private bool _overlayVisible = true;
    /// <summary>True while a fullscreen app covers the taskbar monitor — hide TOPMOST lyrics.</summary>
    private bool _fullscreenSuppressed;
    private bool _spaceSuppressed;
    private TaskbarLyricSide _lyricSide;
    private int _zOrderTick;
    private IntPtr _foregroundHook;
    // Keep delegate alive for SetWinEventHook (GC would otherwise collect it).
    private NativeMethods.WinEventDelegate? _foregroundEventProc;

    private readonly Func<NativeMethods.RECT, int, int, TaskbarFreeRegionLocator.FreeRegionResult> _measureRegion;
    private readonly Func<IntPtr, IntPtr, bool> _shouldSuppressOverlay;
    public MainWindow() : this(TaskbarFreeRegionLocator.Measure) { }
    public MainWindow(Func<NativeMethods.RECT, int, int, TaskbarFreeRegionLocator.FreeRegionResult> measureRegion, Func<long>? clock = null, Func<IntPtr, IntPtr, bool>? shouldSuppressOverlay = null)
    {
        _measureRegion = measureRegion;
        _shouldSuppressOverlay = shouldSuppressOverlay ?? FullscreenDetector.ShouldSuppressOverlay;
        _layoutClock = clock ?? (() => Environment.TickCount64);
        InitializeComponent();
        Karaoke.RenderTransform = _placementTranslation;
        Karaoke.Opacity = 0;
        _layoutEventTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(32) };
        _layoutEventTimer.Tick += (_, _) =>
        {
            if (_layoutClock() >= _layoutWatchUntil) _layoutEventTimer.Stop();
            try { RepositionOverlay(false, "taskbar-layout-event"); }
            catch (Exception ex) { App.Logger?.Error("Taskbar layout event", ex); }
        };
        _placementTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _placementTimer.Tick += (_, _) => TickPlacement();
        _layoutObserver = new TaskbarLayoutObserver(QueueTaskbarLayout);


        if (App.Config is not null)
        {
            _config = App.Config.Current;
            _overlayVisible = _config.General.ShowOverlay;
            Karaoke.ApplyConfig(_config);
            App.Config.ConfigChanged += OnConfigChanged;
        }

        var hz = Math.Clamp(_config.Karaoke.RenderHz, 15, 60);
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / hz)
        };
        _renderTimer.Tick += OnRenderTick;

        _calibrateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(CalibrateIntervalMs)
        };
        _calibrateTimer.Tick += OnCalibrateTick;

        _repositionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RepositionIntervalMs)
        };
        _repositionTimer.Tick += OnRepositionTick;

        // Dedicated high-frequency z-order keeper. Geometry can stay put, but
        // Shell Start/Search/Explorer button clicks demote TOPMOST windows until desktop click.
        _zOrderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ZOrderIntervalMs)
        };
        _zOrderTimer.Tick += OnZOrderTick;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    public void SetOverlayVisible(bool visible)
    {
        _overlayVisible = visible;
        _layoutRevision++;
        if (visible)
        {
            // Re-evaluate fullscreen; may still stay hidden under video/game.
            RefreshFullscreenSuppression(force: true);
            if (!_fullscreenSuppressed)
            {
                ShowOverlayHost("show-overlay");
            }

            if (!_zOrderTimer.IsEnabled)
            {
                _zOrderTimer.Start();
            }

            UpdateKaraoke();
        }
        else
        {
            _zOrderTimer.Stop();
            HideOverlayHost();
        }
    }

    /// <summary>Settings diagnostics: re-run Start-button width measurement now.</summary>
    public void ForceRelayoutForDiagnostics()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        RepositionOverlay(force: true, reason: "settings-diagnostics");
    }

    public Task ForceRelayoutForDiagnosticsAsync() => RequestLayoutAsync(true, "settings-diagnostics");

    private void OnConfigChanged(AppConfig cfg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _layoutRevision++;
                var oldOverlay = _config.Overlay;
                if (oldOverlay.AutoFit != cfg.Overlay.AutoFit || _config.Display.FontSize != cfg.Display.FontSize
                    || oldOverlay.LeftMarginPx != cfg.Overlay.LeftMarginPx || oldOverlay.RightSafetyMarginPx != cfg.Overlay.RightSafetyMarginPx
                    || oldOverlay.MinWidthPx != cfg.Overlay.MinWidthPx || oldOverlay.MaxWidthPx != cfg.Overlay.MaxWidthPx
                    || oldOverlay.WidthRatio != cfg.Overlay.WidthRatio) _layoutStability.Reset();
                _config = cfg;
                Karaoke.ApplyConfig(cfg);
                var hz = Math.Clamp(cfg.Karaoke.RenderHz, 15, 60);
                _renderTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / hz);

                // Only toggle visibility when the flag actually changes — avoid Hide/Show storms.
                if (cfg.General.ShowOverlay != _overlayVisible)
                {
                    SetOverlayVisible(cfg.General.ShowOverlay);
                }

                if (_overlayVisible)
                {
                    RepositionOverlay(force: true, reason: "config-reload");
                    UpdateKaraoke();
                }
            }
            catch (Exception ex)
            {
                App.Logger?.Error("MainWindow.OnConfigChanged", ex);
            }
        }, DispatcherPriority.Background);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            // Avoid WPF owner relationship that can hide us with other app windows.
            helper.Owner = IntPtr.Zero;
            _hwnd = helper.EnsureHandle();
            App.Logger?.Info($"HWND=0x{_hwnd.ToInt64():X}");
            ApplyExtendedStyles(_hwnd);
            AttachMessageHook(_hwnd);
            RepositionOverlay(force: true, reason: "SourceInitialized");
            KeepOnTop(forceToggle: true, reason: "SourceInitialized");
        }
        catch (Exception ex)
        {
            App.Logger?.Error("OnSourceInitialized", ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _renderTimer.Start();
            _calibrateTimer.Start();
            _repositionTimer.Start();
            if (_overlayVisible)
            {
                _zOrderTimer.Start();
            }

            InstallForegroundHook();
            if (_hwnd != IntPtr.Zero)
            {
                RepositionOverlay(force: true, reason: "Loaded");
                KeepOnTop(forceToggle: true, reason: "Loaded");
            }

            UpdateKaraoke();
        }
        catch (Exception ex)
        {
            App.Logger?.Error("OnLoaded", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            if (App.Config is not null)
            {
                App.Config.ConfigChanged -= OnConfigChanged;
            }

            _renderTimer.Stop();
            _calibrateTimer.Stop();
            _repositionTimer.Stop();
            _zOrderTimer.Stop();
            _placementClosed = true;
            _layoutRevision++;
            _layoutEventTimer.Stop(); _placementTimer.Stop(); _layoutObserver.Dispose();
            UninstallForegroundHook();
            if (_hwndSource is not null && _sourceHooked)
            {
                _hwndSource.RemoveHook(WndProc);
                _sourceHooked = false;
            }
        }
        catch (Exception ex)
        {
            App.Logger?.Error("OnClosed", ex);
        }
    }

    private void OnRenderTick(object? sender, EventArgs e) => UpdateKaraoke();

    private void OnCalibrateTick(object? sender, EventArgs e)
    {
        try
        {
            var coord = App.Coordinator;
            if (coord is not null)
            {
                // Playing 75ms / Scrubbing 40ms / Paused 200ms — follow Coordinator policy.
                var want = Math.Clamp(coord.PreferredPollIntervalMs, 40, 500);
                var cur = (int)_calibrateTimer.Interval.TotalMilliseconds;
                if (want != cur)
                {
                    _calibrateTimer.Interval = TimeSpan.FromMilliseconds(want);
                }

                coord.PollCalibrate();
            }
        }
        catch (Exception ex)
        {
            App.Logger?.Error("Calibrate", ex);
        }
    }

    private void OnRepositionTick(object? sender, EventArgs e)
    {
        try
        {
            if (!_overlayVisible)
            {
                return;
            }

            // Secondary fullscreen check (in case foreground hook is missed).
            RefreshFullscreenSuppression(force: false);
            if (_fullscreenSuppressed)
            {
                return;
            }

            RepositionOverlay(force: false, reason: "layout-poll");
        }
        catch (Exception ex)
        {
            App.Logger?.Error("RepositionTick", ex);
        }
    }

    private void OnZOrderTick(object? sender, EventArgs e)
    {
        try
        {
            if (!_overlayVisible || _hwnd == IntPtr.Zero)
            {
                return;
            }

            // Fullscreen video/games: must not reassert TOPMOST over the player.
            RefreshFullscreenSuppression(force: false);
            if (_fullscreenSuppressed)
            {
                return;
            }

            // Soft reassert every tick; full toggle periodically for stubborn shell demotions.
            _zOrderTick++;
            var force = (_zOrderTick % ForceTopmostEveryNTicks) == 0;
            KeepOnTop(forceToggle: force, reason: force ? "z-timer-force" : "z-timer");
        }
        catch (Exception ex)
        {
            App.Logger?.Error("ZOrderTick", ex);
        }
    }

    /// <summary>
    /// Hide TOPMOST lyrics while a fullscreen app covers the taskbar monitor.
    /// Called from z-order timer (~250ms) and foreground changes.
    /// </summary>
    private void RefreshFullscreenSuppression(bool force)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        bool shouldSuppress = _overlayVisible
            && _shouldSuppressOverlay(_hwnd, _hwnd);

        if (!force && shouldSuppress == _fullscreenSuppressed)
        {
            return;
        }

        if (shouldSuppress)
        {
            if (!_fullscreenSuppressed)
            {
                App.Logger?.Info("Fullscreen detected — suppressing taskbar lyrics overlay");
            }

            _fullscreenSuppressed = true;
            _layoutRevision++;
            HideOverlayHost();
        }
        else
        {
            bool wasSuppressed = _fullscreenSuppressed;
            _fullscreenSuppressed = false;
            if (_overlayVisible && wasSuppressed)
            {
                App.Logger?.Info("Fullscreen ended — restoring taskbar lyrics overlay");
                ShowOverlayHost("fullscreen-exit");
            }
        }
    }

    private void HideOverlayHost()
    {
        _placementTimer.Stop();
        _placementPhase = PlacementPhase.Stable;
        _pendingPlacement = false;
        _placementTranslation.X = 0; Karaoke.Opacity = 0;
        _hasDisplayedTarget = false;
        BeginAnimation(OpacityProperty, null);
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            }

            if (IsVisible)
            {
                Hide();
            }
        }
        catch
        {
            // ignore
        }
    }

    private void ShowOverlayHost(string reason)
    {
        if (!_overlayVisible || _fullscreenSuppressed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            RepositionOverlay(force: true, reason: reason);
            // The asynchronous measurement shows the host only after confirming safe geometry.
        }
        catch (Exception ex)
        {
            App.Logger?.Error("ShowOverlayHost", ex);
        }
    }

    /// <summary>
    /// Keep overlay above Shell UI that steals the topmost band
    /// (Start / Search / taskbar pinned apps). Desktop click used to "fix" this
    /// by reshuffling z-order; we now restore it ourselves.
    /// </summary>
    private void KeepOnTop(bool forceToggle, string reason)
    {
        if (_hwnd == IntPtr.Zero || !_overlayVisible || _fullscreenSuppressed || _spaceSuppressed || !_hasDisplayedTarget)
        {
            return;
        }

        // If shell hid us, restore visibility + last geometry (do NOT do this every force tick).
        if (!NativeMethods.IsWindowVisible(_hwnd) || !IsVisible)
        {
            try
            {
                if (!IsVisible)
                {
                    Show();
                }

                if (_hasLastPlacement)
                {
                    OverlayPositioner.Apply(_hwnd, _displayedTarget, out _, reason + "-reshow");
                }
            }
            catch
            {
                // ignore
            }
        }

        if (forceToggle)
        {
            OverlayPositioner.ForceTopmostRefresh(_hwnd, out _, reason);
        }
        else
        {
            OverlayPositioner.ReassertTopmost(_hwnd, out _, reason, log: false);
        }
    }

    private void InstallForegroundHook()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Must pin the delegate so GC does not collect it while the hook is active.
            _foregroundEventProc = OnForegroundWinEvent;
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

            if (_foregroundHook == IntPtr.Zero)
            {
                App.Logger?.Warn("SetWinEventHook(FOREGROUND) failed");
            }
            else
            {
                App.Logger?.Info("Foreground z-order hook installed");
            }
        }
        catch (Exception ex)
        {
            App.Logger?.Error("InstallForegroundHook", ex);
        }
    }

    private void UninstallForegroundHook()
    {
        try
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            _foregroundEventProc = null;
        }
        catch
        {
            // ignore
        }
    }

    private void OnForegroundWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // Called on a hook thread — bounce to UI dispatcher.
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_overlayVisible || _hwnd == IntPtr.Zero)
                {
                    return;
                }

                // Entering/leaving fullscreen often arrives as a foreground change.
                RefreshFullscreenSuppression(force: true);
                if (!_fullscreenSuppressed)
                {
                    KeepOnTop(forceToggle: true, reason: "foreground-change");
                }
            }, DispatcherPriority.Send);
        }
        catch
        {
            // ignore
        }
    }

    private long _visualGeneration = -1, _visualPosition;
    private int _visualLine = -1;

    private void UpdateKaraoke()
    {
        try
        {
            if (!_overlayVisible || _fullscreenSuppressed)
            {
                return;
            }

            var coord = App.Coordinator;
            if (coord is null)
            {
                Karaoke.UpdateState(null);
                _visualLine = -1; Karaoke.BeginAnimation(OpacityProperty, null);
                return;
            }

            var state = coord.GetUiState();
            if (!state.SessionConnected || string.IsNullOrEmpty(state.DisplayText))
            {
                Karaoke.UpdateState(null);
                return;
            }

            var animate = LineTransitionPolicy.ShouldAnimate(_visualGeneration, state.Generation, _visualLine, state.Lyric.LineIndex,
                _visualPosition, state.Lyric.PositionMs, coord.IsScrubbing, state.StaticLineOnly,
                _config.General.ReduceMotion || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast);
            if (_visualGeneration != state.Generation || _visualLine != state.Lyric.LineIndex || coord.IsScrubbing || _config.General.ReduceMotion)
                Karaoke.BeginAnimation(OpacityProperty, null);
            if (animate) Karaoke.BeginAnimation(OpacityProperty, new DoubleAnimation(.85, 1, TimeSpan.FromMilliseconds(80)) { FillBehavior = FillBehavior.Stop });
            _visualGeneration = state.Generation; _visualLine = state.Lyric.LineIndex; _visualPosition = state.Lyric.PositionMs;
            Karaoke.UpdateState(new KaraokeRenderState(
                Text: state.DisplayText,
                Words: state.CurrentLineWords,
                CurrentWordIndex: state.WordIndex,
                CurrentWordProgress: state.WordProgress,
                KaraokeEnabled: _config.Karaoke.Enabled,
                StaticLineOnly: state.StaticLineOnly));
        }
        catch (Exception ex)
        {
            App.Logger?.Error("UpdateKaraoke", ex);
        }
    }

    private void ApplyExtendedStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            IntPtr exStylePtr = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            int exStyle = exStylePtr.ToInt32();
            exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            exStyle |= NativeMethods.WS_EX_NOACTIVATE;
            exStyle |= NativeMethods.WS_EX_LAYERED;
            if (EnableMousePassthrough)
            {
                exStyle |= NativeMethods.WS_EX_TRANSPARENT;
            }

            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_FRAMECHANGED);

            App.Logger?.Info($"Extended styles applied: 0x{exStyle:X8}");
        }
        catch (Exception ex)
        {
            App.Logger?.Error("ApplyExtendedStyles", ex);
        }
    }

    private void AttachMessageHook(IntPtr hwnd)
    {
        if (_sourceHooked)
        {
            return;
        }

        _hwndSource = HwndSource.FromHwnd(hwnd);
        if (_hwndSource is null)
        {
            return;
        }

        _hwndSource.AddHook(WndProc);
        _sourceHooked = true;
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        App.Logger?.Info($"TaskbarCreated msg={_taskbarCreatedMsg}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (msg == NativeMethods.WM_NCHITTEST && EnableMousePassthrough)
            {
                handled = true;
                return new IntPtr(NativeMethods.HTTRANSPARENT);
            }

            if (msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
            }

            if (msg == NativeMethods.WM_DPICHANGED || msg == NativeMethods.WM_DISPLAYCHANGE)
            {
                _layoutRevision++;
                _layoutStability.Reset();
                Dispatcher.BeginInvoke(() =>
                {
                    RepositionOverlay(true, "DPI/Display");
                    KeepOnTop(forceToggle: true, reason: "DPI/Display");
                }, DispatcherPriority.ApplicationIdle);
                return IntPtr.Zero;
            }

            if (msg == NativeMethods.WM_SETTINGCHANGE)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    RepositionOverlay(false, "SETTINGCHANGE");
                    KeepOnTop(forceToggle: true, reason: "SETTINGCHANGE");
                }, DispatcherPriority.ApplicationIdle);
                return IntPtr.Zero;
            }

            // Any app (including Explorer Start/Search) becoming active demotes us.
            if (msg == NativeMethods.WM_ACTIVATEAPP && wParam == IntPtr.Zero)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    RefreshFullscreenSuppression(force: true);
                    if (!_fullscreenSuppressed)
                    {
                        KeepOnTop(forceToggle: true, reason: "ACTIVATEAPP");
                    }
                }, DispatcherPriority.Send);
                return IntPtr.Zero;
            }

            if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
            {
                _layoutRevision++;
                _layoutStability.Reset();
                App.Logger?.Info("TaskbarCreated — recover overlay");
                Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await Task.Delay(500).ConfigureAwait(true);
                        ApplyExtendedStyles(_hwnd);
                        RepositionOverlay(true, "TaskbarCreated");
                        KeepOnTop(forceToggle: true, reason: "TaskbarCreated");
                    }
                    catch (Exception ex)
                    {
                        App.Logger?.Error("TaskbarCreated recovery", ex);
                    }
                }, DispatcherPriority.Normal);
                handled = true;
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            App.Logger?.Error("WndProc", ex);
        }

        return IntPtr.Zero;
    }

    private void QueueTaskbarLayout()
    {
        if (_placementClosed || Dispatcher.HasShutdownStarted) return;
        if (Interlocked.Exchange(ref _layoutEventQueued, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _layoutEventQueued, 0);
            if (_placementClosed) return;
            _layoutWatchUntil = _layoutClock() + 600;
            if (!_layoutEventTimer.IsEnabled)
            {
                try { RepositionOverlay(false, "taskbar-layout-event"); }
                catch (Exception ex) { App.Logger?.Error("Taskbar layout event", ex); }
                _layoutEventTimer.Start();
            }
        }, DispatcherPriority.Render);
    }
    private void BeginLayoutTransition(NativeMethods.RECT bar, TaskbarFreeRegionLocator.FreeRegionResult free)
    {
        _pendingPlacement = false;
        if (!_hasDisplayedTarget) return;
        if (free.Success && _placementPhase is PlacementPhase.Stable or PlacementPhase.Entering)
        {
            var provisional = TaskbarRegionLayout.Select(free.Left, free.Right, EffectiveOverlay(bar.Width), _lyricSide, Math.Max(24, bar.Height / 2));
            if (provisional.Side == _lyricSide && provisional.Left == _displayedTarget.Left && provisional.Width == _displayedTarget.Width
                && bar.Top == _displayedTarget.Top && bar.Height == _displayedTarget.Height) return;
        }
        // Freeze native geometry and text layout until a whole, stable measurement is confirmed.
        if (_placementPhase is PlacementPhase.Stable or PlacementPhase.Entering)
        {
            _exitSide = _lyricSide;
            _exitFromOffset = _placementTranslation.X;
            _exitFromOpacity = Karaoke.Opacity;
            _placementTravel = Math.Max(OverlayPlacementMotion.Travel, Karaoke.ActualWidth + 16);
            _placementPhase = PlacementPhase.Exiting;
            _placementStarted = _layoutClock();
            _placementTimer.Start();
            App.Logger?.Info($"Overlay transition: exit {_exitSide}; waiting for stable taskbar geometry");
        }
        // Safety can shrink immediately, without changing the window width or text alignment.
        var safe = free.Success
            ? (_exitSide == TaskbarLyricSide.Right ? free.Right : free.Left)
            : new TaskbarInterval(_displayedTarget.Left, _displayedTarget.Left);
        ClipPlacement(_displayedTarget, safe);
    }

    private void CommitPlacement(OverlayTargetRect target, TaskbarInterval safe, TaskbarLyricSide side)
    {
        _placementTo = target;
        _placementSafe = safe;
        _placementSide = side;
        _pendingPlacement = true;
        if (_placementPhase == PlacementPhase.Exiting && Motion.Allowed(this)) return;
        BeginPlacementEntry();
    }

    private void BeginPlacementEntry()
    {
        if (!_pendingPlacement) return;
        _pendingPlacement = false;
        _placementPhase = PlacementPhase.Entering;
        _placementStarted = _layoutClock();
        Karaoke.Opacity = 0;
        Karaoke.PlacementAlignment = _placementSide == TaskbarLyricSide.Right ? "Right" : null;
        _placementTranslation.X = (_placementSide == TaskbarLyricSide.Right ? 1 : -1) * OverlayPlacementMotion.Travel;
        if (!IsVisible) Show();
        ApplyPlacementFrame(_placementTo);
        _placementTravel = Math.Max(OverlayPlacementMotion.Travel, Karaoke.ActualWidth + 16);
        _placementTranslation.X = (_placementSide == TaskbarLyricSide.Right ? 1 : -1) * _placementTravel;
        if (!Motion.Allowed(this) || !_hasLastPlacement)
        {
            _placementTranslation.X = 0;
            Karaoke.Opacity = 1;
            _placementPhase = PlacementPhase.Stable;
            _placementTimer.Stop();
        }
        else _placementTimer.Start();
    }

    private void TickPlacement()
    {
        if (_placementClosed || !_overlayVisible || _fullscreenSuppressed || _spaceSuppressed)
        { _placementTimer.Stop(); return; }
        var elapsed = _layoutClock() - _placementStarted;
        var animated = Motion.Allowed(this);
        if (_placementPhase == PlacementPhase.Exiting)
        {
            var t = animated ? Math.Clamp(elapsed / (double)OverlayPlacementMotion.ExitMs, 0, 1) : 1;
            var outward = _exitSide == TaskbarLyricSide.Right ? 1 : -1;
            _placementTranslation.X = _exitFromOffset + outward * _placementTravel * t * t;
            Karaoke.Opacity = _exitFromOpacity * (1 - t);
            if (t < 1) return;
            _placementPhase = PlacementPhase.Waiting;
            _placementTimer.Stop();
            BeginPlacementEntry();
        }
        else if (_placementPhase == PlacementPhase.Entering)
        {
            var t = animated ? Math.Clamp(elapsed / (double)OverlayPlacementMotion.EnterMs, 0, 1) : 1;
            var frame = OverlayPlacementMotion.SideFrame(_placementSide, true, t, _placementTravel);
            _placementTranslation.X = frame.Offset;
            Karaoke.Opacity = frame.Opacity;
            if (t >= 1) { _placementPhase = PlacementPhase.Stable; _placementTimer.Stop(); }
        }
        else _placementTimer.Stop();
    }

    private void ClipPlacement(OverlayTargetRect frame, TaskbarInterval safe)
    {
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;
        var visible = OverlayPlacementMotion.VisibleBand(frame, safe);
        PlacementHost.Clip = new System.Windows.Media.RectangleGeometry(new Rect(
            (visible.Left - frame.Left) / scale, 0, visible.Width / scale, frame.Height / scale));
    }

    private void ApplyPlacementFrame(OverlayTargetRect frame)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Width = frame.Width / dpi.DpiScaleX;
        Height = frame.Height / dpi.DpiScaleY;
        ClipPlacement(frame, _placementSafe);
        // Complete the WPF size change before the final native position is applied.
        UpdateLayout();
        OverlayPositioner.Apply(_hwnd, frame, out _, "stable-layout", log: false);
        _displayedTarget = frame;
        _hasDisplayedTarget = true;
    }

    private void RepositionOverlay(bool force, string reason) => _ = RequestLayoutAsync(force, reason);

    private Task RequestLayoutAsync(bool force, string reason)
    {
        if (_placementClosed || _hwnd == IntPtr.Zero || !_overlayVisible || _fullscreenSuppressed) return Task.CompletedTask;
        _layoutReadPending = true;
        _layoutForcePending |= force;
        _layoutReason = reason;
        if (_layoutReadTask.IsCompleted) _layoutReadTask = ReadLayoutAsync();
        return _layoutReadTask;
    }

    private async Task ReadLayoutAsync()
    {
        // At most one UIA read is in flight; incoming requests are coalesced, never queued per frame.
        while (_layoutReadPending && !_placementClosed)
        {
            _layoutReadPending = false;
            var force = _layoutForcePending;
            _layoutForcePending = false;
            var reason = _layoutReason;
            var revision = _layoutRevision;
            var effective = EffectiveOverlay(0);
            var leftMargin = effective.LeftMarginPx;
            var rightMargin = effective.RightSafetyMarginPx;
            try
            {
                var result = await Task.Run(() =>
                {
                    var location = TaskbarLocator.LocatePrimaryTaskbar();
                    var free = _measureRegion(location.Rect, leftMargin, rightMargin);
                    return (location, free);
                });
                if (_placementClosed || revision != _layoutRevision || !_overlayVisible || _fullscreenSuppressed) continue;
                _layoutObserver.EnsureAttached();
                if (result.location.Rect.Width <= 0) continue;
                if (!_layoutStability.Observe(result.location.Rect, result.free, _layoutClock()))
                {
                    BeginLayoutTransition(result.location.Rect, result.free);
                    _layoutWatchUntil = _layoutClock() + 600;
                    if (!_layoutEventTimer.IsEnabled) _layoutEventTimer.Start();
                    continue;
                }
                ApplyStableLayout(result.location.Rect, result.free, force, reason);
            }
            catch (Exception ex) { App.Logger?.Error("Background taskbar layout", ex); }
        }
    }

    private OverlayConfig EffectiveOverlay(int taskbarWidth)
    {
        var taskbar = NativeMethods.FindWindow(TaskbarLocator.PrimaryTaskbarClassName, null);
        var dpi = taskbar == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(taskbar);
        var scale = dpi > 0 ? dpi / 96.0 : System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        return TaskbarRegionLayout.Resolve(_config.Overlay, _config.Display.FontSize, scale, taskbarWidth);
    }

    private void ApplyStableLayout(NativeMethods.RECT taskbar, TaskbarFreeRegionLocator.FreeRegionResult free,
        bool force, string reason)
    {
        var o = EffectiveOverlay(taskbar.Width);
        TaskbarRegionChoice choice;
        if (free.Success)
            choice = TaskbarRegionLayout.Select(free.Left, free.Right, o, _lyricSide, Math.Max(24, taskbar.Height / 2));
        else
            choice = new(TaskbarLyricSide.Manual, taskbar.Left + o.LeftMarginPx,
                Math.Min(Math.Max(0, taskbar.Width - o.LeftMarginPx - o.RightSafetyMarginPx), ConfigService.ComputeOverlayWidthPx(taskbar.Width, o)));
        var mode = choice.Side switch
        {
            TaskbarLyricSide.Left => "左侧空白",
            TaskbarLyricSide.Right => "右侧空白",
            TaskbarLyricSide.Hidden => "空间不足，暂时隐藏",
            _ => "手动设置宽度"
        };
        var widthPx = choice.Width;
        var target = new OverlayTargetRect(choice.Left, taskbar.Top, Math.Max(1, widthPx), taskbar.Height);
        var safetyBoundary = choice.Side == TaskbarLyricSide.Right ? free.Right.Right
            : choice.Side == TaskbarLyricSide.Left ? free.Left.Right : target.Right;
        OverlayLayoutDiagnostics.Update(new OverlayLayoutSnapshot(
            Mode: mode,
            ModeDetail: free.Success ? "优先左侧；左侧不足时使用右侧；两侧不足时隐藏。" : "无法测量，使用手动宽度。",
            TaskbarWidthPx: taskbar.Width,
            TaskbarLeftPx: taskbar.Left,
            StartLeftPx: free.Success ? free.StartLeftPx : null,
            FreeBandPx: free.Success ? Math.Max(free.Left.Width, free.Right.Width) : null,
            RequestedWidthPx: widthPx,
            FinalWidthPx: widthPx,
            SafetyRightPx: safetyBoundary,
            OverlayLeftPx: choice.Left,
            OverlayRightPx: choice.Left + widthPx,
            Source: free.Source,
            UpdatedAt: DateTimeOffset.Now));
        if (!choice.Visible)
        {
            if (!_spaceSuppressed) App.Logger?.Info($"Overlay hidden: left={free.Left.Width}, right={free.Right.Width}, minimum={o.MinWidthPx}");
            _spaceSuppressed = true;
            HideOverlayHost();
            return;
        }
        var recovered = _spaceSuppressed;
        _spaceSuppressed = false;
        var oldSide = _lyricSide;
        var sideChanged = oldSide != choice.Side;
        _lyricSide = choice.Side;

        if (recovered) App.Logger?.Info($"Overlay space recovered: {mode}");
        force |= recovered || sideChanged;

        bool taskbarChanged = !_hasLastPlacement
            || taskbar.Left != _lastTaskbarRect.Left
            || taskbar.Top != _lastTaskbarRect.Top
            || taskbar.Right != _lastTaskbarRect.Right
            || taskbar.Bottom != _lastTaskbarRect.Bottom;

        bool targetChanged = !_hasLastPlacement
            || target.Left != _lastTarget.Left
            || target.Top != _lastTarget.Top
            || target.Width != _lastTarget.Width
            || target.Height != _lastTarget.Height;

        if (force || taskbarChanged || targetChanged || !_hasDisplayedTarget || _placementPhase == PlacementPhase.Waiting || _placementPhase == PlacementPhase.Exiting)
        {
            if (force || taskbarChanged)
            {
                App.Logger?.Info(
                    $"Overlay width mode={mode} taskbar={taskbar.Width} freeBand={(free.Success ? free.AvailableWidthPx.ToString() : "n/a")} " +
                    $"source={free.Source} startLeft={(free.Success ? free.StartLeftPx.ToString() : "n/a")} " +
                    $"requested={widthPx} final={target.Width} safetyRight={safetyBoundary} reason={reason}");
            }

            var safe = choice.Side == TaskbarLyricSide.Left ? free.Left
                : choice.Side == TaskbarLyricSide.Right ? free.Right : new TaskbarInterval(target.Left, target.Right);
            if (targetChanged || sideChanged || !_hasDisplayedTarget || _placementPhase is PlacementPhase.Waiting or PlacementPhase.Exiting)
                CommitPlacement(target, safe, choice.Side);

            if (force || taskbarChanged)
            {
                OverlayPositioner.LogPlacementDiagnostics(_hwnd, taskbar, target);
            }

            _lastTaskbarRect = taskbar;
            _lastTarget = target;
            _hasLastPlacement = true;
        }
        else
        {
            // Geometry unchanged: keep above siblings / reassert visibility.
            OverlayPositioner.ReassertTopmost(_hwnd, out _, reason + "-z", log: false);
        }
    }
}
