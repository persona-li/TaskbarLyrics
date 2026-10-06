namespace TaskbarLyrics.App.Config;

/// <summary>Only lyric colors/shadow visibility; restoring this must not reset fonts or timing.</summary>
public sealed record LyricColorScheme(string Normal, string Highlight, string Shadow, bool ShadowEnabled)
{
    public static LyricColorScheme Capture(AppConfig config) => FromDisplay(config.Display);
    public static LyricColorScheme Default => FromDisplay(new DisplayConfig());
    private static LyricColorScheme FromDisplay(DisplayConfig display) => new(display.NormalColor, display.HighlightColor, display.ShadowColor, display.ShadowEnabled);
    public void Apply(AppConfig config)
    {
        config.Display.NormalColor = Normal;
        config.Display.HighlightColor = Highlight;
        if (Normal == "#FF496DBF" && Highlight == "#FFA0CCEE")
            config.Display.PaletteHue = OklchLyricPalette.DefaultHue;
        config.Display.ShadowColor = Shadow;
        config.Display.ShadowEnabled = ShadowEnabled;
    }
}
