using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

internal static class ScrollGutterChecks
{
    public static void Run(Action<bool, string> check)
    {
        var content = new Border { Height = 80 };
        var viewer = new ScrollViewer { Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Style = (Style)Application.Current.FindResource("AppScrollViewer") };
        void Layout(double height)
        {
            viewer.Measure(new Size(400, height));
            viewer.Arrange(new Rect(0, 0, 400, height));
            viewer.UpdateLayout();
        }
        Layout(200);
        var width = content.ActualWidth;
        check(Math.Abs(width - 386) < .1, "Auto scrolling reserves the scrollbar and inset before overflow");
        var bar = (System.Windows.Controls.Primitives.ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
        check(bar.Visibility == Visibility.Collapsed, "Reserved gutter does not show an unnecessary scrollbar");
        content.Height = 500;
        Layout(200);
        check(bar.Visibility == Visibility.Visible && Math.Abs(content.ActualWidth - width) < .1,
            "Expanding content shows scrollbar without changing content width");
        content.Height = 80;
        Layout(200);
        Layout(40);
        check(bar.Visibility == Visibility.Visible && Math.Abs(content.ActualWidth - width) < .1,
            "Reducing viewport height preserves content width");
        Layout(200);
        check(bar.Visibility == Visibility.Collapsed && Math.Abs(content.ActualWidth - width) < .1,
            "Restoring viewport height hides scrollbar without shifting content");
        foreach (var mode in new[] { ScrollBarVisibility.Disabled, ScrollBarVisibility.Hidden })
        {
            viewer.VerticalScrollBarVisibility = mode;
            Layout(200);
            check(Math.Abs(content.ActualWidth - 400) < .1,
                "Editors without visible vertical scrolling do not gain a gutter: " + mode);
        }
    }
}
