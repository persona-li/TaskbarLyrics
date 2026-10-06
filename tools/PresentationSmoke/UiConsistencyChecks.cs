using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Pages;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.App.Windows;

internal static class UiConsistencyChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var window = new Window { Width = 800, Height = 660, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        Motion.SetReduce(window, true);
        async Task Layout() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
        try
        {
            window.Show();
            var radius = (CornerRadius)Application.Current.FindResource("AppearanceControlCornerRadius");
            foreach (var (page, name) in new (UserControl, string)[] {
                (new OverviewPage(), "PlaybackCard"), (new AppearancePage(), "AppearanceCard"),
                (new SynchronizationPage(), "CalibrationCard"), (new GeneralPage(), "GeneralCard"), (new AboutPage(), "AboutCard") })
            {
                window.Content = page; await Layout();
                var card = (Border)page.FindName(name);
                check(card.CornerRadius == radius, "All page cards use the shared corner radius");
                var edge = card.TranslatePoint(new Point(card.ActualWidth, 0), page).X;
                check(Math.Abs(edge - (page.ActualWidth - 28)) < 1, "Page card right edges align despite the overview outer scrollbar gutter");
                if (page is OverviewPage)
                {
                    var action = Descendants(page).OfType<Button>().Single(b => b.Content is "选择歌词");
                    check(double.IsNaN(action.Width) && action.ActualWidth <= 80 && action.Padding.Left == 10,
                        "Lyric action sizes to its label with compact symmetric padding");
                    var scroll = Descendants(page).OfType<ScrollViewer>().First();
                    check(scroll.ScrollableHeight < 1, "Overview has no unnecessary bottom overflow at normal height");
                }
            }
            var combo = new ComboBox { Width = 180, VerticalAlignment = VerticalAlignment.Top, ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
            window.Content = combo; await Layout(); combo.IsDropDownOpen = true; await Layout();
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var surface = (Border)combo.Template.FindName("PopupSurface", combo);
            check(Math.Abs(surface.ActualWidth - combo.ActualWidth) < .1 && surface.CornerRadius == radius,
                "Generic dropdown surface matches trigger width and shared radius");
            var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
            var itemRoot = (Border)item.Template.FindName("Root", item);
            check(itemRoot.CornerRadius == radius && itemRoot.Background.ToString() == Application.Current.FindResource("PlaybackPressedBrush").ToString(),
                "Selected dropdown item shares the blue selection palette and radius");
            combo.IsDropDownOpen = false;
            var button = new Button { Content = "More", Style = (Style)Application.Current.FindResource("GhostButton") };
            window.Content = button; await Layout();
            check(Motion.GetHoverBrush(button)?.ToString() == Application.Current.FindResource("PlaybackHoverBrush").ToString()
                && Motion.GetPressedBrush(button)?.ToString() == Application.Current.FindResource("PlaybackPressedBrush").ToString(),
                "Shared actions have consistent hover and press colors");
        }
        finally { window.Close(); }
        using var vm = new RematchViewModel(new FakeRematch());
        var dialog = new LyricsRematchWindow(vm, autoSearch: false, constrainToScreen: false) {
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Width = 580, Height = 480 };
        try
        {
            dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
            var apply = (Button)dialog.FindName("ApplyButton"); var cancel = (Button)dialog.FindName("CancelButton");
            apply.Content = "应用中…"; dialog.UpdateLayout();
            var text = new FormattedText("应用中…", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(apply.FontFamily, apply.FontStyle, apply.FontWeight, apply.FontStretch), apply.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(apply).PixelsPerDip);
            check(apply.ActualWidth == cancel.ActualWidth && text.Width <= apply.ActualWidth - apply.Padding.Left - apply.Padding.Right,
                "Equal compact rematch buttons fit the full busy label without clipping");
        }
        finally { dialog.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
