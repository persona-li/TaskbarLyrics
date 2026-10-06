using System.Text.Json.Serialization;
using System.Windows.Media;

namespace TaskbarLyrics.App.Config;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public DisplayConfig Display { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();
    public KaraokeConfig Karaoke { get; set; } = new();
    public LyricsUserConfig Lyrics { get; set; } = new();
    public GeneralConfig General { get; set; } = new();

    public static AppConfig CreateDefault() => new();
}

public sealed class LyricsUserConfig
{
    /// <summary>+100 = lyrics appear ~100ms earlier (effectivePosition later).</summary>
    public long GlobalOffsetMs { get; set; }
}

public sealed class GeneralConfig
{
    public bool ShowOverlay { get; set; } = true;
    public bool ReduceMotion { get; set; }
    /// <summary>Default on for install/release; registry is source of truth at runtime.</summary>
    public bool StartWithWindows { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public List<string> RecentColors { get; set; } = [];
    public List<string> RecentNormalColors { get; set; } = [];
    public List<string> RecentHighlightColors { get; set; } = [];
    public List<string> RecentShadowColors { get; set; } = [];
}

public sealed class DisplayConfig
{
    public double PaletteHue { get; set; } = OklchLyricPalette.DefaultHue;
    public double FontSize { get; set; } = 14.0;
    public FontConfig Fonts { get; set; } = new();
    public string NormalColor { get; set; } = "#FF496DBF";
    public string HighlightColor { get; set; } = "#FFA0CCEE";
    public bool ShadowEnabled { get; set; } = true;
    public string ShadowColor { get; set; } = "#80000000";
    public double ShadowOffsetX { get; set; } = 1.0;
    public double ShadowOffsetY { get; set; } = 1.0;
    public string TextAlignment { get; set; } = "Center";
    public double VerticalOffsetPx { get; set; }

    [JsonIgnore] public Color ParsedNormalColor { get; set; }
    [JsonIgnore] public Color ParsedHighlightColor { get; set; }
    [JsonIgnore] public Color ParsedShadowColor { get; set; }
    [JsonIgnore] public bool AlignCenter { get; set; } = true;
}

public sealed class FontConfig
{
    public const string DefaultFamily = "Microsoft YaHei UI";
    public string Chinese { get; set; } = DefaultFamily;
    public string Japanese { get; set; } = DefaultFamily;
    public string Korean { get; set; } = DefaultFamily;
    public string Latin { get; set; } = DefaultFamily;
    public string Cyrillic { get; set; } = DefaultFamily;
    public string Arabic { get; set; } = DefaultFamily;
    public string Other { get; set; } = DefaultFamily;
}

public sealed class OverlayConfig
{
    public bool AutoFit { get; set; } = true;
    /// <summary>Hard cap as fraction of taskbar width (center safety).</summary>
    public const double MaxTaskbarWidthFraction = 0.69;

    public int LeftMarginPx { get; set; } = 8;
    /// <summary>~1.5× previous default (0.40 → 0.60).</summary>
    public double WidthRatio { get; set; } = 0.60;
    public int MinWidthPx { get; set; } = 750;
    public int MaxWidthPx { get; set; } = 1350;
    public int RightSafetyMarginPx { get; set; } = 40;
}

public sealed class KaraokeConfig
{
    public bool Enabled { get; set; } = true;
    public int RenderHz { get; set; } = 30;
    public bool AutoPanLongLyrics { get; set; } = true;
    public int AutoPanRightPaddingPx { get; set; } = 60;
}
