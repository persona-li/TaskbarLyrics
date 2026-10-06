using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace TaskbarLyrics.App.Controls;

public static class ComboBoxInteraction
{
    public static readonly DependencyProperty OpenOnClickProperty = DependencyProperty.RegisterAttached("OpenOnClick", typeof(bool), typeof(ComboBoxInteraction), new PropertyMetadata(false, Changed));
    public static bool GetOpenOnClick(DependencyObject o) => (bool)o.GetValue(OpenOnClickProperty);
    public static void SetOpenOnClick(DependencyObject o, bool value) => o.SetValue(OpenOnClickProperty, value);
    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ComboBox combo) return;
        if ((bool)e.NewValue)
        {
            combo.PreviewMouseLeftButtonDown += OnMouseDown;
        }
        else
        {
            combo.PreviewMouseLeftButtonDown -= OnMouseDown;
        }
    }
    private static void OnMouseDown(object sender, MouseButtonEventArgs e) => OpenEditableField((ComboBox)sender, e.OriginalSource as DependencyObject, e.ClickCount);
    public static bool OpenEditableField(ComboBox combo, DependencyObject? source, int clickCount)
    {
        if (!combo.IsEditable || combo.IsDropDownOpen || !combo.IsEnabled || clickCount != 1) return false;
        var node = source;
        while (node is not null && !ReferenceEquals(node, combo))
        {
            if (node is TextBox editor && editor.Name == "PART_EditableTextBox")
            {
                combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
                // Leave the mouse event unhandled so caret placement/selection/typing still work.
                return true;
            }
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
