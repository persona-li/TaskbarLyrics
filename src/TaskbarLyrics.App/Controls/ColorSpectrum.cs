using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pen = System.Windows.Media.Pen;

namespace TaskbarLyrics.App.Controls;

/// <summary>A continuous saturation/value plane. Programmatic refreshes never commit an edit.</summary>
public sealed class ColorSpectrum : FrameworkElement
{
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(
        nameof(Hue), typeof(double), typeof(ColorSpectrum),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null,
            (_, value) => Clamp((double)value, 360)));
    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
        nameof(Saturation), typeof(double), typeof(ColorSpectrum),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null,
            (_, value) => Clamp((double)value, 1)));
    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(
        nameof(Brightness), typeof(double), typeof(ColorSpectrum),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, null,
            (_, value) => Clamp((double)value, 1)));

    private static readonly LinearGradientBrush WhiteOverlay = CreateOverlay(Colors.White, Colors.Transparent, new Point(1, 0));
    private static readonly LinearGradientBrush BlackOverlay = CreateOverlay(Colors.Transparent, Colors.Black, new Point(0, 1));
    private bool _dragging;

    public double Hue { get => (double)GetValue(HueProperty); set => SetValue(HueProperty, value); }
    public double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public event EventHandler? SelectionChanged;

    public ColorSpectrum()
    {
        Focusable = true;
        Cursor = System.Windows.Input.Cursors.Cross;
        IsEnabledChanged += (_, _) => { if (!IsEnabled) EndDrag(); };
        Unloaded += (_, _) => EndDrag();
    }

    private static LinearGradientBrush CreateOverlay(Color start, Color end, Point endPoint)
    {
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), endPoint);
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.PushClip(new RectangleGeometry(rect, 8, 8));
        dc.DrawRectangle(new SolidColorBrush(FromHsv(Hue, 1, 1)), null, rect);
        dc.DrawRectangle(WhiteOverlay, null, rect);
        dc.DrawRectangle(BlackOverlay, null, rect);
        dc.Pop();
        // Keep the selection ring entirely visible, including at white/black corners.
        var inset = Math.Min(7, Math.Min(ActualWidth, ActualHeight) / 2);
        var center = new Point(Math.Clamp(Saturation * ActualWidth, inset, ActualWidth - inset),
            Math.Clamp((1 - Brightness) * ActualHeight, inset, ActualHeight - inset));
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), center, 5, 5);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), center, 5, 5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        Focus();
        _dragging = CaptureMouse();
        SelectAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        SelectAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        SelectAt(e.GetPosition(this));
        EndDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        _dragging = false;
        base.OnLostMouseCapture(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .1 : .01;
        switch (e.Key)
        {
            case Key.Left: Select(Saturation - step, Brightness); break;
            case Key.Right: Select(Saturation + step, Brightness); break;
            case Key.Up: Select(Saturation, Brightness + step); break;
            case Key.Down: Select(Saturation, Brightness - step); break;
            default: return;
        }
        e.Handled = true;
    }

    private void EndDrag()
    {
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void SelectAt(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var selection = SelectionFromPoint(point, ActualWidth, ActualHeight);
        Select(selection.Saturation, selection.Brightness);
    }

    private void Select(double saturation, double brightness)
    {
        saturation = Clamp(saturation, 1);
        brightness = Clamp(brightness, 1);
        if (Saturation == saturation && Brightness == brightness) return;
        SetCurrentValue(SaturationProperty, saturation);
        SetCurrentValue(BrightnessProperty, brightness);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public static (double Saturation, double Brightness) SelectionFromPoint(Point point, double width, double height) =>
        (width > 0 && double.IsFinite(width) ? Clamp(point.X / width, 1) : 0,
         height > 0 && double.IsFinite(height) ? 1 - Clamp(point.Y / height, 1) : 1);

    private static double Clamp(double value, double max) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, max);

    public static Color FromHsv(double hue, double saturation, double brightness, byte alpha = 255)
    {
        hue = Clamp(hue, 360) % 360;
        saturation = Clamp(saturation, 1);
        brightness = Clamp(brightness, 1);
        var chroma = brightness * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = brightness - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double Hue, double Saturation, double Brightness) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6)
            : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }
}
