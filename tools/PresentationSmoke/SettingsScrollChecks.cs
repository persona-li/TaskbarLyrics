using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Pages;

internal static class SettingsScrollChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // No view model or production startup: this fixture only exercises the real
        // settings layout and WPF's requested-versus-visible scroll offsets.
        var page = new GeneralPage();
        Motion.SetReduce(page, true);
        var window = new Window
        {
            Content = page, Width = 1000, Height = 700,
            ShowInTaskbar = false, ShowActivated = false,
            Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual
        };
        var scroll = (ScrollViewer)page.FindName("GeneralScroll");
        var maintenance = (Expander)page.FindName("MaintenanceSettings");
        var content = (FrameworkElement)page.FindName("GeneralContent");
        async Task LayoutAsync()
        {
            // ScrollViewer executes scroll commands on layout; a ScrollChanged
            // handler can enqueue the follow-up that commits the clamped offset.
            for (var i = 0; i < 3; i++)
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
            }
        }
        void Wheel(int delta)
        {
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            content.RaiseEvent(args);
            if (args.Handled) return;
            args.RoutedEvent = Mouse.MouseWheelEvent;
            content.RaiseEvent(args);
        }
        window.Show();
        try
        {
            await LayoutAsync();
            check(scroll.ScrollableHeight == 0, "Collapsed settings fixture fits its viewport");
            for (var i = 0; i < 6; i++) Wheel(-120);
            await LayoutAsync();
            maintenance.IsExpanded = true;
            await LayoutAsync();
            check(scroll.ScrollableHeight > 0 && scroll.VerticalOffset == 0,
                "Wheel input while settings fit does not become a hidden offset on expansion");

            Wheel(-120);
            await LayoutAsync();
            var downOffset = scroll.VerticalOffset;
            check(downOffset > 0, "Overflowing settings still scroll down with the wheel");
            Wheel(120);
            await LayoutAsync();
            check(scroll.VerticalOffset < downOffset, "Overflowing settings still scroll up with the wheel");

            scroll.ScrollToBottom();
            await LayoutAsync();
            check(scroll.VerticalOffset > 0, "Expanded settings fixture reaches its bottom");
            maintenance.IsExpanded = false;
            await LayoutAsync();
            check(scroll.ScrollableHeight == 0 && scroll.VerticalOffset == 0,
                "Collapsing settings removes the no-longer-valid visible scroll offset");
            maintenance.IsExpanded = true;
            await LayoutAsync();
            check(scroll.VerticalOffset == 0, "Reopening settings does not restore the pre-collapse bottom offset");

            maintenance.IsExpanded = false;
            await LayoutAsync();
            scroll.ScrollToBottom();
            await LayoutAsync();
            maintenance.IsExpanded = true;
            await LayoutAsync();
            check(scroll.VerticalOffset == 0, "An End request without overflow cannot jump to the newly expanded bottom");

            maintenance.IsExpanded = false;
            window.Height = 380;
            await LayoutAsync();
            check(scroll.ScrollableHeight > 36, "Short settings fixture already scrolls before expansion");
            scroll.ScrollToVerticalOffset(36);
            await LayoutAsync();
            maintenance.IsExpanded = true;
            await LayoutAsync();
            check(Math.Abs(scroll.VerticalOffset - 36) < .1,
                "Expanding within an already scrollable page retains the user's visible position");

            System.Windows.Controls.Primitives.ScrollBar.PageDownCommand.Execute(null, scroll);
            await LayoutAsync();
            check(scroll.VerticalOffset > 36, "Keyboard page scrolling remains available after expansion");
            var lastAction = (Button)page.FindName("CurrentManualResetButton");
            lastAction.BringIntoView();
            await LayoutAsync();
            var bounds = lastAction.TransformToAncestor(scroll).TransformBounds(new Rect(lastAction.RenderSize));
            check(bounds.Top >= 0 && bounds.Bottom <= scroll.ActualHeight,
                "BringIntoView can still reveal an action for keyboard navigation");

            window.Height = 1400;
            await LayoutAsync();
            check(scroll.ScrollableHeight == 0 && scroll.VerticalOffset == 0,
                "Enlarging the window resets a no-longer-valid offset");
            window.Height = 380;
            await LayoutAsync();
            check(scroll.VerticalOffset == 0, "Shrinking after a fit does not resurrect the prior offset");
        }
        finally
        {
            window.Close();
        }
    }
}
