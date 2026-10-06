using System.Windows;
using System.Windows.Controls;
using TaskbarLyrics.App.Controls;

internal static class FontPickerChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(FontPicker.ComputePopupX(250, 420, 270) == -170,
            "Font popup may extend left of the field while aligning its right edge");
        check(FontPicker.ComputePopupX(250, 420, 250) + 420 == 242,
            "Font popup keeps eight pixels inside the owner right edge");
        check(FontPicker.ComputePopupX(250, 420, 230) + 420 == 222,
            "Font popup clamps right edge even when the field reaches past the owner");
        var below = FontPicker.ComputePopupVertical(400, 32, 600);
        check(!below.Above && 400 + 32 + 4 + below.MaxHeight <= 592, "Font list stays above the window bottom inset");
        var above = FontPicker.ComputePopupVertical(550, 32, 600);
        check(above.Above && 550 - 4 - above.MaxHeight >= 8, "Low font field opens upward inside the window");
        check(FontPicker.ComputePopupVertical(100, 32, 900).MaxHeight == 320, "Tall font list retains its normal height cap");
        var picker = new FontPicker();
        check(!picker.IsEditable && !picker.IsTextSearchEnabled, "Font picker only accepts explicit choices from its installed-font list");
        check(ReferenceEquals(picker.ItemContainerStyle, Application.Current.FindResource("FontPickerItemStyle")),
            "Font picker has its dedicated item style");
        var marquee = new FontNameMarquee { Child = new TextBlock { Text = new string('W', 120) } };
        marquee.Measure(new Size(160, 32));
        marquee.Arrange(new Rect(0, 0, 160, 32));
        check(marquee.DesiredSize.Width <= 160 && marquee.ClipToBounds,
            "Long font names keep a clipped fixed viewport");
        check(!marquee.IsActive, "Non-highlighted font names do not scroll");
        check(marquee.OverflowWidth > 160, "Long font label retains its true overflowing width after arrange");
        var item = new FontPickerItem { Content = new string('W', 120), Style = picker.ItemContainerStyle, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        item.Arrange(new Rect(0, 0, 260, 40));
        item.ApplyTemplate(); item.UpdateLayout();
        item.IsSelected = true;
        var itemRoot = (Border)item.Template.FindName("Root", item);
        check(((System.Windows.Media.SolidColorBrush)itemRoot.Background).Color.A == 0,
            "Previously selected font does not retain a second blue row when it is not highlighted");
        check(!ToolTipService.GetIsEnabled(item) && ((TextBlock)((FontNameMarquee)item.Template.FindName("LabelViewport", item)).Child).ToolTip is null,
            "Font names have no hover tooltip window");
        check(item.Template.Triggers.OfType<Trigger>().All(t=>t.Property==FontPickerItem.IsActiveProperty),
            "Font row fill and scrolling share the single owner-controlled active item");
        var viewport = (FontNameMarquee)item.Template.FindName("LabelViewport", item);
        check(viewport.ActualWidth <= 260 && viewport.OverflowWidth > 260,
            "Virtual-list infinite measure still produces bounded row with scrollable overflow");
        var shortLabel = new FontNameMarquee { Child = new TextBlock { Text = "Arial" }, IsActive = true };
        shortLabel.Measure(new Size(double.PositiveInfinity, 32)); shortLabel.Arrange(new Rect(0, 0, 260, 32));
        check(shortLabel.OverflowWidth == 0, "Fitting font name never receives a scrolling distance");
        var host = new Window { Content = marquee, Width = 180, Height = 70,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual };
        host.Show(); host.UpdateLayout();
        try
        {
            marquee.IsActive = true;
            if (Motion.Allowed(marquee))
            {
                check(marquee.ScrollClock is not null, "Loaded highlighted overflow starts a scroll clock");
                marquee.ScrollClock!.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(1.8), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
                check(marquee.ScrollOffset < -1, "Seeking active scroll clock moves the rendered font label left");
            }
            marquee.IsActive = false;
            check(marquee.ScrollOffset == 0 && marquee.ScrollClock is null,
                "Unhighlighted font item resets offset and removes animation");
            Motion.SetReduce(marquee, true); marquee.IsActive = true;
            check(marquee.ScrollClock is null && marquee.ScrollOffset == 0,
                "Reduced motion keeps overflowing labels still");
        }
        finally { host.Close(); }
    }
}
