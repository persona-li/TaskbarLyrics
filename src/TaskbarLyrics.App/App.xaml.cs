using System.Windows;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.App.Windows;
using TaskbarLyrics.Cache;
using TaskbarLyrics.Windows;

namespace TaskbarLyrics.App;

public partial class App : Application
{
    private AppLogger? _logger;
    private ConfigService? _config;
    private CancellationTokenSource? _appCts;
    private TrackCoordinator? _coordinator;
    private TrackLookupCache? _lookup;
    private TrackSettingsStore? _trackSettings;
    private CacheStatisticsService? _stats;
    private TrayService? _tray;
    private SingleInstanceService? _single;
    private MainWindow? _overlay;
    private SettingsWindow? _settings;

    public static TrackCoordinator? Coordinator { get; private set; }
    public static AppLogger? Logger { get; private set; }
    public static ConfigService? Config { get; private set; }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Installer requests a normal, settings-preserving exit of this exact copy.
        if (e.Args.Contains("--shutdown"))
        {
            var path = Environment.ProcessPath!;
            var targets = System.Diagnostics.Process.GetProcessesByName(
                System.IO.Path.GetFileNameWithoutExtension(path))
                .Where(p => p.Id != Environment.ProcessId)
                .Where(p => { try { return string.Equals(p.MainModule?.FileName, path,
                    StringComparison.OrdinalIgnoreCase); } catch { return false; } }).ToArray();
            if (targets.Length > 0) SingleInstanceService.TryNotifyPrimaryExit(path);
            var exited = true;
            foreach (var process in targets)
            {
                try { if (!await Task.Run(() => process.WaitForExit(15000))) exited = false; }
                finally { process.Dispose(); }
            }
            Shutdown(exited ? 0 : 1);
            return;
        }
        // Single instance first (before heavy init)
        _single = new SingleInstanceService();
        if (!_single.IsPrimaryInstance)
        {
            if (!SingleInstanceService.TryNotifyPrimaryShowSettings())
            {
                MessageBox.Show("TaskbarLyrics 已在运行。", "TaskbarLyrics", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown(0);
            return;
        }

        _single.StartListening();
        _single.ShowSettingsRequested += () => Dispatcher.BeginInvoke(OpenSettings);
        _single.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);

        try
        {
            _logger = new AppLogger();
            Logger = _logger;
            WindowsLog.SetLogger(_logger);
            _appCts = new CancellationTokenSource();

            _logger.Info("========== TaskbarLyrics.App starting ==========");
            _logger.Info($"Version: {typeof(App).Assembly.GetName().Version}");
            _logger.Info($"OS: {Environment.OSVersion}");
            _logger.Info($".NET: {Environment.Version}");
            _logger.Info($"Log: {_logger.LogFilePath}");
            _logger.Info($"Cache: {CachePaths.LyricsRoot}");

            _config = new ConfigService(
                msg => _logger.Info(msg),
                msg => _logger.Warn(msg),
                (stage, ex) => _logger.Error(stage, ex));
            Config = _config;
            ThemeManager.Apply(_config.Current.General.Theme);
            ThemeManager.StartWatchingSystemTheme();
            _config.ConfigChanged += OnConfigChanged;

            _lookup = new TrackLookupCache(_logger);
            _trackSettings = new TrackSettingsStore(_logger);
            _stats = new CacheStatisticsService(_lookup, _trackSettings);

            _coordinator = new TrackCoordinator(
                _logger,
                _config,
                _lookup,
                _trackSettings,
                _appCts.Token);
            Coordinator = _coordinator;

            await _coordinator.InitializeAsync(_appCts.Token).ConfigureAwait(true);

            base.OnStartup(e);

            _overlay = new MainWindow();
            MainWindow = _overlay;
            _overlay.Show();
            if (!_config.Current.General.ShowOverlay)
            {
                _overlay.SetOverlayVisible(false);
            }

            _tray = new TrayService(
                _config,
                openSettings: OpenSettings,
                rematchLyrics: OpenRematch,
                exitApp: ExitApplication,
                setOverlayVisible: visible => _overlay?.SetOverlayVisible(visible),
                coordinator: _coordinator);

            _logger.Info("Tray + Overlay ready");
        }
        catch (Exception ex)
        {
            _logger?.Error("OnStartup", ex);
            MessageBox.Show(
                $"启动失败：{ex.Message}\n日志：{_logger?.LogFilePath}",
                "TaskbarLyrics",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void OpenSettings() => OpenSettingsPage(SettingsPage.Overview);

    public void OpenSettingsPage(SettingsPage page)
    {
        try
        {
            if (_config is null || _coordinator is null || _stats is null)
            {
                _logger?.Warn("OpenSettings: services not ready");
                MessageBox.Show("应用尚未初始化完成，请稍后再试。", "TaskbarLyrics");
                return;
            }

            if (_settings is null)
            {
                _logger?.Info("Creating SettingsWindow...");
                _settings = new SettingsWindow(_config, _coordinator, _stats, OpenRematch);
                _logger?.Info("SettingsWindow created");
            }

            if (!_settings.IsVisible)
            {
                _settings.Show();
            }

            if (_settings.WindowState == WindowState.Minimized)
            {
                _settings.WindowState = WindowState.Normal;
            }

            _settings.Navigate(page);
            _settings.Activate();
            _ = _settings.Focus();
        }
        catch (Exception ex)
        {
            _logger?.Error("OpenSettings", ex);
            // Allow retry after a failed construct
            try { _settings = null; } catch { /* ignore */ }
            try
            {
                MessageBox.Show(
                    $"无法打开设置：{ex.Message}\n\n{ex.GetType().Name}\n日志：{_logger?.LogFilePath}",
                    "TaskbarLyrics",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch
            {
                // ignore
            }
        }
    }

    public void OpenRematch()
    {
        if (_coordinator is null)
        {
            return;
        }

        if (_coordinator.CurrentTrack is null || string.IsNullOrWhiteSpace(_coordinator.CurrentTrack.Title))
        {
            OpenSettingsPage(SettingsPage.Overview);
            _settings?.ViewModel.Notify("请先在 QQ 音乐播放一首歌，再选择歌词版本。");
            return;
        }

        var w = new LyricsRematchWindow(_coordinator)
        {
            Owner = _settings is { IsVisible: true } ? _settings : null
        };
        w.ShowDialog();
    }

    public void ExitApplication()
    {
        try { _config?.SaveNow(); }
        catch (Exception ex)
        {
            OpenSettingsPage(SettingsPage.General);
            _settings?.ViewModel.Notify("配置保存失败：" + ex.Message, true);
            if (_settings is null || !ModernDialogWindow.Confirm(_settings, "仍要退出吗？",
                "最新设置尚未保存。可以取消并重试保存，或明确放弃未保存的修改后退出。", danger: true, confirmText: "不保存并退出")) return;
            _config?.DisposeWithoutSaving();
        }

        try
        {
            _logger?.Info("Exit requested from tray");
            _logger?.Info(
                $"Stats: CacheHits={_coordinator?.Cache.CacheHits} " +
                $"Misses={_coordinator?.Cache.CacheMisses} " +
                $"Repairs={_coordinator?.Cache.CacheRepairs} " +
                $"NetLoads={_coordinator?.NetworkStats.NetworkLyricsLoads} " +
                $"NetFails={_coordinator?.NetworkStats.NetworkLyricsFailures}");
            _logger?.Info("========== TaskbarLyrics.App stopped ==========");
        }
        catch
        {
            // ignore
        }

        try { _appCts?.Cancel(); } catch { /* ignore */ }
        try { ThemeManager.StopWatchingSystemTheme(); } catch { /* ignore */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _coordinator?.Dispose(); } catch { /* ignore */ }
        try { _config?.Dispose(); } catch { /* ignore */ }
        try { _single?.Dispose(); } catch { /* ignore */ }
        try { _logger?.Dispose(); } catch { /* ignore */ }

        try
        {
            if (_settings is not null)
            {
                _settings.AllowClose = true;
                _settings.Close();
            }
        }
        catch { /* ignore */ }

        try { _overlay?.Close(); } catch { /* ignore */ }

        Coordinator = null;
        Logger = null;
        Config = null;
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _single?.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }

    private void OnConfigChanged(AppConfig config)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnConfigChanged(config));
            return;
        }

        if (!string.Equals(ThemeManager.CurrentPreference, config.General.Theme, StringComparison.Ordinal))
        {
            ThemeManager.Apply(config.General.Theme);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("Dispatcher unhandled", e.Exception);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.Error("AppDomain unhandled", ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error("Unobserved task", e.Exception);
        e.SetObserved();
    }
}
