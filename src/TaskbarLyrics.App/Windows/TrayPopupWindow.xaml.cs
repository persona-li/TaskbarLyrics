using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using Forms = System.Windows.Forms;

namespace TaskbarLyrics.App.Windows;

/// <summary>
/// Compact flat tray context menu (Wallpaper Engine-like).
/// Dismisses reliably on outside click even when Activate() fails from the tray.
/// </summary>
public partial class TrayPopupWindow : Window
{
    private readonly ConfigService _config;
    private readonly Action _openSettings;
    private readonly Action _reload;
    private readonly Action _rematch;
    private readonly Action _openCache;
    private readonly Action _exit;
    private readonly Action<bool> _setOverlayVisible;

    private bool _closing;
    private bool _readyToDismiss;
    private DispatcherTimer? _dismissPoll;
    private DispatcherTimer? _graceTimer;

    public TrayPopupWindow(
        ConfigService config,
        Action openSettings,
        Action reload,
        Action rematch,
        Action openCache,
        Action exit,
        Action<bool> setOverlayVisible)
    {
        InitializeComponent();
        _config = config;
        _openSettings = openSettings;
        _reload = reload;
        _rematch = rematch;
        _openCache = openCache;
        _exit = exit;
        _setOverlayVisible = setOverlayVisible;
        RefreshToggleLabel();

        Deactivated += (_, _) => TryDismissFromOutside();
        // Any click that lands outside our window bounds should close us.
        // (WinForms tray click often fails to properly activate a WPF window.)
        PreviewMouseDown += (_, _) => { /* keep alive while interacting */ };
    }

    public void ShowNearTray(int screenX, int screenY)
    {
        _readyToDismiss = false;
        Opacity = 0;

        // Show without activating first so the OS tray overflow can finish closing
        // without leaving us in a half-focused zombie state.
        Show();
        UpdateLayout();

        PositionAtCursor(screenX, screenY);
        Opacity = 1;

        // Force foreground after a short delay — tray overflow usually collapses by then.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ActivatePopup();
            }
            catch
            {
                // ignore
            }
        }, DispatcherPriority.ApplicationIdle);

        // Grace: ignore outside-dismiss for the same physical click that opened us.
        _graceTimer?.Stop();
        _graceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer.Stop();
            _readyToDismiss = true;
            StartDismissPoll();
            try { ActivatePopup(); } catch { /* ignore */ }
        };
        _graceTimer.Start();
    }

    private void PositionAtCursor(int screenX, int screenY)
    {
        var dpi = GetDpiScale();
        if (dpi <= 0)
        {
            dpi = 1;
        }

        // Prefer the monitor that contains the tray click (multi-monitor safe).
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(screenX, screenY));
        var wa = screen.WorkingArea; // physical pixels

        var widthDip = ActualWidth;
        var heightDip = ActualHeight;
        var widthPx = widthDip * dpi;
        var heightPx = heightDip * dpi;

        // Default: menu grows up and left from the cursor (tray is usually bottom-right).
        var leftPx = screenX - widthPx + 8;
        var topPx = screenY - heightPx - 6;

        // Keep fully inside the working area of that monitor.
        if (leftPx < wa.Left + 4)
        {
            leftPx = wa.Left + 4;
        }

        if (topPx < wa.Top + 4)
        {
            // Not enough room above — open downward from cursor.
            topPx = screenY + 6;
        }

        if (leftPx + widthPx > wa.Right - 4)
        {
            leftPx = wa.Right - widthPx - 4;
        }

        if (topPx + heightPx > wa.Bottom - 4)
        {
            topPx = wa.Bottom - heightPx - 4;
        }

        Left = leftPx / dpi;
        Top = topPx / dpi;
    }

    private void ActivatePopup()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero)
            {
                helper.EnsureHandle();
            }

            // Bring to front even when launched from NotifyIcon (no proper activation).
            NativeMethods.SetForegroundWindow(helper.Handle);
            Activate();
            Focus();
        }
        catch
        {
            try { Activate(); } catch { /* ignore */ }
        }
    }

    private void StartDismissPoll()
    {
        _dismissPoll?.Stop();
        // Poll: if user clicks outside, or another window is foreground, close.
        // This is the reliable fix when Deactivated never fires from tray context.
        _dismissPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _dismissPoll.Tick += (_, _) => PollDismiss();
        _dismissPoll.Start();
    }

    private void PollDismiss()
    {
        if (!_readyToDismiss || _closing || !IsVisible)
        {
            return;
        }

        // Primary button down outside our bounds → dismiss (classic context-menu feel).
        if (Forms.Control.MouseButtons == Forms.MouseButtons.Left
            || Forms.Control.MouseButtons == Forms.MouseButtons.Right)
        {
            var pt = Forms.Control.MousePosition; // physical
            if (!ContainsPhysicalPoint(pt.X, pt.Y))
            {
                CloseSoft();
                return;
            }
        }

        // If we lost foreground to another app/window, dismiss.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var fg = NativeMethods.GetForegroundWindow();
            if (hwnd != IntPtr.Zero && fg != IntPtr.Zero && fg != hwnd)
            {
                // Still allow if mouse is over us (some hosts steal FG briefly).
                var pt = Forms.Control.MousePosition;
                if (!ContainsPhysicalPoint(pt.X, pt.Y))
                {
                    CloseSoft();
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private bool ContainsPhysicalPoint(int x, int y)
    {
        var dpi = GetDpiScale();
        if (dpi <= 0)
        {
            dpi = 1;
        }

        var left = Left * dpi;
        var top = Top * dpi;
        var right = left + ActualWidth * dpi;
        var bottom = top + ActualHeight * dpi;
        return x >= left && x <= right && y >= top && y <= bottom;
    }

    private void TryDismissFromOutside()
    {
        if (!_readyToDismiss || _closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_readyToDismiss && !_closing)
            {
                CloseSoft();
            }
        }, DispatcherPriority.Background);
    }

    private double GetDpiScale()
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget is not null)
            {
                return src.CompositionTarget.TransformToDevice.M11;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            // Fallback via WinForms when PresentationSource not ready.
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private void RefreshToggleLabel()
    {
        var on = _config.Current.General.ShowOverlay;
        ToggleCheck.Text = on ? "✓" : "";
    }

    private void BtnToggle_OnClick(object sender, RoutedEventArgs e)
    {
        var next = !_config.Current.General.ShowOverlay;
        _config.Update(c => c.General.ShowOverlay = next);
        _setOverlayVisible(next);
        RefreshToggleLabel();
        // Keep menu open after toggle — matches normal context menus with checks.
    }

    private void BtnSettings_OnClick(object sender, RoutedEventArgs e)
    {
        CloseSoft();
        _openSettings();
    }

    private void BtnReload_OnClick(object sender, RoutedEventArgs e)
    {
        CloseSoft();
        _reload();
    }

    private void BtnRematch_OnClick(object sender, RoutedEventArgs e)
    {
        CloseSoft();
        _rematch();
    }

    private void BtnCache_OnClick(object sender, RoutedEventArgs e)
    {
        CloseSoft();
        _openCache();
    }

    private void BtnExit_OnClick(object sender, RoutedEventArgs e)
    {
        CloseSoft();
        _exit();
    }

    private void CloseSoft()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _readyToDismiss = false;
        try { _graceTimer?.Stop(); } catch { /* ignore */ }
        try { _dismissPoll?.Stop(); } catch { /* ignore */ }
        try { Close(); } catch { /* ignore */ }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
    }
}
