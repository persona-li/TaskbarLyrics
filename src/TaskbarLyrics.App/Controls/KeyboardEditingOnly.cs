using System.Windows;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace TaskbarLyrics.App.Controls;

/// <summary>Keeps text editing available without keyboard-driven application actions.</summary>
public static class KeyboardEditingOnly
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(KeyboardEditingOnly), new PropertyMetadata(false, EnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement root) return;
        if ((bool)e.NewValue)
        {
            root.PreviewKeyDown += FilterKey;
            root.PreviewKeyUp += FilterKey;
            root.PreviewTextInput += FilterText;
        }
        else
        {
            root.PreviewKeyDown -= FilterKey;
            root.PreviewKeyUp -= FilterKey;
            root.PreviewTextInput -= FilterText;
        }
    }

    private static void FilterKey(object sender, KeyEventArgs e)
    {
        // Window management belongs to Windows, not the application's shortcut policy.
        if (e.Key == Key.System && e.SystemKey == Key.F4) return;
        if (e.Key is Key.LWin or Key.RWin) return;
        var editor = FindTextEditor(e.OriginalSource);
        if (editor is not null && e.Key != Key.Tab && e.Key != Key.System
            && !IsComboNavigation(editor, e.Key)
            && e.Key is not (>= Key.F1 and <= Key.F24)) return;
        e.Handled = true;
    }

    private static void FilterText(object sender, TextCompositionEventArgs e)
    {
        if (FindTextEditor(e.OriginalSource) is null) e.Handled = true;
    }

    private static TextBoxBase? FindTextEditor(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is TextBoxBase editor) return editor;
        }
        return null;
    }

    private static bool IsComboNavigation(TextBoxBase editor, Key key)
    {
        for (var node = Parent(editor); node is not null; node = Parent(node))
        {
            if (node is ComboBox combo)
                return key is Key.Up or Key.Down or Key.PageUp or Key.PageDown
                    || combo.IsDropDownOpen && key is Key.Home or Key.End;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject node) => node is Visual or Visual3D
        ? VisualTreeHelper.GetParent(node)
        : node is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(node);
}
