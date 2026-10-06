using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace TaskbarLyrics.App.Services;

/// <summary>
/// Resolves a screen-space anchor for the tray menu.
/// Prefer the icon rect; fall back to the taskbar notification area so menus
/// never float mid-desktop after the overflow (^) flyout collapses.
/// </summary>
internal static class TrayAnchor
{
    /// <summary>
    /// Returns a point at the bottom-right of the preferred anchor rect
    /// (physical screen pixels). Menu should open AboveLeft from this point.
    /// </summary>
    public static DrawingPoint GetMenuAnchor(Forms.NotifyIcon icon)
    {
        if (TryGetIconRect(icon, out var iconRect) && iconRect.Width > 0 && iconRect.Height > 0)
        {
            // Icon still visible (main tray or open overflow): sit above the icon.
            return new DrawingPoint(iconRect.Right, iconRect.Top);
        }

        if (TryGetNotificationAreaRect(out var notifyRect))
        {
            // Overflow closed / icon hidden: park menu on the taskbar notify cluster.
            return new DrawingPoint(notifyRect.Right - 4, notifyRect.Top);
        }

        // Last resort: primary work-area bottom-right (typical tray corner).
        var wa = Forms.Screen.PrimaryScreen?.WorkingArea
                 ?? new Rectangle(0, 0, 1920, 1080);
        return new DrawingPoint(wa.Right - 8, wa.Bottom);
    }

    public static bool TryGetIconRect(Forms.NotifyIcon icon, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        try
        {
            if (!TryGetNotifyIconIdentity(icon, out var hwnd, out var id))
            {
                return false;
            }

            var ident = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = hwnd,
                uID = id,
                guidItem = Guid.Empty,
            };

            if (Shell_NotifyIconGetRect(ref ident, out var r) != 0)
            {
                return false;
            }

            rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return rect.Width > 0 && rect.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetNotificationAreaRect(out Rectangle rect)
    {
        rect = Rectangle.Empty;
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero)
            {
                return false;
            }

            // Win10/11: TrayNotifyWnd hosts the notification icons + chevron.
            var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero)
            {
                return false;
            }

            if (!GetWindowRect(notify, out var r))
            {
                return false;
            }

            rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return rect.Width > 0 && rect.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetNotifyIconIdentity(Forms.NotifyIcon icon, out IntPtr hwnd, out uint id)
    {
        hwnd = IntPtr.Zero;
        id = 0;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        // .NET Framework: window / id   ·  .NET (Core/5+): _window / _id
        object? windowObj =
            typeof(Forms.NotifyIcon).GetField("window", flags)?.GetValue(icon)
            ?? typeof(Forms.NotifyIcon).GetField("_window", flags)?.GetValue(icon);

        object? idObj =
            typeof(Forms.NotifyIcon).GetField("id", flags)?.GetValue(icon)
            ?? typeof(Forms.NotifyIcon).GetField("_id", flags)?.GetValue(icon);

        if (windowObj is null || idObj is null)
        {
            return false;
        }

        // NativeWindow / NotifyIconNativeWindow → Handle
        var handleProp = windowObj.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (handleProp?.GetValue(windowObj) is not IntPtr handle || handle == IntPtr.Zero)
        {
            // Some builds expose Handle as a field
            var handleField = windowObj.GetType().GetField("handle", flags)
                              ?? windowObj.GetType().GetField("_handle", flags);
            if (handleField?.GetValue(windowObj) is IntPtr h2)
            {
                handle = h2;
            }
            else if (windowObj is Forms.NativeWindow nw)
            {
                handle = nw.Handle;
            }
            else
            {
                return false;
            }
        }

        hwnd = handle;
        id = Convert.ToUInt32(idObj);
        return hwnd != IntPtr.Zero;
    }

    #region Native

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    #endregion
}

// Local alias so we don't fight global using Point = System.Windows.Point
internal readonly record struct DrawingPoint(int X, int Y);
