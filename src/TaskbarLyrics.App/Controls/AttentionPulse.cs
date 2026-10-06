using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using TaskbarLyrics.App.Services;

namespace TaskbarLyrics.App.Controls;

/// <summary>A background-only cue. The button's text and hit area never animate.</summary>
public sealed class AttentionPulse : System.Windows.Controls.Border
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(AttentionPulse), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(AttentionPulse), new PropertyMetadata(false, Changed));
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public bool IsPulsing { get; private set; }
    private bool _watching;

    static AttentionPulse() => Motion.ReduceProperty.OverrideMetadata(typeof(AttentionPulse),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, Changed));

    public AttentionPulse()
    {
        IsHitTestVisible = false;
        Opacity = 0;
        Loaded += (_, _) =>
        {
            if (!_watching)
            {
                SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
                ThemeManager.ThemeChanged += ThemeChanged;
                _watching = true;
            }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
            ThemeManager.ThemeChanged -= ThemeChanged;
            _watching = false;
            Stop(0);
        };
        IsVisibleChanged += (_, _) => Refresh();
        IsEnabledChanged += (_, _) => Refresh();
    }

    public static bool ShouldPulse(bool active, bool visible, bool reduceMotion, bool systemAnimation, bool highContrast) =>
        active && visible && Motion.ShouldAnimate(reduceMotion, systemAnimation, highContrast);

    public static DoubleAnimation CreateAnimation() => new(.3, .85, TimeSpan.FromMilliseconds(1400))
    {
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
    };

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AttentionPulse)d).Refresh();
    private void ThemeChanged() => RefreshOnDispatcher();
    private void SystemPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) RefreshOnDispatcher();
    }
    private void RefreshOnDispatcher()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh);
    }
    private void Refresh()
    {
        var shown = IsActive && IsLoaded && IsVisible && IsEnabled;
        var animate = ShouldPulse(shown, true, ReduceMotion || Motion.GetReduce(this) || App.Config?.Current.General.ReduceMotion == true,
            SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast || ThemeManager.IsHighContrast);
        if (!animate) { Stop(shown ? .7 : 0); return; }
        if (IsPulsing) return;
        Opacity = .7;
        BeginAnimation(OpacityProperty, CreateAnimation());
        IsPulsing = true;
    }
    private void Stop(double opacity)
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = opacity;
        IsPulsing = false;
    }
}
