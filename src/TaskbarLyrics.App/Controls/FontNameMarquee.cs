using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Size = System.Windows.Size;

namespace TaskbarLyrics.App.Controls;

/// <summary>Clipped font label that pans only while its item is actively highlighted.</summary>
public sealed class FontNameMarquee : Decorator
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(FontNameMarquee), new PropertyMetadata(false, Changed));
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    private readonly TranslateTransform _translation = new();
    private double _overflow;
    private double _naturalWidth;
    public double OverflowWidth => _overflow;
    public double ScrollOffset => _translation.X;
    public AnimationClock? ScrollClock { get; private set; }
    public FontNameMarquee()
    {
        ClipToBounds = true;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => { _translation.BeginAnimation(TranslateTransform.XProperty, null); ScrollClock = null; };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((FontNameMarquee)d).UpdateAnimation();
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        _naturalWidth = Child.DesiredSize.Width;
        // Do not let a virtualizing stack's infinite measure expand the item beyond its viewport.
        // HorizontalContentAlignment=Stretch supplies the actual row width at arrange time.
        return new Size(0, Child.DesiredSize.Height);
    }
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null) return arrangeSize;
        Child.RenderTransform = _translation;
        Child.Arrange(new Rect(0, 0, Math.Max(arrangeSize.Width, _naturalWidth), arrangeSize.Height));
        var overflow = Math.Max(0, _naturalWidth - arrangeSize.Width);
        if (Math.Abs(overflow - _overflow) > .01) { _overflow = overflow; UpdateAnimation(); }
        return arrangeSize;
    }
    private void UpdateAnimation()
    {
        _translation.BeginAnimation(TranslateTransform.XProperty, null);
        ScrollClock = null;
        _translation.X = 0;
        if (!IsLoaded || !IsActive || _overflow <= .5 || !Motion.Allowed(this)) return;
        var travel = Math.Max(1, _overflow / 35);
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.8))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-_overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.8 + travel))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-_overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6 + travel))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6 + 2 * travel))));
        ScrollClock = animation.CreateClock();
        _translation.ApplyAnimationClock(TranslateTransform.XProperty, ScrollClock);
    }
}
