using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Presentation;
using Binding = System.Windows.Data.Binding;

internal static class PlaybackSeekSliderChecks
{
    public static void Run(Action<bool, string> check)
    {
        var seeks = new List<double>();
        var permitted = true;
        var model = new ProgressModel();
        var slider = new InputProbe
        {
            Width = 360,
            DurationSeconds = 120,
            TrackKey = "first",
            SeekCommand = new RelayCommand(p => seeks.Add((double)p!), _ => permitted)
        };
        slider.SetBinding(PlaybackSeekSlider.PositionSecondsProperty,
            new Binding(nameof(ProgressModel.Position)) { Source = model, Mode = BindingMode.OneWay });
        var window = new Window
        {
            Width = 420, Height = 120, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Content = slider
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            check(slider.Template != null && slider.IsMoveToPointEnabled,
                "Playback progress inherits the app Slider template and supports track clicks");
            model.Position = 12;
            check(slider.Value == 12 && seeks.Count == 0,
                "Playback progress binding updates without issuing media commands");

            slider.SimulateKeyDown(Key.Right);
            slider.Value = 25;
            model.Position = 14;
            check(slider.Value == 25 && seeks.Count == 0,
                "Incoming playback samples do not move the thumb while seeking");
            slider.KeyReleased(Key.Right);
            slider.KeyReleased(Key.Right);
            check(seeks.SequenceEqual(new[] { 25d }), "Keyboard seek commits once on key release");

            slider.SimulateMouseDown();
            slider.Value = 38;
            model.Position = 16;
            check(slider.Value == 38 && seeks.Count == 1, "Mouse seek waits for release");
            slider.MouseReleased();
            slider.MouseReleased();
            check(seeks.SequenceEqual(new[] { 25d, 38d }), "Mouse seek commits once on release");

            slider.SimulateKeyDown(Key.End);
            slider.Value = 1000;
            slider.KeyReleased(Key.End);
            slider.SimulateKeyDown(Key.Home);
            slider.Value = -1000;
            slider.KeyReleased(Key.Home);
            check(seeks[^2] == 120 && seeks[^1] == 0, "Seek requests stay inside the playable duration");

            var beforeCancel = seeks.Count;
            slider.SimulateMouseDown();
            slider.Value = 80;
            slider.TrackKey = "second";
            model.Position = 2;
            slider.MouseReleased();
            check(seeks.Count == beforeCancel && slider.Value == 2,
                "Changing tracks cancels the old drag rather than seeking the new song");

            slider.SimulateKeyDown(Key.Right);
            slider.Value = 70;
            slider.TrackKey = "third";
            slider.SimulateKeyDown(Key.Right);
            slider.KeyReleased(Key.Right);
            check(seeks.Count == beforeCancel && slider.Value == 2,
                "A held keyboard gesture cannot restart itself after a track change");

            slider.SimulateKeyDown(Key.PageUp);
            slider.Value = 50;
            slider.LoseFocus();
            slider.KeyReleased(Key.PageUp);
            check(seeks.Count == beforeCancel && slider.Value == 2, "Losing focus cancels a pending seek");

            slider.SimulateKeyDown(Key.PageDown);
            slider.Value = 60;
            slider.IsEnabled = false;
            slider.KeyReleased(Key.PageDown);
            check(seeks.Count == beforeCancel && slider.Value == 2, "Disabling progress cancels a pending seek");
            slider.IsEnabled = true;

            permitted = false;
            slider.SimulateKeyDown(Key.Right);
            slider.Value = 20;
            slider.KeyReleased(Key.Right);
            check(seeks.Count == beforeCancel && slider.Value == 2,
                "A command that stops accepting seeks cannot receive a pending request");
            permitted = true;

            slider.SimulateMouseDown();
            slider.Value = 90;
            slider.DurationSeconds = 0;
            slider.MouseReleased();
            check(!slider.IsEnabled && slider.Value == 0 && seeks.Count == beforeCancel,
                "Unknown duration disables seeking and cancels the previous gesture");
            slider.DurationSeconds = double.NaN;
            check(!slider.IsEnabled && slider.Maximum == 0, "Non-finite duration is treated as unknown");
            slider.DurationSeconds = 30;
            model.Position = double.PositiveInfinity;
            check(slider.IsEnabled && slider.Value == 0, "Progress recovers when duration becomes known");
            model.Position = 4;
            check(slider.Value == 4 && seeks.Count == beforeCancel,
                "Cancelled gestures resume the live binding without requesting playback");
        }
        finally { window.Close(); }
    }

    private sealed class ProgressModel : INotifyPropertyChanged
    {
        private double _position;
        public double Position
        {
            get => _position;
            set { _position = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class InputProbe : PlaybackSeekSlider
    {
        private KeyEventArgs KeyArgs(Key key, RoutedEvent routedEvent) =>
            new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, key) { RoutedEvent = routedEvent, Source = this };
        public void SimulateKeyDown(Key key) => OnPreviewKeyDown(KeyArgs(key, Keyboard.PreviewKeyDownEvent));
        public void KeyReleased(Key key) => OnKeyUp(KeyArgs(key, Keyboard.KeyUpEvent));
        public void SimulateMouseDown() => OnPreviewMouseLeftButtonDown(
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonDownEvent, Source = this });
        public void MouseReleased() => OnPreviewMouseLeftButtonUp(
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent, Source = this });
        public void LoseFocus() => OnLostKeyboardFocus(
            new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, this, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent, Source = this });
    }
}
