namespace TaskbarLyrics.App.Config;

/// <summary>One hue controls the fixed lightness/chroma relationship of both lyric colors.</summary>
public static class OklchLyricPalette
{
    public const double DefaultHue = 264.540965113;
    public const double NormalLightness = 0.548562815;
    public const double NormalChroma = 0.134419357;
    public const double HighlightLightness = 0.825590585;
    public const double HighlightChroma = 0.066768327;
    public const double HighlightHueOffset = 22.733561535;

    public readonly record struct Palette(string NormalColor, string HighlightColor);
    public readonly record struct MappedColor(double Lightness, double Chroma, double Hue,
        double LinearRed, double LinearGreen, double LinearBlue)
    {
        public bool IsInGamut => InGamut(LinearRed, LinearGreen, LinearBlue);
        public string Argb => $"#FF{Encode(LinearRed):X2}{Encode(LinearGreen):X2}{Encode(LinearBlue):X2}";
    }

    public static double NormalizeHue(double hue)
    {
        if (!double.IsFinite(hue)) return DefaultHue;
        return ((hue % 360) + 360) % 360;
    }

    public static Palette Generate(double hue)
    {
        hue = NormalizeHue(hue);
        return new(Map(NormalLightness, NormalChroma, hue).Argb,
            Map(HighlightLightness, HighlightChroma, hue - HighlightHueOffset).Argb);
    }

    /// <summary>Find the largest in-gamut chroma without altering the requested lightness or hue.</summary>
    public static MappedColor Map(double lightness, double chroma, double hue)
    {
        if (!double.IsFinite(lightness) || lightness < 0 || lightness > 1)
            throw new ArgumentOutOfRangeException(nameof(lightness));
        if (!double.IsFinite(chroma) || chroma < 0)
            throw new ArgumentOutOfRangeException(nameof(chroma));
        hue = NormalizeHue(hue);
        var candidate = ToLinear(lightness, chroma, hue);
        if (candidate.IsInGamut) return candidate;

        double lower = 0, upper = chroma;
        var result = ToLinear(lightness, 0, hue);
        for (var i = 0; i < 48; i++)
        {
            var middle = lower + (upper - lower) / 2;
            candidate = ToLinear(lightness, middle, hue);
            if (candidate.IsInGamut) { lower = middle; result = candidate; }
            else upper = middle;
        }
        return result;
    }

    private static MappedColor ToLinear(double lightness, double chroma, double hue)
    {
        var radians = hue * Math.PI / 180;
        var a = chroma * Math.Cos(radians);
        var b = chroma * Math.Sin(radians);
        var l = lightness + 0.3963377774 * a + 0.2158037573 * b;
        var m = lightness - 0.1055613458 * a - 0.0638541728 * b;
        var s = lightness - 0.0894841775 * a - 1.2914855480 * b;
        l = l * l * l; m = m * m * m; s = s * s * s;
        return new(lightness, chroma, hue,
            4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static bool InGamut(double r, double g, double b) =>
        r >= -1e-12 && r <= 1 + 1e-12 && g >= -1e-12 && g <= 1 + 1e-12 && b >= -1e-12 && b <= 1 + 1e-12;

    private static byte Encode(double linear)
    {
        // Map has already removed real gamut overflow. Clamp only floating-point boundary noise.
        linear = Math.Clamp(linear, 0, 1);
        var srgb = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(srgb * 255, MidpointRounding.AwayFromZero);
    }
}
