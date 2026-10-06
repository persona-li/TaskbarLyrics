using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarLyrics.Windows;

/// <summary>
/// Detects exclusive / borderless fullscreen on the taskbar's monitor so TOPMOST
/// taskbar lyrics can hide and not paint over video / games.
/// </summary>
public static class FullscreenDetector
{
    /// <summary>Allow a few px of mismatch (DPI / borders / exclusive modes).</summary>
    private const int CoverTolerancePx = 8;

    /// <summary>
    /// Returns true when lyrics should be suppressed because something is fullscreen
    /// on the same monitor as the taskbar/overlay.
    /// </summary>
    /// <param name="overlayOrTaskbarHwnd">Window used to pick the relevant monitor.</param>
    /// <param name="excludeHwnd">Our overlay HWND (never treat ourselves as fullscreen).</param>
    public static bool ShouldSuppressOverlay(IntPtr overlayOrTaskbarHwnd, IntPtr excludeHwnd)
    {
        // 1) Shell-reported exclusive / presentation / busy fullscreen.
        if (IsShellFullscreenState())
        {
            return true;
        }

        // 2) Foreground window geometrically covering the taskbar monitor
        //    (browser F11, borderless video players, etc.).
        return IsForegroundCoveringTaskbarMonitor(overlayOrTaskbarHwnd, excludeHwnd);
    }

    public static bool IsShellFullscreenState()
    {
        try
        {
            int hr = NativeMethods.SHQueryUserNotificationState(out var state);
            if (hr != 0)
            {
                return false;
            }

            return state is NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                or NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                or NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsForegroundCoveringTaskbarMonitor(IntPtr monitorHintHwnd, IntPtr excludeHwnd)
    {
        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == excludeHwnd)
            {
                return false;
            }

            if (!NativeMethods.IsWindowVisible(fg) || NativeMethods.IsIconic(fg))
            {
                return false;
            }

            if (IsShellOrDesktopWindow(fg))
            {
                return false;
            }

            // Ignore our own process windows (settings, rematch, tray menu host, etc.).
            if (IsSameProcess(fg, excludeHwnd))
            {
                return false;
            }

            if (!TryGetMonitorRect(monitorHintHwnd, out NativeMethods.RECT mon))
            {
                // Fallback: primary screen metrics.
                mon = new NativeMethods.RECT
                {
                    Left = 0,
                    Top = 0,
                    Right = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
                    Bottom = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)
                };
            }

            if (!NativeMethods.GetWindowRect(fg, out NativeMethods.RECT wr) || wr.Width <= 0 || wr.Height <= 0)
            {
                return false;
            }

            // Window must cover essentially the whole monitor (including taskbar band).
            return CoversMonitor(wr, mon, CoverTolerancePx);
        }
        catch
        {
            return false;
        }
    }

    public static bool CoversMonitor(NativeMethods.RECT window, NativeMethods.RECT monitor, int tolerancePx)
    {
        return window.Left <= monitor.Left + tolerancePx
            && window.Top <= monitor.Top + tolerancePx
            && window.Right >= monitor.Right - tolerancePx
            && window.Bottom >= monitor.Bottom - tolerancePx;
    }

    private static bool TryGetMonitorRect(IntPtr hwnd, out NativeMethods.RECT monitorRect)
    {
        monitorRect = default;
        IntPtr mon = hwnd != IntPtr.Zero
            ? NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);

        if (mon == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfo(mon, ref info))
        {
            return false;
        }

        monitorRect = info.rcMonitor;
        return monitorRect.Width > 0 && monitorRect.Height > 0;
    }

    private static bool IsShellOrDesktopWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        if (NativeMethods.GetClassName(hwnd, sb, sb.Capacity) <= 0)
        {
            return false;
        }

        string cls = sb.ToString();
        // Desktop / taskbar / shell chrome — never treat as "fullscreen video".
        return cls is "Progman"
            or "WorkerW"
            or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd"
            or "DV2ControlHost"
            or "MultitaskingViewFrame"
            or "ForegroundStaging";
    }

    private static bool IsSameProcess(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _ = GetWindowThreadProcessId(a, out uint pidA);
            _ = GetWindowThreadProcessId(b, out uint pidB);
            return pidA != 0 && pidA == pidB;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
