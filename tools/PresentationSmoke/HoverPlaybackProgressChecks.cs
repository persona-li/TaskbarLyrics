using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Presentation;

internal static class HoverPlaybackProgressChecks
{
    private static readonly DependencyPropertyKey MouseOverKey = ReadKey("IsMouseOverPropertyKey");
    private static readonly DependencyPropertyKey CaptureWithinKey = ReadKey("IsMouseCaptureWithinPropertyKey");
    private static readonly DependencyPropertyKey FocusWithinKey = ReadKey("IsKeyboardFocusWithinPropertyKey");

    public static void Run(Action<bool, string> check)
    {
        check(PlaybackSeekSlider.ShouldSmooth(10, 10.2, 200, true, true, true), "Seek progress interpolates ordinary playback samples");
        check(!PlaybackSeekSlider.ShouldSmooth(10, 9, 200, true, true, true)
            && !PlaybackSeekSlider.ShouldSmooth(10, 20, 200, true, true, true), "Seek jumps snap in either direction");
        check(!PlaybackSeekSlider.ShouldSmooth(10, 10.2, 0, true, true, true)
            && !PlaybackSeekSlider.ShouldSmooth(10, 10.2, 200, false, true, true)
            && !PlaybackSeekSlider.ShouldSmooth(10, 10.2, 200, true, false, true)
            && !PlaybackSeekSlider.ShouldSmooth(10, 10.2, 200, true, true, false), "Unknown duration, pause, hidden and reduced motion disable smoothing");
        var calls = new List<double>();
        var slider = new Probe
        {
            Width = 320, DurationSeconds = 200, PositionSeconds = 10, TrackKey = "first",
            SeekCommand = new RelayCommand(p => calls.Add((double)p!)),
            Style = (Style)Application.Current.FindResource("HoverPlaybackProgress")
        };
        check(!slider.SmoothPlayback, "Calibration sliders retain immediate progress by default");
        var host = new Window { Content = slider, Width = 360, Height = 100, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            host.Show(); host.UpdateLayout();
            var thumb = ((Track)slider.Template.FindName("PART_Track", slider)).Thumb;
            check(slider.CanSeek && thumb.Opacity == 0, "Progress has no permanent thumb when idle");
            WithState(slider, MouseOverKey, () => check(thumb.Opacity == 1, "Hover reveals the current progress point"));
            check(thumb.Opacity == 0, "Leaving progress hides the current point immediately");
            WithState(slider, CaptureWithinKey, () =>
            {
                check(!slider.IsMouseOver && thumb.Opacity == 1, "Dragging outside progress keeps the current point visible while captured");
                slider.IsSeekEnabled = false;
                check(thumb.Opacity == 0, "Unavailable seek hides the captured point");
                slider.IsSeekEnabled = true;
            });
            check(thumb.Opacity == 0, "Releasing capture outside progress hides the point");
            WithState(slider, FocusWithinKey, () => check(thumb.Opacity == 0, "Keyboard focus alone cannot leave the progress point visible"));
            slider.IsSeekEnabled = false;
            WithState(slider, MouseOverKey, () => check(thumb.Opacity == 0, "Unavailable seek has no hover point"));
            slider.PositionSeconds = 12;
            check(!slider.CanSeek && slider.Value == 12 && thumb.Opacity == 0 && slider.Opacity == 1,
                "Unsupported seek still displays full-opacity live progress without a thumb");
            slider.IsSeekEnabled = true;
            slider.SmoothPlayback = true;
            slider.IsPlaying = true;
            slider.PositionSeconds = 12.2;
            check(slider.HasAnimatedProperties == Motion.Allowed(slider), "Playback interpolation follows system motion preference");
            check((double)slider.GetAnimationBaseValue(RangeBase.ValueProperty) == 12.2, "Smooth playback preserves exact target below the visual interpolation");
            slider.IsPlaying = false;
            check(!slider.HasAnimatedProperties && slider.Value == 12.2, "Pause stops playback interpolation immediately");
            slider.IsPlaying = true; slider.PositionSeconds = 12.4;
            slider.BeginKeyboard();
            check(!slider.HasAnimatedProperties, "User seek cancels the presentation animation");
            slider.Value = 50; slider.PositionSeconds = 13;
            check(slider.Value == 50, "Playback samples cannot move a seeking thumb");
            slider.EndKeyboard(); slider.EndKeyboard();
            check(calls.SequenceEqual(new[] { 50d }), "Hover progress commits one command per completed gesture");
            slider.IsPlaying = false; slider.BeginKeyboard(); slider.Value = 70; slider.EndKeyboard();
            check(!slider.IsPlaying && calls.SequenceEqual(new[] { 50d, 70d }), "Seeking while paused never requests playing state");
            slider.IsPlaying = true; slider.PositionSeconds = 13.2; slider.TrackKey = "second";
            check(!slider.HasAnimatedProperties && slider.Value == 13.2, "Track switch clears interpolation from the old song");
            slider.BeginKeyboard(); slider.Value = 90; slider.TrackKey = "third"; slider.EndKeyboard();
            check(calls.Count == 2 && slider.Value == 13.2, "Track switch cancels a pending seek");
            slider.SeekCommand = null; slider.PositionSeconds = 20;
            check(!slider.CanSeek && slider.Value <= 20 && thumb.Opacity == 0, "Missing seek command keeps a passive progress display");
        }
        finally { host.Close(); }
    }

    // Exercise the actual WPF trigger states without moving the user's mouse or focus.
    private static DependencyPropertyKey ReadKey(string name) =>
        (DependencyPropertyKey)(typeof(UIElement).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Missing WPF fixture key: " + name));

    private static void WithState(DependencyObject target, DependencyPropertyKey key, Action action)
    {
        var previous = target.GetValue(key.DependencyProperty);
        try { target.SetValue(key, true); action(); }
        finally { target.SetValue(key, previous); }
    }

    private sealed class Probe : PlaybackSeekSlider
    {
        private KeyEventArgs KeyArgs(RoutedEvent routedEvent) => new(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(this), 0, Key.Right) { RoutedEvent = routedEvent, Source = this };
        public void BeginKeyboard() => OnPreviewKeyDown(KeyArgs(Keyboard.PreviewKeyDownEvent));
        public void EndKeyboard() => OnKeyUp(KeyArgs(Keyboard.KeyUpEvent));
    }
}
