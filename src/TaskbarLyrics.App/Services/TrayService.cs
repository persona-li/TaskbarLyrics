using System.Drawing;
using TaskbarLyrics.App.Config;
using WpfApp = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace TaskbarLyrics.App.Services;

/// <summary>
/// NotifyIcon + system ContextMenuStrip (flat compact renderer).
/// ContextMenuStrip is required so the menu works when the icon is in the overflow (^) area.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ModernTrayMenu _menu;
    private bool _disposed;
    private readonly TrackCoordinator? _coordinator;
    private readonly ConfigService _config;

    public TrayService(
        ConfigService config,
        Action openSettings,
        Action rematchLyrics,
        Action exitApp,
        Action<bool> setOverlayVisible, TrackCoordinator? coordinator = null)
    {
        _coordinator = coordinator; _config = config;
        var trayIcon = LoadTrayIcon() ?? SystemIcons.Application;

        _icon = new Forms.NotifyIcon
        {
            Text = "TaskbarLyrics",
            Visible = true,
            Icon = trayIcon,
        };

        _menu = new ModernTrayMenu(
            _icon,
            config,
            openSettings: openSettings,
            rematch: rematchLyrics,
            exit: exitApp,
            setOverlayVisible: setOverlayVisible,
            readState: coordinator is null ? null : coordinator.GetUiState);
        if (coordinator is not null) coordinator.StateChanged += OnStateChanged;
        config.ConfigChanged += OnConfigChanged;

        App.Logger?.Info("Tray ready (anchored ContextMenuStrip + rounded region)");
    }

    private void OnConfigChanged(AppConfig _) => OnStateChanged();
    private void OnStateChanged()
    {
        WpfApp.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _menu.RefreshPresentation();
            _icon.Text = "TaskbarLyrics";
        });
    }

    private static Icon? LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/Icons/tray.ico");
            var streamInfo = WpfApp.GetResourceStream(uri);
            if (streamInfo?.Stream is not null)
            {
                return new Icon(streamInfo.Stream);
            }
        }
        catch (Exception ex)
        {
            App.Logger?.Warn("Tray icon load failed: " + ex.Message);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is not null) _coordinator.StateChanged -= OnStateChanged;
        _config.ConfigChanged -= OnConfigChanged;
        try { _menu.Dispose(); } catch { /* ignore */ }
        _icon.Visible = false;
        _icon.Dispose();
    }
}
