using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskbarLyrics.App.Controls;

// Show full text only when the visible text has been clipped or ellipsized.
public static class OverflowHint
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(OverflowHint), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock text) return;
        if ((bool)e.NewValue) text.ToolTipOpening += Opening;
        else text.ToolTipOpening -= Opening;
    }
    private static void Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock text) return;
        var formatted = new FormattedText(text.Text ?? "", CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        var width = Math.Max(1, text.ActualWidth - text.Padding.Left - text.Padding.Right);
        if (text.TextWrapping != TextWrapping.NoWrap) formatted.MaxTextWidth = width;
        e.Handled = formatted.WidthIncludingTrailingWhitespace <= width + .5
            && formatted.Height <= text.ActualHeight - text.Padding.Top - text.Padding.Bottom + .5;
    }
}
