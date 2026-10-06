using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarLyrics.App.Controls;
internal static class InteractionMotionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var button = new Button { Content = "Apply", Width = 90 };
        var window = new Window { Content = button, Width = 240, Height = 140, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var root = (FrameworkElement)button.Template.FindName("Root", button);
            var width = button.ActualWidth;
            check(InteractionMotion.GetEnabled(button), "Shared buttons opt into press feedback");
            InteractionMotion.Apply(button, true, false);
            check(root.RenderTransform is ScaleTransform { ScaleX: .96, ScaleY: .96 } && button.ActualWidth == width, "Press feedback leaves the hit area and layout unchanged");
            InteractionMotion.Apply(button, false, true);
            InteractionMotion.Apply(button, true, true);
            Motion.SetReduce(button, true);
            check(root.RenderTransform is ScaleTransform { ScaleX: 1, ScaleY: 1 }, "Reduced motion cancels an interrupted press at its resting endpoint");
            Motion.SetReduce(button, false);
            Motion.SetChangeKey(button, "track-a");
            Motion.SetChangeKey(button, "track-b");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(!Motion.Allowed(button) || Motion.GetActiveStoryboard(button) is not null, "Latest track identity starts its reveal without waiting for the previous one");
            Motion.SetReduce(button, true);
            check(button.Opacity == 1 && Motion.GetActiveStoryboard(button) is null, "Disabling motion restores changed content immediately");
        }
        finally { window.Close(); }
    }
}
