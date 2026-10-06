namespace TaskbarLyrics.Windows;

/// <summary>
/// Overlay target rectangle in screen pixels.
/// </summary>
public readonly struct OverlayTargetRect
{
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }

    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public OverlayTargetRect(int left, int top, int width, int height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public override string ToString()
        => $"L={Left} T={Top} R={Right} B={Bottom} (W={Width} H={Height})";
}

/// <summary>
/// Positions freestanding TOPMOST overlay on the taskbar band (screen coordinates).
/// Note: SetParent(Shell_TrayWnd) is NOT used — it fails for WPF layered windows on Win11
/// and can leave the overlay invisible.
/// </summary>
public static class OverlayPositioner
{
    public const int DefaultLeftMarginPx = 8;
    public const int DefaultWidthPx = 400;

    public static OverlayTargetRect ComputeTarget(
        NativeMethods.RECT taskbarRect,
        int leftMarginPx = DefaultLeftMarginPx,
        int widthPx = DefaultWidthPx)
    {
        int height = taskbarRect.Height;
        if (height <= 0)
        {
            height = 48;
        }

        int maxWidth = Math.Max(1, taskbarRect.Width - leftMarginPx);
        int width = Math.Min(widthPx, maxWidth);
        if (width <= 0)
        {
            width = Math.Min(widthPx, Math.Max(1, taskbarRect.Width));
        }

        int left = taskbarRect.Left + leftMarginPx;
        int top = taskbarRect.Top;
        return new OverlayTargetRect(left, top, width, height);
    }

    public static bool Apply(
        IntPtr hwnd,
        OverlayTargetRect target,
        out int win32Error,
        string reason = "position", bool log = true)
    {
        if (hwnd == IntPtr.Zero)
        {
            win32Error = 0;
            WindowsLog.Error("OverlayPositioner.Apply: HWND is zero.");
            return false;
        }

        // Ensure visible — shell interactions can hide / un-show layered windows.
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);

        bool ok = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        win32Error = ok ? 0 : NativeMethods.GetWin32Error();
        if (log || !ok) WindowsLog.Info(
            $"SetWindowPos ({reason}): success={ok}, Win32Error={win32Error}, " +
            $"HWND=0x{hwnd.ToInt64():X}, Overlay rect: {target}");
        return ok;
    }

    public static bool ReassertTopmost(IntPtr hwnd, out int win32Error, string reason = "reassert-topmost", bool log = false)
    {
        if (hwnd == IntPtr.Zero)
        {
            win32Error = 0;
            return false;
        }

        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }

        bool ok = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        win32Error = ok ? 0 : NativeMethods.GetWin32Error();
        if (log || !ok)
        {
            WindowsLog.Info($"SetWindowPos ({reason}): success={ok}, Win32Error={win32Error}");
        }

        return ok;
    }

    /// <summary>
    /// Strong z-order recovery: NOTOPMOST → TOPMOST + show + invalidate.
    /// Used when Start/Search/taskbar buttons bury the overlay.
    /// </summary>
    public static bool ForceTopmostRefresh(IntPtr hwnd, out int win32Error, string reason = "force-topmost")
    {
        if (hwnd == IntPtr.Zero)
        {
            win32Error = 0;
            return false;
        }

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        bool ok = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        // Also push to top of the topmost stack.
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, false);
        NativeMethods.RedrawWindow(
            hwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_FRAME);

        win32Error = ok ? 0 : NativeMethods.GetWin32Error();
        return ok;
    }

    public static void LogPlacementDiagnostics(
        IntPtr hwnd,
        NativeMethods.RECT taskbarRect,
        OverlayTargetRect target)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT actual))
        {
            WindowsLog.Error($"GetWindowRect(overlay) failed. Win32Error={NativeMethods.GetWin32Error()}");
            return;
        }

        bool sameTop = actual.Top == taskbarRect.Top;
        bool bottomWithin = actual.Bottom <= taskbarRect.Bottom;
        bool leftWithin = actual.Left >= taskbarRect.Left && actual.Left < taskbarRect.Right;
        bool entirelyAbove = actual.Bottom <= taskbarRect.Top;

        WindowsLog.Info($"Taskbar rect: {taskbarRect}");
        WindowsLog.Info($"Target rect:  {target}");
        WindowsLog.Info($"Overlay rect: {actual}");
        WindowsLog.Info($"Visible: {NativeMethods.IsWindowVisible(hwnd)}");
        WindowsLog.Info($"Overlay.Top == Taskbar.Top: {sameTop}");
        WindowsLog.Info($"Overlay.Bottom <= Taskbar.Bottom: {bottomWithin}");

        if (entirelyAbove)
        {
            WindowsLog.Error("DIAGNOSIS: Window is ENTIRELY ABOVE the taskbar (invalid).");
        }
        else if (sameTop && bottomWithin && leftWithin)
        {
            WindowsLog.Info("DIAGNOSIS: geometry inside taskbar band OK.");
        }
    }
}
