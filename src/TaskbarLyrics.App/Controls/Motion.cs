using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Control = System.Windows.Controls.Control;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace TaskbarLyrics.App.Controls;

public static class Motion
{
    public static readonly TimeSpan PageDuration = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(180);
    private sealed class State
    {
        public long Revision;
        public long Starts;
        public Storyboard? Storyboard;
        public bool Disclosure;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();
    public static long GetTransitionStartCount(FrameworkElement element) => (States.TryGetValue(element, out var state) ? state.Starts : 0) + DropdownTransition.GetStartCount(element);
    public static Storyboard? GetActiveStoryboard(FrameworkElement element) => DropdownTransition.GetStoryboard(element) ?? (States.TryGetValue(element, out var state) ? state.Storyboard : null);
    public static readonly DependencyProperty ReduceProperty = DependencyProperty.RegisterAttached("Reduce", typeof(bool), typeof(Motion), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, ReduceChanged));
    public static bool GetReduce(DependencyObject o) => (bool)o.GetValue(ReduceProperty);
    public static void SetReduce(DependencyObject o, bool value) => o.SetValue(ReduceProperty, value);
    public static bool ShouldAnimate(bool reduce, bool systemAnimation, bool highContrast) => !reduce && systemAnimation && !highContrast;
    public static bool Allowed(DependencyObject o) => ShouldAnimate(GetReduce(o) || App.Config?.Current.General.ReduceMotion == true, SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);

    private static void ReduceChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue || o is not FrameworkElement element) return;
        DropdownTransition.Reduce(element);
        if (element is System.Windows.Controls.Primitives.ButtonBase button) InteractionMotion.Apply(button, false, false);
        if (element is CheckBox check && GetSwitch(check)) SetSwitchState(check, false);
        if (element is Expander expander && GetExpand(expander)) SetExpansion(expander, false);
        if (States.TryGetValue(element, out var state))
        {
            if (state.Disclosure) SetDisclosureState(element, false);
            else if (element is not CheckBox)
            {
                state.Revision++;
                state.Storyboard?.Remove(element); state.Storyboard = null;
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 1;
                if (element.RenderTransform is TranslateTransform) element.RenderTransform = Transform.Identity;
            }
        }
    }

    public static readonly DependencyProperty DisclosureOpenProperty = DependencyProperty.RegisterAttached("DisclosureOpen", typeof(bool), typeof(Motion), new PropertyMetadata(false, DisclosureChanged));
    public static bool GetDisclosureOpen(DependencyObject o) => (bool)o.GetValue(DisclosureOpenProperty);
    public static void SetDisclosureOpen(DependencyObject o, bool value) => o.SetValue(DisclosureOpenProperty, value);
    private static void DisclosureChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement element) return;
        var state = States.GetOrCreateValue(element);
        if (!state.Disclosure)
        {
            state.Disclosure = true;
            element.Loaded += DisclosureLoaded;
            element.Unloaded += DisclosureUnloaded;
        }
        SetDisclosureState(element, element.IsLoaded);
    }
    private static void DisclosureLoaded(object sender, RoutedEventArgs e) => SetDisclosureState((FrameworkElement)sender, false);
    private static void DisclosureUnloaded(object sender, RoutedEventArgs e) => SetDisclosureState((FrameworkElement)sender, false);
    private static void SetDisclosureState(FrameworkElement element, bool animate)
    {
        var state = States.GetOrCreateValue(element);
        var transform = element.RenderTransform as RotateTransform;
        // Read the displayed angle before removing the clock so a quick reversal is continuous.
        var from = transform?.Angle ?? 0;
        state.Storyboard?.Remove(element); state.Storyboard = null;
        if (transform is null || transform.IsFrozen) { transform = new RotateTransform(); element.RenderTransform = transform; }
        transform.BeginAnimation(RotateTransform.AngleProperty, null);
        element.RenderTransformOrigin = new Point(.5, .5);
        var target = GetDisclosureOpen(element) ? 90d : 0d;
        transform.Angle = target;
        if (!animate || !element.IsVisible || !Allowed(element) || Math.Abs(from - target) < .001) return;
        var rotate = new DoubleAnimation(from, target, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(rotate, element);
        Storyboard.SetTargetProperty(rotate, new PropertyPath("(0).(1)", UIElement.RenderTransformProperty, RotateTransform.AngleProperty));
        state.Storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        state.Storyboard.Children.Add(rotate);
        state.Storyboard.Begin(element, HandoffBehavior.SnapshotAndReplace, true);
        state.Starts++;
    }

    // Nullable defaults distinguish an attached closed state from a control that does not use this behavior.
    public static readonly DependencyProperty PopupOpenProperty = DependencyProperty.RegisterAttached("PopupOpen", typeof(bool?), typeof(Motion), new PropertyMetadata(null, (o, e) =>
    {
        if (o is Popup popup) DropdownTransition.SetPopupOpen(popup, (bool?)e.NewValue == true);
    }));
    public static bool GetPopupOpen(DependencyObject o) => (bool?)o.GetValue(PopupOpenProperty) == true;
    public static void SetPopupOpen(DependencyObject o, bool value) => o.SetValue(PopupOpenProperty, value);

    public static readonly DependencyProperty ContentOpenProperty = DependencyProperty.RegisterAttached("ContentOpen", typeof(bool?), typeof(Motion), new PropertyMetadata(null, (o, e) =>
    {
        if (o is FrameworkElement element) DropdownTransition.SetContentOpen(element, (bool?)e.NewValue == true);
    }));
    public static bool GetContentOpen(DependencyObject o) => (bool?)o.GetValue(ContentOpenProperty) == true;
    public static void SetContentOpen(DependencyObject o, bool value) => o.SetValue(ContentOpenProperty, value);

    public static readonly DependencyProperty PressedBrushProperty = DependencyProperty.RegisterAttached("PressedBrush", typeof(Brush), typeof(Motion));
    public static Brush? GetPressedBrush(DependencyObject o) => (Brush?)o.GetValue(PressedBrushProperty);
    public static void SetPressedBrush(DependencyObject o, Brush? value) => o.SetValue(PressedBrushProperty, value);

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached("HoverBrush", typeof(Brush), typeof(Motion));
    public static Brush? GetHoverBrush(DependencyObject o) => (Brush?)o.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject o, Brush? value) => o.SetValue(HoverBrushProperty, value);
    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached("Hover", typeof(bool), typeof(Motion), new PropertyMetadata(false, HoverChanged));
    public static bool GetHover(DependencyObject o) => (bool)o.GetValue(HoverProperty);
    public static void SetHover(DependencyObject o, bool value) => o.SetValue(HoverProperty, value);
    private static void HoverChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Control control) return;
        if ((bool)e.NewValue) { control.MouseEnter += HoverEnter; control.MouseLeave += HoverLeave; }
        else { control.MouseEnter -= HoverEnter; control.MouseLeave -= HoverLeave; }
    }
    private static void HoverEnter(object sender, System.Windows.Input.MouseEventArgs e) => SetHover((Control)sender, 1);
    private static void HoverLeave(object sender, System.Windows.Input.MouseEventArgs e) => SetHover((Control)sender, 0);
    private static void SetHover(Control control, double target)
    {
        if (control.Template?.FindName("HoverLayer", control) is not FrameworkElement layer) return;
        var from = layer.Opacity;
        layer.BeginAnimation(UIElement.OpacityProperty, null);
        layer.Opacity = target;
        if (Allowed(control)) layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(110)) { FillBehavior = FillBehavior.Stop });
    }

    public static Storyboard CreateRevealStoryboard(FrameworkElement element, bool backwards = false, TimeSpan? duration = null, double distance = 8)
    {
        var transform = new TranslateTransform(); element.RenderTransform = transform; element.Opacity = 1;
        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        var fade = new DoubleAnimation(0, 1, duration ?? PageDuration);
        var move = new DoubleAnimation(backwards ? -distance : distance, 0, duration ?? PageDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, element); Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        Storyboard.SetTarget(move, element); Storyboard.SetTargetProperty(move, new PropertyPath("(0).(1)", UIElement.RenderTransformProperty, TranslateTransform.YProperty));
        storyboard.Children.Add(fade); storyboard.Children.Add(move);
        return storyboard;
    }
    public static void Reveal(FrameworkElement element, bool backwards = false) => ScheduleReveal(element, element, PageDuration, backwards, 8);
    private static void ScheduleReveal(FrameworkElement element, FrameworkElement owner, TimeSpan duration, bool backwards, double distance)
    {
        var state = States.GetOrCreateValue(element); var revision = ++state.Revision;
        state.Storyboard?.Remove(element); state.Storyboard = null;
        element.RenderTransform = Transform.Identity;
        var animate = owner.IsVisible && Allowed(owner);
        element.Opacity = animate ? 0 : 1;
        if (!animate) return;
        element.Dispatcher.BeginInvoke(() =>
        {
            if (revision != state.Revision) return;
            if (!element.IsVisible || !owner.IsVisible || !Allowed(owner)) { element.Opacity = 1; return; }
            element.UpdateLayout();
            state.Storyboard = CreateRevealStoryboard(element, backwards, duration, distance);
            state.Storyboard.Begin(element, HandoffBehavior.SnapshotAndReplace, isControllable: true);
            state.Starts++;
        }, DispatcherPriority.Loaded);
    }
    public static readonly DependencyProperty ChangeKeyProperty = DependencyProperty.RegisterAttached("ChangeKey", typeof(object), typeof(Motion), new PropertyMetadata(null, (o, e) =>
    {
        if (o is FrameworkElement element && element.IsLoaded && element.IsVisible && !Equals(e.OldValue, e.NewValue))
            ScheduleReveal(element, element, TimeSpan.FromMilliseconds(150), false, 3);
    }));
    public static object GetChangeKey(DependencyObject o) => o.GetValue(ChangeKeyProperty);
    public static void SetChangeKey(DependencyObject o, object value) => o.SetValue(ChangeKeyProperty, value);

    public static readonly DependencyProperty SwitchProperty = DependencyProperty.RegisterAttached("Switch", typeof(bool), typeof(Motion), new PropertyMetadata(false, SwitchChanged));
    public static bool GetSwitch(DependencyObject o) => (bool)o.GetValue(SwitchProperty);
    public static void SetSwitch(DependencyObject o, bool value) => o.SetValue(SwitchProperty, value);
    private static void SwitchChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not CheckBox check) return;
        if ((bool)e.NewValue) { check.Loaded += SwitchLoaded; check.Checked += SwitchToggled; check.Unchecked += SwitchToggled; }
        else { check.Loaded -= SwitchLoaded; check.Checked -= SwitchToggled; check.Unchecked -= SwitchToggled; }
    }
    private static void SwitchLoaded(object sender, RoutedEventArgs e) => SetSwitchState((CheckBox)sender, false);
    private static void SwitchToggled(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        var check = (CheckBox)sender;
        if (!check.IsLoaded) { SetSwitchState(check, false); return; }
        // Binding updates (including the animation preference itself) settle before evaluating motion.
        check.Dispatcher.BeginInvoke(() => SetSwitchState(check, true), DispatcherPriority.Loaded);
    }
    public static double SwitchTravel(double width, double thumb, double inset) => Math.Max(0, width - thumb - inset);
    private static void SetSwitchState(CheckBox check, bool animate)
    {
        check.ApplyTemplate();
        if (check.Template.FindName("Track", check) is not Border track || check.Template.FindName("Thumb", check) is not FrameworkElement thumb || check.Template.FindName("SwitchFill", check) is not FrameworkElement fill) return;
        var width = track.ActualWidth > 0 ? track.ActualWidth : track.Width;
        var thumbWidth = thumb.ActualWidth > 0 ? thumb.ActualWidth : thumb.Width;
        var target = check.IsChecked == true ? SwitchTravel(width, thumbWidth, track.BorderThickness.Left + track.BorderThickness.Right + thumb.Margin.Left + thumb.Margin.Right) : 0;
        var transform = thumb.RenderTransform as TranslateTransform;
        if (transform is null || transform.IsFrozen) { transform = new TranslateTransform(); thumb.RenderTransform = transform; }
        var from = transform.X; var opacity = fill.Opacity;
        var state = States.GetOrCreateValue(check);
        state.Storyboard?.Remove(check); state.Storyboard = null;
        transform.X = target; fill.Opacity = check.IsChecked == true ? 1 : 0;
        if (!animate || !check.IsVisible || !Allowed(check)) return;
        var move = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(opacity, fill.Opacity, TimeSpan.FromMilliseconds(180));
        Storyboard.SetTarget(move, thumb); Storyboard.SetTargetProperty(move, new PropertyPath("(0).(1)", UIElement.RenderTransformProperty, TranslateTransform.XProperty));
        Storyboard.SetTarget(fade, fill); Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        state.Storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        state.Storyboard.Children.Add(move); state.Storyboard.Children.Add(fade);
        state.Storyboard.Begin(check, HandoffBehavior.SnapshotAndReplace, true);
        state.Starts++;
    }

    public static readonly DependencyProperty ExpandProperty = DependencyProperty.RegisterAttached("Expand", typeof(bool), typeof(Motion), new PropertyMetadata(false, ExpandChanged));
    public static bool GetExpand(DependencyObject o) => (bool)o.GetValue(ExpandProperty);
    public static void SetExpand(DependencyObject o, bool value) => o.SetValue(ExpandProperty, value);
    private static void ExpandChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Expander expander) return;
        if ((bool)e.NewValue) { expander.Expanded += ExpansionChanged; expander.Collapsed += ExpansionChanged; expander.Loaded += ExpansionLoaded; }
        else { expander.Expanded -= ExpansionChanged; expander.Collapsed -= ExpansionChanged; expander.Loaded -= ExpansionLoaded; }
    }
    private static void ExpansionLoaded(object sender, RoutedEventArgs e) => SetExpansion((Expander)sender, false);
    private static void ExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender)) SetExpansion((Expander)sender, true);
    }
    private static void SetExpansion(Expander expander, bool animate)
    {
        expander.ApplyTemplate();
        if (expander.Template.FindName("ExpansionHost", expander) is not Border host || host.Child is not FrameworkElement content) return;
        var state = States.GetOrCreateValue(expander); var revision = ++state.Revision;
        var expanded = expander.IsExpanded; var from = host.ActualHeight;
        host.BeginAnimation(FrameworkElement.HeightProperty, null); host.BeginAnimation(UIElement.OpacityProperty, null);
        if (!animate || !expander.IsLoaded || !Allowed(expander))
        {
            host.Height = expanded ? double.NaN : 0; host.Opacity = expanded ? 1 : 0;
            host.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        host.Visibility = Visibility.Visible;
        content.Measure(new System.Windows.Size(Math.Max(1, expander.ActualWidth), double.PositiveInfinity));
        var target = expanded ? content.DesiredSize.Height : 0;
        host.Height = from; host.Opacity = expanded ? 1 : 0;
        var height = new DoubleAnimation(from, target, ExpandDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        height.Completed += (_, _) =>
        {
            if (state.Revision != revision) return;
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            host.Height = expanded ? double.NaN : 0;
            if (!expanded) host.Visibility = Visibility.Collapsed;
        };
        host.BeginAnimation(FrameworkElement.HeightProperty, height);
        host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(expanded ? 0 : 1, expanded ? 1 : 0, ExpandDuration) { FillBehavior = FillBehavior.Stop });
    }
}
