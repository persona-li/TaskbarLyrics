using TaskbarLyrics.App.Config;

internal static class OklchPaletteChecks
{
    public static void Run(Action<bool, string> check)
    {
        var original = OklchLyricPalette.Generate(OklchLyricPalette.DefaultHue);
        check(original.NormalColor == "#FF496DBF" && original.HighlightColor == "#FFA0CCEE",
            "OKLCH default hue exactly reproduces both original opaque ARGB colors");
        check(OklchLyricPalette.Generate(360) == OklchLyricPalette.Generate(0)
            && OklchLyricPalette.Generate(-15) == OklchLyricPalette.Generate(345)
            && OklchLyricPalette.Generate(745) == OklchLyricPalette.Generate(25),
            "Palette hue wraps across positive and negative full turns");
        check(OklchLyricPalette.Generate(double.NaN) == original,
            "Nonfinite hue safely returns the original palette");

        foreach (var hue in new[] { OklchLyricPalette.DefaultHue, 305, 345, 25, 65, 105, 185, 225 })
        {
            var palette = OklchLyricPalette.Generate(hue);
            var normal = OklchLyricPalette.Map(OklchLyricPalette.NormalLightness, OklchLyricPalette.NormalChroma, hue);
            var highlight = OklchLyricPalette.Map(OklchLyricPalette.HighlightLightness, OklchLyricPalette.HighlightChroma,
                hue - OklchLyricPalette.HighlightHueOffset);
            check(normal.IsInGamut && highlight.IsInGamut && palette.NormalColor == normal.Argb && palette.HighlightColor == highlight.Argb,
                $"Preset {hue} uses the same gamut-mapped palette algorithm");
            check(palette.NormalColor.StartsWith("#FF") && palette.HighlightColor.StartsWith("#FF") && palette.NormalColor != palette.HighlightColor,
                $"Preset {hue} provides distinct opaque lyric colors");
        }

        for (var hue = 0; hue < 360; hue++)
        {
            var mapped = OklchLyricPalette.Map(.6, .5, hue);
            check(mapped.IsInGamut && mapped.Chroma > 0 && mapped.Chroma < .5,
                $"Out-of-gamut hue {hue} reduces chroma into linear sRGB");
            var l = Math.Cbrt(.4122214708 * mapped.LinearRed + .5363325363 * mapped.LinearGreen + .0514459929 * mapped.LinearBlue);
            var m = Math.Cbrt(.2119034982 * mapped.LinearRed + .6806995451 * mapped.LinearGreen + .1073969566 * mapped.LinearBlue);
            var s = Math.Cbrt(.0883024619 * mapped.LinearRed + .2817188376 * mapped.LinearGreen + .6299787005 * mapped.LinearBlue);
            var recoveredL = .2104542553 * l + .7936177850 * m - .0040720468 * s;
            var a = 1.9779984951 * l - 2.4285922050 * m + .4505937099 * s;
            var b = .0259040371 * l + .7827717662 * m - .8086757660 * s;
            var recoveredHue = OklchLyricPalette.NormalizeHue(Math.Atan2(b, a) * 180 / Math.PI);
            var hueError = Math.Abs(recoveredHue - hue);
            hueError = Math.Min(hueError, 360 - hueError);
            check(Math.Abs(recoveredL - .6) < 1e-7 && hueError < .0001,
                $"Chroma mapping preserves actual lightness and hue {hue}, without channel clipping");
            var larger = OklchLyricPalette.Map(.6, mapped.Chroma + .00001, hue);
            check(Math.Abs(larger.Chroma - mapped.Chroma) < 1e-9,
                $"Binary search approaches the gamut boundary for hue {hue}");
        }
        check(OklchLyricPalette.Map(0, 0, 0).Argb == "#FF000000"
            && OklchLyricPalette.Map(1, 0, 0).Argb == "#FFFFFFFF"
            && OklchLyricPalette.Map(.5, 0, 0).Argb == "#FF636363",
            "Linear RGB is gamma encoded to sRGB, including black, white and middle OKLab gray");
    }
}
