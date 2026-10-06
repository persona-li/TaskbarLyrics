using TaskbarLyrics.App.Config;
namespace TaskbarLyrics.App.Presentation;

public sealed record PalettePreset(string Name, double Hue, bool Selected = false)
{
    public string NormalColor => OklchLyricPalette.Generate(Hue).NormalColor;
    public string HighlightColor => OklchLyricPalette.Generate(Hue).HighlightColor;
    public static IReadOnlyList<PalettePreset> All { get; } = [
        new("原蓝", OklchLyricPalette.DefaultHue), new("紫藤", 305), new("玫瑰", 345),
        new("珊瑚", 25), new("焦糖", 65), new("橄榄", 105), new("松绿", 185), new("湖青", 225)];
}
