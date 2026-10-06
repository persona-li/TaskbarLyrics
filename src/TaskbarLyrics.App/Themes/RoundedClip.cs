using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskbarLyrics.App.Themes;

/// <summary>
/// Clips a Border to its CornerRadius so ScrollViewer / ItemsPresenter
/// cannot paint rectangular corners outside the rounded surface.
/// Does not affect sibling shadow layers (apply only on content surface).
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(RoundedClip),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.SizeChanged += OnSizeChanged;
            element.Loaded += OnLoaded;
            Apply(element);
        }
        else
        {
            element.SizeChanged -= OnSizeChanged;
            element.Loaded -= OnLoaded;
            element.Clip = null;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            Apply(fe);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            Apply(fe);
        }
    }

    private static void Apply(FrameworkElement element)
    {
        var w = element.ActualWidth;
        var h = element.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        double rx = 12;
        double ry = 12;
        if (element is Border border)
        {
            // Use smallest corner so all four match the visual radius.
            var cr = border.CornerRadius;
            rx = Math.Min(Math.Min(cr.TopLeft, cr.TopRight), Math.Min(cr.BottomLeft, cr.BottomRight));
            ry = rx;
        }

        // Inset slightly so border stroke stays inside clip at DPI scales.
        var geom = new RectangleGeometry(new Rect(0, 0, w, h), rx, ry)
        {
            // Freeze after first apply would break updates — recreate each time.
        };
        element.Clip = geom;
    }
}
