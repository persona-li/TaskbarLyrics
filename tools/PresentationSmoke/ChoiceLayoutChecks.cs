using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Pages;

internal static class ChoiceLayoutChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        using var config = new TaskbarLyrics.App.Config.ConfigService(_ => { }, _ => { }, (_, _) => { }, System.IO.Path.Combine(root, "choice-layout"), watchFile: false, fontExists: _ => true);
        using var vm = new TaskbarLyrics.App.Presentation.SettingsViewModel(config, new FakeSettings());
        var window = new Window
        {
            Width = 1040, Height = 850, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        Motion.SetReduce(window, true);
        async Task LayoutAsync()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        try
        {
            var page = new SynchronizationPage();
            var choices = new[] { "TimingStepChoice", "GlobalTimingStepChoice" }
                .Select(name => (ComboBox)page.FindName(name)).ToArray();
            // No settings backend: neither real media sessions nor user configuration
            // are needed to exercise the exact templates used on both adjustment rows.
            foreach (var choice in choices)
            {
                choice.ItemsSource = new[] { 50d, 100d, 500d };
                choice.SelectedItem = 100d;
            }
            page.DataContext = vm;
            void SetStep(double step) => vm.TimingStep = step;
            SetStep(100);
            window.Content = page;
            window.Show();
            await LayoutAsync();
            var scroll = (ScrollViewer)page.FindName("CalibrationScroll");
            foreach (var height in new[] { 850d, 430d })
            {
                window.Height = height;
                await LayoutAsync();
                foreach (var atBottom in new[] { false, true })
                {
                    scroll.ScrollToVerticalOffset(atBottom ? scroll.ScrollableHeight : 0);
                    await LayoutAsync();
                    var offset = scroll.VerticalOffset;
                    var labels = Descendants(page).OfType<TextBlock>().Where(t => t.Name is "ActionPrefix" or "ActionUnit" or "AmountUnit").ToArray();
                    var labelOrigins = labels.Select(t => t.TranslatePoint(new Point(), page)).ToArray();
                    foreach (var choice in choices)
                    {
                        var prefix = (FrameworkElement)choice.Template.FindName("StepPrefix", choice);
                        var value = (FrameworkElement)choice.Template.FindName("StepValue", choice);
                        var arrow = (FrameworkElement)choice.Template.FindName("DisclosureArrow", choice);
                        var popup = (Popup)choice.Template.FindName("PART_Popup", choice);
                        var prefixOrigin = prefix.TranslatePoint(new Point(), choice);
                        var valueRight = value.TranslatePoint(new Point(value.ActualWidth, 0), choice).X;
                        var arrowBounds = Bounds(arrow, choice);
                        var pageBounds = Bounds(choice, page);
                        Size? menuSize = null;
                        foreach (var step in new[] { 50d, 100d, 500d })
                        {
                            choice.IsDropDownOpen = true;
                            await LayoutAsync();
                            var selectedRow = (ComboBoxItem)choice.ItemContainerGenerator.ContainerFromIndex(Array.IndexOf(new[] { 50d, 100d, 500d }, step));
                            selectedRow.BringIntoView();
                            await LayoutAsync();
                            check(Close(scroll.VerticalOffset, offset), $"Opening/selecting a timing menu must not scroll the page: height={height}, bottom={atBottom}, choice={choice.Name}, step={step}, offset={offset}->{scroll.VerticalOffset}");
                            SetStep(step);
                            foreach (var synchronizedChoice in choices) synchronizedChoice.SelectedItem = step;
                            await LayoutAsync();
                            var surface = (FrameworkElement)popup.Child;
                            menuSize ??= surface.RenderSize;
                            check(Close(surface.ActualWidth, menuSize.Value.Width) && Close(surface.ActualHeight, menuSize.Value.Height),
                                "Timing step menu keeps its dimensions when choosing 50, 100 or 500 ms");
                            choice.IsDropDownOpen = false;
                            await LayoutAsync();
                            for (var labelIndex = 0; labelIndex < labels.Length; labelIndex++)
                                check(Close(labels[labelIndex].TranslatePoint(new Point(), page).X, labelOrigins[labelIndex].X), "Action prefixes and ms units stay in place across two- and three-digit steps");
                            check(Close(prefix.TranslatePoint(new Point(), choice).X, prefixOrigin.X)
                                && Close(value.TranslatePoint(new Point(value.ActualWidth, 0), choice).X, valueRight)
                                && Near(Bounds(arrow, choice), arrowBounds),
                                "Timing prefix, unit edge and arrow stay anchored across step selection");
                            check(Near(Bounds(choice, page), pageBounds) && Close(scroll.VerticalOffset, offset),
                                "Step selection preserves its row and page scroll position at both viewport sizes");
                        }
                    }
                }
            }

            var preview = new LyricsPreview();
            window.Content = preview;
            window.Height = 400;
            await LayoutAsync();
            var source = (ComboBox)preview.FindName("SourceChoice");
            var sourceText = (ContentPresenter)source.Template.FindName("ContentSite", source);
            var sourcePopup = (Popup)source.Template.FindName("PART_Popup", source);
            for (var index = 0; index < 2; index++)
            {
                source.SelectedIndex = index;
                await LayoutAsync();
                check(sourceText.HorizontalAlignment == HorizontalAlignment.Left
                    && Close(sourceText.TranslatePoint(new Point(), source).X, 8),
                    "Both preview source labels share a fixed left text edge");
                check(source.ActualWidth <= 78 && sourceText.DesiredSize.Width <= source.ActualWidth + .1,
                    $"Compact preview trigger fits: width={source.ActualWidth}, text={sourceText.DesiredSize.Width}, font={source.FontSize}, index={index}");
                source.IsDropDownOpen = true;
                await LayoutAsync();
                var surface = (FrameworkElement)sourcePopup.Child;
                var item = (ComboBoxItem)source.ItemContainerGenerator.ContainerFromIndex(index);
                var row = (Border)item.Template.FindName("Root", item);
                var text = Descendants(row).OfType<ContentPresenter>().Single();
                check(Close(text.TranslatePoint(new Point(), surface).X, sourceText.TranslatePoint(new Point(), source).X),
                    "Preview menu and trigger text use the same left inset");
                var menuScroll = Descendants(surface).OfType<ScrollViewer>().Single();
                var viewport = Descendants(menuScroll).OfType<ScrollContentPresenter>().First();
                check(Close(menuScroll.ActualWidth, viewport.ActualWidth)
                    && Close(surface.ActualWidth, 68) && surface.ActualWidth < source.ActualWidth
                    && text.DesiredSize.Width <= text.ActualWidth + .1,
                    "Current-song option fits a tighter menu with equal small text insets and no arrow gutter");
                source.IsDropDownOpen = false;
                await LayoutAsync();
            }
        }
        finally { window.Close(); }
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) < .1;
    private static bool Near(Rect a, Rect b) => Close(a.X, b.X) && Close(a.Y, b.Y) && Close(a.Width, b.Width) && Close(a.Height, b.Height);
    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
