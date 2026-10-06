using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace TaskbarLyrics.App.Controls;

/// <summary>Presentation-only interpolation; never changes the playback clock or seeks audio.</summary>
public sealed class SmoothProgressBar : System.Windows.Controls.ProgressBar
{
    public static readonly DependencyProperty TargetValueProperty = DependencyProperty.Register(
        nameof(TargetValue), typeof(double), typeof(SmoothProgressBar),
        new PropertyMetadata(0d, TargetChanged, (_, value) => double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 100) : 0d));
    public static readonly DependencyProperty TrackKeyProperty = DependencyProperty.Register(
        nameof(TrackKey), typeof(string), typeof(SmoothProgressBar), new PropertyMetadata("", ResetChanged));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(SmoothProgressBar), new PropertyMetadata(false, ResetChanged));
    public double TargetValue { get => (double)GetValue(TargetValueProperty); set => SetValue(TargetValueProperty, value); }
    public string TrackKey { get => (string)GetValue(TrackKeyProperty); set => SetValue(TrackKeyProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

    public SmoothProgressBar()
    {
        Maximum = 100;
        Loaded += (_, _) => Snap();
        Unloaded += (_, _) => Snap();
        IsVisibleChanged += (_, _) => Snap();
    }

    public static bool ShouldSmooth(double previous, double next, bool playing, bool visible, bool animations) =>
        playing && visible && animations && next > previous && next - previous <= 1;

    private static void ResetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SmoothProgressBar)d).Snap();
    private static void TargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (SmoothProgressBar)d;
        var from = bar.Value;
        var smooth = ShouldSmooth((double)e.OldValue, bar.TargetValue, bar.IsPlaying, bar.IsVisible && bar.IsLoaded, Motion.Allowed(bar));
        bar.Snap();
        if (smooth)
            bar.BeginAnimation(ValueProperty, new DoubleAnimation(from, bar.TargetValue, TimeSpan.FromMilliseconds(250))
            { FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private void Snap()
    {
        BeginAnimation(ValueProperty, null);
        // Value is owned by this control; callers bind TargetValue instead.
        // SetCurrentValue's coerced value is lost when an animation is attached,
        // so commit the local base value before interpolating it.
        SetValue(ValueProperty, TargetValue);
    }
}
