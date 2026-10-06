using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TaskbarLyrics.App.Controls;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

internal static class SidebarPressChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var item = new ListBoxItem { Content = "Sidebar press fixture" };
        var other = new ListBoxItem { Content = "Other fixture" };
        var list = new ListBox();
        list.Items.Add(item);
        list.Items.Add(other);
        SidebarPress.SetEnabled(item, true);
        var window = new FixtureWindow
        {
            Content = list,
            Width = 240,
            Height = 180,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(item.IsLoaded && !SidebarPress.GetIsPressed(item), "Sidebar press fixture starts loaded and released");
            var captureBefore = Mouse.Captured;
            var down = RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            check(SidebarPress.GetIsPressed(item) && !down.Handled && ReferenceEquals(Mouse.Captured, captureBefore),
                "Sidebar pointer down adds feedback without consuming input or taking capture");
            var up = RaiseMouse(item, Mouse.PreviewMouseUpEvent);
            check(!SidebarPress.GetIsPressed(item) && !up.Handled, "Sidebar pointer up removes feedback without consuming input");

            RaiseMouse(item, Mouse.PreviewMouseDownEvent, MouseButton.Right);
            check(!SidebarPress.GetIsPressed(item), "Sidebar right click does not look like a navigation press");
            RaiseMouse(item, Mouse.PreviewMouseDownEvent, handled: true);
            check(SidebarPress.GetIsPressed(item), "Sidebar observes already handled pointer down");
            RaiseMouse(window, Mouse.PreviewMouseUpEvent, handled: true);
            check(!SidebarPress.GetIsPressed(item), "Sidebar observes handled window pointer up after ListBox capture");

            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            check(!SidebarPress.GetIsPressed(item), "Sidebar moving outside clears pointer feedback");
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            check(!SidebarPress.GetIsPressed(item), "Sidebar losing mouse capture clears feedback");
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            list.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            check(!SidebarPress.GetIsPressed(item), "Sidebar observes its parent ListBox losing mouse capture");

            foreach (var key in new[] { Key.Space, Key.Enter })
            {
                var keyDown = RaiseKey(item, window, Keyboard.PreviewKeyDownEvent, key);
                check(!SidebarPress.GetIsPressed(item) && !keyDown.Handled, $"Sidebar {key} down does not create pointer feedback or consume input");
                var keyUp = RaiseKey(item, window, Keyboard.PreviewKeyUpEvent, key);
                check(!SidebarPress.GetIsPressed(item) && !keyUp.Handled, $"Sidebar {key} up leaves pointer feedback released");
            }
            RaiseKey(item, window, Keyboard.PreviewKeyDownEvent, Key.Down);
            check(!SidebarPress.GetIsPressed(item), "Sidebar arrow navigation does not create a held press");
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            RaiseKey(item, window, Keyboard.PreviewKeyDownEvent, Key.Space);
            RaiseKey(item, window, Keyboard.PreviewKeyUpEvent, Key.Space);
            check(SidebarPress.GetIsPressed(item), "Sidebar keyboard input does not release an active pointer press");
            item.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, item, other)
            {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent
            });
            check(!SidebarPress.GetIsPressed(item), "Sidebar focus loss clears pointer feedback");

            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            window.SimulateDeactivation();
            check(!SidebarPress.GetIsPressed(item), "Sidebar window deactivation cannot leave a pressed row");
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            item.IsEnabled = false;
            check(!SidebarPress.GetIsPressed(item), "Disabling a sidebar item clears its held press");
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            check(!SidebarPress.GetIsPressed(item), "Disabled sidebar items ignore pointer presses");
            item.IsEnabled = true;

            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            SidebarPress.SetEnabled(item, false);
            check(!SidebarPress.GetIsPressed(item), "Removing sidebar feedback clears its current press");
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            check(!SidebarPress.GetIsPressed(item), "Removing sidebar feedback detaches input listeners");
            SidebarPress.SetEnabled(item, true);
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            list.Items.Remove(item);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(!item.IsLoaded && !SidebarPress.GetIsPressed(item), "Unloading a sidebar item clears its press");
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            check(!SidebarPress.GetIsPressed(item), "Unloaded sidebar items have no active input listeners");
            list.Items.Add(item);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            RaiseMouse(item, Mouse.PreviewMouseDownEvent);
            check(item.IsLoaded && SidebarPress.GetIsPressed(item), "Reloading a sidebar item restores press observation once");
            RaiseMouse(window, Mouse.PreviewMouseUpEvent);
            check(!SidebarPress.GetIsPressed(item), "Reloaded sidebar items still clear through window pointer up");
        }
        finally
        {
            SidebarPress.SetEnabled(item, false);
            window.Close();
        }
    }

    private static MouseButtonEventArgs RaiseMouse(UIElement target, RoutedEvent routedEvent,
        MouseButton button = MouseButton.Left, bool handled = false)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = routedEvent,
            Handled = handled
        };
        target.RaiseEvent(args);
        return args;
    }

    private static KeyEventArgs RaiseKey(UIElement target, Window window, RoutedEvent routedEvent, Key key)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Missing fixture presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = routedEvent };
        target.RaiseEvent(args);
        return args;
    }

    private sealed class FixtureWindow : Window
    {
        public void SimulateDeactivation() => OnDeactivated(EventArgs.Empty);
    }
}
