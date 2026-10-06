using System.Windows.Controls;
using System.Windows.Input;

namespace TaskbarLyrics.App.Controls;

/// <summary>Opening a font list must not expose ComboBox's automatic select-all flash.</summary>
public sealed class FontPickerTextBox : TextBox
{
    public FontPickerTextBox() => SelectionOpacity = 0;

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        SelectionOpacity = 0;
        base.OnPreviewGotKeyboardFocus(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Select(CaretIndex, 0);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Explicit double-click selection remains visible; opening with one click does not select all.
        SelectionOpacity = e.ClickCount >= 2 ? 1 : 0;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0)
            SelectionOpacity = 1;
        base.OnPreviewKeyDown(e);
    }
}
