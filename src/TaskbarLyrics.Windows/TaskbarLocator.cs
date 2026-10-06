using System.Runtime.InteropServices;

namespace TaskbarLyrics.Windows;

/// <summary>
/// Taskbar rect query result. Source: TaskbarOverlayProbe.
/// </summary>
public sealed class TaskbarLocationResult
{
    public required NativeMethods.RECT Rect { get; init; }
    public required bool ShAppBarMessageSucceeded { get; init; }
    public required bool UsedFindWindowFallback { get; init; }
    public string Source { get; init; } = string.Empty;
    public int? Win32Error { get; init; }
    public string? ErrorMessage { get; init; }

    public int Width => Rect.Width;
    public int Height => Rect.Height;
}

/// <summary>
/// Locate primary taskbar in screen pixels via SHAppBarMessage, fallback FindWindow.
/// Source: TaskbarOverlayProbe.
/// </summary>
public static class TaskbarLocator
{
    public const string PrimaryTaskbarClassName = "Shell_TrayWnd";

    public static TaskbarLocationResult LocatePrimaryTaskbar()
    {
        var abd = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = IntPtr.Zero,
            uCallbackMessage = 0,
            uEdge = 0,
            rc = default,
            lParam = 0
        };

        IntPtr shResult = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref abd);
        int shError = NativeMethods.GetWin32Error();

        if (shResult != IntPtr.Zero)
        {
            var rect = abd.rc;
            if (IsValidRect(rect))
            {
                WindowsLog.Info(
                    $"SHAppBarMessage(ABM_GETTASKBARPOS) OK. Result={shResult.ToInt64()}, " +
                    $"Rect={rect}, W={rect.Width}, H={rect.Height}");

                return new TaskbarLocationResult
                {
                    Rect = rect,
                    ShAppBarMessageSucceeded = true,
                    UsedFindWindowFallback = false,
                    Source = "SHAppBarMessage(ABM_GETTASKBARPOS)",
                    Win32Error = null,
                    ErrorMessage = null
                };
            }

            WindowsLog.Warn($"SHAppBarMessage non-zero but invalid rect: {rect}. Fallback FindWindow.");
        }
        else
        {
            WindowsLog.Error(
                $"SHAppBarMessage FAILED Win32Error={shError}. Fallback FindWindow(Shell_TrayWnd).");
        }

        return LocateViaFindWindow(shError);
    }

    private static TaskbarLocationResult LocateViaFindWindow(int previousShError)
    {
        IntPtr tray = NativeMethods.FindWindow(PrimaryTaskbarClassName, null);
        int findError = NativeMethods.GetWin32Error();

        if (tray == IntPtr.Zero)
        {
            var msg = $"FindWindow(Shell_TrayWnd) NULL. Win32={findError}, prev SH={previousShError}";
            WindowsLog.Error(msg);
            return new TaskbarLocationResult
            {
                Rect = default,
                ShAppBarMessageSucceeded = false,
                UsedFindWindowFallback = true,
                Source = "FindWindow FAILED",
                Win32Error = findError,
                ErrorMessage = msg
            };
        }

        if (!NativeMethods.GetWindowRect(tray, out NativeMethods.RECT rect))
        {
            var msg = $"GetWindowRect failed Win32={NativeMethods.GetWin32Error()}";
            WindowsLog.Error(msg);
            return new TaskbarLocationResult
            {
                Rect = default,
                ShAppBarMessageSucceeded = false,
                UsedFindWindowFallback = true,
                Source = "GetWindowRect FAILED",
                Win32Error = NativeMethods.GetWin32Error(),
                ErrorMessage = msg
            };
        }

        if (!IsValidRect(rect))
        {
            var msg = $"Invalid rect {rect}";
            WindowsLog.Error(msg);
            return new TaskbarLocationResult
            {
                Rect = rect,
                ShAppBarMessageSucceeded = false,
                UsedFindWindowFallback = true,
                Source = "invalid rect",
                ErrorMessage = msg
            };
        }

        WindowsLog.Info($"FindWindow fallback OK. HWND=0x{tray.ToInt64():X} Rect={rect}");
        return new TaskbarLocationResult
        {
            Rect = rect,
            ShAppBarMessageSucceeded = false,
            UsedFindWindowFallback = true,
            Source = "FindWindow+GetWindowRect",
            ErrorMessage = null
        };
    }

    private static bool IsValidRect(NativeMethods.RECT rc) =>
        rc.Right > rc.Left && rc.Bottom > rc.Top;
}
