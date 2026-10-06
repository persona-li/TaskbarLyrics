using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarLyrics.Windows;
namespace TaskbarLyrics.App.Services;
public static class WindowPlacement
{
    // Physical coordinates avoid mixing the DPI origins of different monitors.
    public static Rect Constrain(Rect bounds, Rect workArea)
    {
        var width = Math.Min(bounds.Width, workArea.Width);
        var height = Math.Min(bounds.Height, workArea.Height);
        return new Rect(Math.Clamp(bounds.X, workArea.Left, workArea.Right - width),
            Math.Clamp(bounds.Y, workArea.Top, workArea.Bottom - height), width, height);
    }
    public static double AvailableHeight(Window window)
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle);
        return screen.WorkingArea.Height / VisualTreeHelper.GetDpi(window).DpiScaleY;
    }
    public static void ClampToWorkArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var r)) return;
        var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        window.MinWidth = Math.Min(window.MinWidth, area.Width / dpi.DpiScaleX);
        window.MinHeight = Math.Min(window.MinHeight, area.Height / dpi.DpiScaleY);
        var target = Constrain(new Rect(r.Left, r.Top, r.Width, r.Height), new Rect(area.Left, area.Top, area.Width, area.Height));
        if (target.Width < r.Width || target.Height < r.Height) window.SizeToContent = SizeToContent.Manual;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, (int)target.X, (int)target.Y, (int)target.Width, (int)target.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
