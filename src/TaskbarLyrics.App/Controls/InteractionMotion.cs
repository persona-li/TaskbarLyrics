using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace TaskbarLyrics.App.Controls;

// Transform the template surface, keeping the hit area and surrounding layout stable.
public static class InteractionMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ButtonBase button) return;
        if ((bool)e.NewValue)
        {
            button.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down), true);
            button.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up), true);
            button.LostMouseCapture += Lost;
            button.MouseLeave += Leave;
            button.Unloaded += Unloaded;
        }
        else
        {
            button.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down));
            button.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up));
            button.LostMouseCapture -= Lost; button.MouseLeave -= Leave; button.Unloaded -= Unloaded;
            Apply(button, false, false);
        }
    }
    private static void Down(object s, MouseButtonEventArgs e) => Apply((ButtonBase)s, true, true);
    private static void Up(object s, MouseButtonEventArgs e) => Apply((ButtonBase)s, false, true);
    private static void Lost(object s, MouseEventArgs e) => Apply((ButtonBase)s, false, true);
    private static void Leave(object s, MouseEventArgs e) => Apply((ButtonBase)s, false, true);
    private static void Unloaded(object s, RoutedEventArgs e) => Apply((ButtonBase)s, false, false);
    public static void Apply(ButtonBase button, bool pressed, bool animate)
    {
        var name = button is CheckBox ? "Track" : "Root";
        if (button.Template?.FindName(name, button) is not FrameworkElement surface) return;
        if (surface.RenderTransform is not ScaleTransform scale)
        {
            if (!surface.RenderTransform.Value.IsIdentity) return;
            surface.RenderTransform = scale = new ScaleTransform(1, 1);
            surface.RenderTransformOrigin = new Point(.5, .5);
        }
        var from = scale.ScaleX;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        var target = pressed && button.IsEnabled ? .96 : 1;
        scale.ScaleX = scale.ScaleY = target;
        if (!animate || !button.IsVisible || !Motion.Allowed(button)) return;
        var motion = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(pressed ? 80 : 150))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
    }
}
