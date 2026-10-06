using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskbarLyrics.App.Config;
namespace TaskbarLyrics.App.Controls;

public sealed class PaletteHueSlider : Slider
{
    public PaletteHueSlider()
    {
        var drawing = new DrawingGroup();
        var normal = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        var highlight = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        for (var h = 0; h <= 360; h++)
        {
            var pair = OklchLyricPalette.Generate(h);
            normal.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(pair.NormalColor), h / 360d));
            highlight.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(pair.HighlightColor), h / 360d));
        }
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(normal, null, new Rect(0, 0, 360, 1));
            dc.DrawRectangle(highlight, null, new Rect(0, 1, 360, 1));
        }
        drawing.Freeze();
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        brush.Freeze();
        Background = brush;
    }
}
