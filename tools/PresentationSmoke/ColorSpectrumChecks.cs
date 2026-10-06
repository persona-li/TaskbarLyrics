using System.Windows.Media;
using TaskbarLyrics.App.Controls;

internal static class ColorSpectrumChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var color in new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.Cyan, Colors.Magenta,
            Colors.Yellow, Colors.White, Colors.Black, Colors.Gray,
            Color.FromArgb(128, 73, 109, 191), Color.FromRgb(160, 204, 238) })
        {
            var hsv = ColorSpectrum.ToHsv(color);
            check(ColorSpectrum.FromHsv(hsv.Hue, hsv.Saturation, hsv.Brightness, color.A) == color,
                $"HSV conversion preserves color and alpha {color}");
        }
        check(ColorSpectrum.FromHsv(360, 1, 1) == Colors.Red, "Hue endpoint wraps to red");
        check(ColorSpectrum.FromHsv(-10, 2, 2) == Colors.Red, "HSV channels clamp out-of-range inputs");
        check(ColorSpectrum.FromHsv(double.NaN, double.NaN, double.NaN) == Colors.Black,
            "Nonfinite HSV input cannot produce invalid channels");
        check(ColorSpectrum.SelectionFromPoint(new Point(50, 25), 100, 100) == (.5, .75),
            "Palette coordinates map to saturation and inverted brightness");
        check(ColorSpectrum.SelectionFromPoint(new Point(-10, 110), 100, 100) == (0, 0),
            "Dragging outside palette clamps both channels");
        check(ColorSpectrum.SelectionFromPoint(new Point(110, -10), 100, 100) == (1, 1),
            "Dragging beyond opposite corner clamps both channels");
        check(ColorSpectrum.SelectionFromPoint(new Point(50, 25), 0, 0) == (0, 1),
            "Zero-size palette does not divide by zero");
        var spectrum = new ColorSpectrum();
        var edits = 0;
        spectrum.SelectionChanged += (_, _) => edits++;
        spectrum.Hue = 500;
        spectrum.Saturation = -1;
        spectrum.Brightness = 2;
        check(spectrum.Hue == 360 && spectrum.Saturation == 0 && spectrum.Brightness == 1,
            "Spectrum dependency properties coerce to valid channel bounds");
        check(edits == 0, "Refreshing palette state does not commit user edits");
        check(spectrum.Focusable, "Palette supports keyboard focus");
    }
}
