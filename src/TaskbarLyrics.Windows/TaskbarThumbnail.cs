using System.Runtime.InteropServices;
namespace TaskbarLyrics.Windows;
public static class TaskbarThumbnail
{
    public static bool Enable(IntPtr hwnd)
    {
        int yes = 1;
        PreventPeek(hwnd); // Thumbnail only, no desktop-sized duplicate.
        return DwmSetWindowAttribute(hwnd, 7, ref yes, 4) >= 0
            && DwmSetWindowAttribute(hwnd, 10, ref yes, 4) >= 0;
    }
    public static void Invalidate(IntPtr hwnd) => DwmInvalidateIconicBitmaps(hwnd);
    public static int Set(IntPtr hwnd, int width, int height, byte[] pixels)
    {
        var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
        var bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) return -1;
        try { Marshal.Copy(pixels, 0, bits, pixels.Length); return DwmSetIconicThumbnail(hwnd, bitmap, 0); }
        finally { DeleteObject(bitmap); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmInvalidateIconicBitmaps(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmSetIconicThumbnail(IntPtr h, IntPtr bitmap, uint flags);
    public static bool PreventPeek(IntPtr hwnd)
    {
        int yes = 1;
        return DwmSetWindowAttribute(hwnd, 11, ref yes, 4) >= 0;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
