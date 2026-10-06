using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;

namespace TaskbarLyrics.App.Controls;

/// <summary>Observes sidebar pointer presses without changing ListBox selection or mouse capture.</summary>
public static class SidebarPress
{
    private static readonly ConditionalWeakTable<ListBoxItem, PressState> States = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SidebarPress), new PropertyMetadata(false, EnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static readonly DependencyPropertyKey IsPressedPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsPressed", typeof(bool), typeof(SidebarPress), new PropertyMetadata(false));

    public static readonly DependencyProperty IsPressedProperty = IsPressedPropertyKey.DependencyProperty;
    public static bool GetIsPressed(DependencyObject element) => (bool)element.GetValue(IsPressedProperty);

    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ListBoxItem item) return;
        if ((bool)e.NewValue)
        {
            States.GetValue(item, static owner => new PressState(owner)).Attach();
        }
        else if (States.TryGetValue(item, out var state))
        {
            state.Detach();
            States.Remove(item);
        }
    }

    private sealed class PressState(ListBoxItem item)
    {
        private bool _attached;
        private bool _listening;
        private bool _pointerDown;
        private Window? _window;

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            item.Loaded += Loaded;
            item.Unloaded += Unloaded;
            if (item.IsLoaded) Listen();
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            item.Loaded -= Loaded;
            item.Unloaded -= Unloaded;
            StopListening();
        }

        private void Loaded(object sender, RoutedEventArgs e) => Listen();
        private void Unloaded(object sender, RoutedEventArgs e) => StopListening();

        private void Listen()
        {
            if (_listening || !_attached) return;
            _listening = true;
            item.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(MouseDown), true);
            item.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(MouseUp), true);
            item.AddHandler(Mouse.MouseLeaveEvent, new MouseEventHandler(MouseLeave), true);
            item.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(LostCapture), true);
            item.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(LostFocus), true);
            item.IsEnabledChanged += IsEnabledChanged;
        }

        private void StopListening()
        {
            Reset();
            if (!_listening) return;
            _listening = false;
            item.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(MouseDown));
            item.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(MouseUp));
            item.RemoveHandler(Mouse.MouseLeaveEvent, new MouseEventHandler(MouseLeave));
            item.RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(LostCapture));
            item.RemoveHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(LostFocus));
            item.IsEnabledChanged -= IsEnabledChanged;
        }

        private void MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !item.IsEnabled) return;
            _pointerDown = true;
            Update();
        }

        private void MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _pointerDown = false;
            Update();
        }

        private void MouseLeave(object sender, MouseEventArgs e)
        {
            _pointerDown = false;
            Update();
        }

        private void LostCapture(object sender, MouseEventArgs e) => Reset();
        private void LostFocus(object sender, KeyboardFocusChangedEventArgs e) => Reset();

        private void IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(bool)e.NewValue) Reset();
        }

        private void Update()
        {
            var pressed = _pointerDown;
            item.SetValue(IsPressedPropertyKey, pressed);
            if (pressed && _window is null)
            {
                _window = Window.GetWindow(item);
                if (_window is not null)
                {
                    // The ListBox may capture the mouse; an up event need not return to this item.
                    _window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(MouseUp), true);
                    _window.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(LostCapture), true);
                    _window.Deactivated += WindowDeactivated;
                }
            }
            else if (!pressed)
            {
                StopWatchingWindow();
            }
        }

        private void WindowDeactivated(object? sender, EventArgs e) => Reset();

        private void Reset()
        {
            _pointerDown = false;
            item.SetValue(IsPressedPropertyKey, false);
            StopWatchingWindow();
        }

        private void StopWatchingWindow()
        {
            if (_window is null) return;
            _window.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(MouseUp));
            _window.RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(LostCapture));
            _window.Deactivated -= WindowDeactivated;
            _window = null;
        }
    }
}
