using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TaskbarLyrics.App.Controls;
using Control = System.Windows.Controls.Control;
using SelectionMode = System.Windows.Controls.SelectionMode;

internal static class KeyboardEditingOnlyChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Synthetic routed input only: never send keys to the user's desktop or load settings.
        var button = new Button { Content = "Action" };
        var list = new ListBox { SelectionMode = SelectionMode.Single };
        list.Items.Add(new ListBoxItem { Content = "First" });
        list.Items.Add(new ListBoxItem { Content = "Second" });
        list.SelectedIndex = 0;
        var editor = new TextBox { Text = "Editable value" };
        var fontNames = new[] { "Fixture First", "Fixture Second", "Fixture Third" };
        var fontPicker = new FontPicker
        {
            IsEditable = true, // Exercise the optional editor keyboard policy independently of the selection-only default.
            CatalogProvider = () => Task.FromResult(fontNames),
            ItemsSource = fontNames,
            SelectedIndex = 1
        };
        var panel = new StackPanel();
        panel.Children.Add(button);
        panel.Children.Add(list);
        panel.Children.Add(editor);
        panel.Children.Add(fontPicker);
        var window = new Window
        {
            Content = panel, Width = 280, Height = 240,
            Left = -20000, Top = -20000, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        var popupButton = new Button { Content = "Popup action" };
        var popupEditor = new TextBox { Text = "#FF496DBF" };
        var popupRoot = new StackPanel { Width = 220 };
        popupRoot.Children.Add(popupButton);
        popupRoot.Children.Add(popupEditor);
        var popup = new Popup
        {
            Child = popupRoot, Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = -20000, VerticalOffset = -20000, StaysOpen = true
        };
        var buttonEvents = 0;
        var popupEvents = 0;
        var clicks = 0;
        button.PreviewKeyDown += (_, _) => buttonEvents++;
        button.PreviewKeyUp += (_, _) => buttonEvents++;
        popupButton.PreviewKeyDown += (_, _) => popupEvents++;
        popupButton.PreviewKeyUp += (_, _) => popupEvents++;
        button.Click += (_, _) => clicks++;
        popupButton.Click += (_, _) => clicks++;
        KeyboardEditingOnly.SetEnabled(window, true);
        KeyboardEditingOnly.SetEnabled(popupRoot, true);
        try
        {
            window.Show();
            window.UpdateLayout();
            popup.IsOpen = true;
            popupRoot.UpdateLayout();
            var blockedKeys = new[]
            {
                Key.A, Key.D1, Key.F6, Key.Left, Key.Down,
                Key.Space, Key.Enter, Key.Escape, Key.Tab
            };
            foreach (var key in blockedKeys)
            {
                var down = RaiseKey(button, Keyboard.PreviewKeyDownEvent, key);
                var up = RaiseKey(button, Keyboard.PreviewKeyUpEvent, key);
                check(down.Handled && up.Handled && buttonEvents == 0 && clicks == 0,
                    $"Application {key} input is stopped before a button receives it");
                var listDown = RaiseKey((ListBoxItem)list.Items[0], Keyboard.PreviewKeyDownEvent, key);
                var listUp = RaiseKey((ListBoxItem)list.Items[0], Keyboard.PreviewKeyUpEvent, key);
                check(listDown.Handled && listUp.Handled && list.SelectedIndex == 0,
                    $"Application {key} input does not move the selected navigation item");
                var popupDown = RaiseKey(popupButton, Keyboard.PreviewKeyDownEvent, key);
                var popupUp = RaiseKey(popupButton, Keyboard.PreviewKeyUpEvent, key);
                check(popupDown.Handled && popupUp.Handled && popupEvents == 0 && clicks == 0,
                    $"Detached popup root blocks {key} action input");
            }

            foreach (var key in new[] { Key.A, Key.D1, Key.Left, Key.Right, Key.Home, Key.End,
                         Key.Back, Key.Delete, Key.Space, Key.Enter, Key.Escape })
            {
                check(!RaiseKey(editor, Keyboard.PreviewKeyDownEvent, key).Handled
                    && !RaiseKey(editor, Keyboard.PreviewKeyUpEvent, key).Handled,
                    $"Text editor retains {key} editing input");
                check(!RaiseKey(popupEditor, Keyboard.PreviewKeyDownEvent, key).Handled
                    && !RaiseKey(popupEditor, Keyboard.PreviewKeyUpEvent, key).Handled,
                    $"Popup text editor retains {key} editing input");
            }
            foreach (var target in new[] { editor, popupEditor })
            {
                check(RaiseKey(target, Keyboard.PreviewKeyDownEvent, Key.Tab).Handled
                    && RaiseKey(target, Keyboard.PreviewKeyDownEvent, Key.F6).Handled,
                    "Text editing does not restore Tab or F6 application navigation");
            }
            check(RaiseText(button, "a").Handled && RaiseText(popupButton, "a").Handled,
                "Type-to-select text is blocked on application and popup actions");
            check(!RaiseText(editor, "a").Handled && !RaiseText(popupEditor, "a").Handled,
                "Text composition still reaches application and popup text editors");

            CheckFontEditor(fontPicker, check);

            KeyboardEditingOnly.SetEnabled(popupRoot, false);
            check(!RaiseKey(popupButton, Keyboard.PreviewKeyDownEvent, Key.A).Handled && popupEvents == 1,
                "Disabling a detached popup policy restores its local keyboard route");
            check(RaiseKey(button, Keyboard.PreviewKeyDownEvent, Key.A).Handled,
                "Disabling a popup policy does not disable the window policy");
            KeyboardEditingOnly.SetEnabled(window, false);
            check(!RaiseKey(button, Keyboard.PreviewKeyDownEvent, Key.A).Handled && buttonEvents == 1,
                "Disabling the window policy detaches its input filter");
            check(!RaiseText(button, "a").Handled,
                "Disabling the window policy restores ordinary text routing");

            CheckFocusTemplate(window, "KeyboardFocus", check);
            CheckFocusTemplate(window, SystemParameters.FocusVisualStyleKey, check);
        }
        finally
        {
            fontPicker.IsDropDownOpen = false;
            popup.IsOpen = false;
            KeyboardEditingOnly.SetEnabled(popupRoot, false);
            KeyboardEditingOnly.SetEnabled(window, false);
            window.Close();
        }
    }

    private static void CheckFontEditor(FontPicker picker, Action<bool, string> check)
    {
        picker.ApplyTemplate();
        var editor = picker.Template.FindName("PART_EditableTextBox", picker) as TextBox;
        check(editor is FontPickerTextBox,
            "Keyboard regression uses the real editable font picker template");
        if (editor is null) return;
        // The injected completed catalog avoids enumerating installed user fonts.
        picker.EnsureCatalogAsync().GetAwaiter().GetResult();
        picker.SelectedIndex = 1;
        var selected = picker.SelectedItem;
        foreach (var open in new[] { false, true })
        {
            picker.IsDropDownOpen = open;
            picker.UpdateLayout();
            foreach (var key in new[] { Key.Up, Key.Down, Key.PageUp, Key.PageDown })
            {
                var down = RaiseKey(editor, Keyboard.PreviewKeyDownEvent, key);
                var blocked = down.Handled;
                down.RoutedEvent = Keyboard.KeyDownEvent;
                editor.RaiseEvent(down);
                var up = RaiseKey(editor, Keyboard.PreviewKeyUpEvent, key);
                check(blocked && up.Handled && Equals(picker.SelectedItem, selected),
                    $"Font editor {key} cannot change a font while its dropdown is {(open ? "open" : "closed")}");
            }
            foreach (var key in new[] { Key.Home, Key.End })
            {
                var down = RaiseKey(editor, Keyboard.PreviewKeyDownEvent, key);
                var up = RaiseKey(editor, Keyboard.PreviewKeyUpEvent, key);
                check(down.Handled == open && up.Handled == open && Equals(picker.SelectedItem, selected),
                    $"Font editor {key} {(open ? "cannot navigate dropdown options" : "remains available for text editing")}");
            }
        }
        picker.IsDropDownOpen = false;
        foreach (var key in new[] { Key.Left, Key.Right, Key.Home, Key.End })
            check(!RaiseKey(editor, Keyboard.PreviewKeyDownEvent, key).Handled
                && !RaiseKey(editor, Keyboard.PreviewKeyUpEvent, key).Handled
                && Equals(picker.SelectedItem, selected),
                $"Closed font editor retains {key} cursor editing without changing its selected font");
    }

    private static KeyEventArgs RaiseKey(UIElement target, RoutedEvent routedEvent, Key key)
    {
        var source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("Missing keyboard fixture presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = routedEvent };
        target.RaiseEvent(args);
        return args;
    }

    private static TextCompositionEventArgs RaiseText(UIElement target, string text)
    {
        var composition = new TextComposition(InputManager.Current, target, text);
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent
        };
        target.RaiseEvent(args);
        return args;
    }

    private static void CheckFocusTemplate(FrameworkElement root, object key, Action<bool, string> check)
    {
        var style = root.TryFindResource(key) as Style;
        check(style is not null, $"Focus policy resource {key} exists");
        if (style is null) return;
        var probe = new Control { Style = style };
        probe.ApplyTemplate();
        check(VisualTreeHelper.GetChildrenCount(probe) == 1
            && VisualTreeHelper.GetChild(probe, 0) is Grid { Children.Count: 0, Background: null },
            $"Focus policy resource {key} renders no selection rectangle");
    }
}
