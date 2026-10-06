using System.Runtime.InteropServices;

namespace TaskbarLyrics.Windows;

public static class WindowFrame
{
    // Windows 11 DWM owns the single outer contour; WPF must not add another region.
    public static bool UseRoundedBorderlessFrame(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        int round = 2; // DWMWCP_ROUND
        if (DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)) < 0) return false;
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        return DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int)) >= 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
