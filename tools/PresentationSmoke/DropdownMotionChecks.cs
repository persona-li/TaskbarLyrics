using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarLyrics.App.Controls;
using ShapePath = System.Windows.Shapes.Path;
using Binding = System.Windows.Data.Binding;

internal static class DropdownMotionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var arrow = new ShapePath { Width = 10, Height = 10, Data = Geometry.Parse("M 2,1 L 7,5 L 2,9"), Stroke = Brushes.Black };
        Motion.SetDisclosureOpen(arrow, true);
        var combo = new ComboBox { Width = 150, ItemsSource = new[] { "First", "Second", "Third" }, SelectedIndex = 0 };
        ComboBoxInteraction.SetOpenOnClick(combo, true);
        var originalClip = new RectangleGeometry(new Rect(0, 0, 180, 80));
        var content = new Border { Width = 180, Height = 80, Background = Brushes.White, Clip = originalClip, Visibility = Visibility.Collapsed };
        Motion.SetContentOpen(content, false);
        var panel = new StackPanel();
        panel.Children.Add(arrow); panel.Children.Add(combo); panel.Children.Add(content);
        var window = new Window
        {
            Content = panel, Width = 260, Height = 240,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(Angle(arrow) == 90 && Motion.GetActiveStoryboard(arrow) is null,
                "An initially expanded disclosure loads pointing down without an entrance spin");
            var allowed = Motion.Allowed(arrow);
            Motion.SetDisclosureOpen(arrow, false);
            check((Motion.GetActiveStoryboard(arrow) is not null) == allowed, "Disclosure rotation respects system motion preferences");
            if (allowed)
            {
                Seek(arrow, 70);
                var mid = Angle(arrow);
                check(mid > 0 && mid < 90, "Closing disclosure rotates through an intermediate angle");
                Motion.SetDisclosureOpen(arrow, true);
                Seek(arrow, 0);
                check(Math.Abs(Angle(arrow) - mid) < .001, "Quick disclosure reversal starts at its displayed angle");
                Seek(arrow, 200);
                check(Angle(arrow) == 90, "Reversed disclosure reaches the expanded endpoint");
                Motion.SetDisclosureOpen(arrow, false);
                Seek(arrow, 70);
                Motion.SetReduce(arrow, true);
                check(Angle(arrow) == 0 && Motion.GetActiveStoryboard(arrow) is null,
                    $"Enabling reduced motion clears an active arrow clock at the current closed endpoint (angle={Angle(arrow)}, clock={Motion.GetActiveStoryboard(arrow) is not null})");
            }
            Motion.SetReduce(arrow, true);
            Motion.SetDisclosureOpen(arrow, true);
            check(Angle(arrow) == 90 && Motion.GetActiveStoryboard(arrow) is null, "Reduced motion immediately points an open disclosure down");
            panel.Children.Remove(arrow);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Motion.SetDisclosureOpen(arrow, false);
            panel.Children.Add(arrow);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(Angle(arrow) == 0 && Motion.GetActiveStoryboard(arrow) is null, "Reloaded disclosure reflects its latest closed state");

            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var surface = (FrameworkElement)popup.Child;
            check(popup.IsOpen && surface.ActualWidth > 0 && surface.ActualHeight > 0, "Dropdown animation fixture opens a measured popup");
            if (Motion.Allowed(combo))
            {
                var width = surface.ActualWidth; var height = surface.ActualHeight;
                Seek(surface, 80);
                check(surface.RenderTransform is TranslateTransform { Y: < 0 and > -4 }, "Popup moves down from above its resting position");
                check(surface.Clip is RectangleGeometry clip && clip.Rect.Top == 0 && clip.Rect.Height > 0 && clip.Rect.Height < height,
                    "Popup reveals its content from the top edge downward");
                check(surface.ActualWidth == width && surface.ActualHeight == height, "Popup reveal does not change its measured size");
                var openingOpacity = surface.Opacity;
                combo.IsDropDownOpen = false;
                check(popup.IsOpen && !surface.IsHitTestVisible && Motion.GetActiveStoryboard(surface) is not null,
                    "Closing request retains the popup window for its reverse animation and blocks stale item clicks");
                Seek(surface, 70);
                var closingOpacity = surface.Opacity;
                check(closingOpacity > 0 && closingOpacity < openingOpacity,
                    "Popup visibly fades and retracts upward before the native popup is hidden");
                combo.IsDropDownOpen = true;
                Seek(surface, 0);
                check(Math.Abs(surface.Opacity - closingOpacity) < .001 && surface.IsHitTestVisible,
                    "Reopening a closing popup starts from the visible frame and restores item input");
                Seek(surface, 200);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                check(surface.Clip is null && surface.Opacity == 1 && popup.IsOpen,
                    "Completed opening restores the popup surface without hiding it");
            }
            combo.SelectedIndex = 1;
            combo.IsDropDownOpen = false;
            await Complete(surface);
            check(!popup.IsOpen && combo.SelectedIndex == 1 && surface.Clip is null && surface.Opacity == 1
                && surface.RenderTransform.Value.IsIdentity && !Equals(surface.ReadLocalValue(UIElement.IsHitTestVisibleProperty), false) && Motion.GetActiveStoryboard(surface) is null,
                $"Completed closing hides the popup, preserves selection and removes temporary visual state (open={popup.IsOpen}, selection={combo.SelectedIndex}, clip={surface.Clip is not null}, opacity={surface.Opacity}, identity={surface.RenderTransform.Value.IsIdentity}, hit={surface.IsHitTestVisible}, clock={Motion.GetActiveStoryboard(surface) is not null})");
            combo.IsDropDownOpen = true;
            combo.IsDropDownOpen = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(!popup.IsOpen && surface.Clip is null && surface.Opacity == 1 && Motion.GetActiveStoryboard(surface) is null,
                "Closing before the queued opening frame prevents a stale popup animation");
            Motion.SetReduce(combo, true);
            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(popup.IsOpen && surface.Clip is null && surface.Opacity == 1 && Motion.GetActiveStoryboard(surface) is null,
                "Reduced motion opens the complete popup without a transient clip");
            combo.IsDropDownOpen = false;
            check(!popup.IsOpen, "Reduced motion immediately hides the popup on close");
            combo.IsDropDownOpen = true;
            combo.IsDropDownOpen = false;
            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(popup.IsOpen && combo.IsDropDownOpen,
                "A delayed native Closed event cannot cancel a popup that has already reopened");
            combo.IsDropDownOpen = false;
            Motion.SetReduce(combo, false);
            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Motion.Allowed(combo))
            {
                Seek(surface, 80);
                combo.IsDropDownOpen = false;
                Seek(surface, 70);
                Motion.SetReduce(combo, true);
                check(!popup.IsOpen && Motion.GetActiveStoryboard(surface) is null,
                    "Enabling reduced motion during popup closing finishes and hides it immediately");
            }
            Motion.SetReduce(combo, false);
            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            combo.Visibility = Visibility.Collapsed;
            check(!popup.IsOpen && !combo.IsDropDownOpen && Motion.GetActiveStoryboard(surface) is null,
                "Hiding a dropdown owner closes its native popup and cancels queued frames");
            combo.Visibility = Visibility.Visible;

            check(content.Visibility == Visibility.Collapsed, "Closed inline content begins collapsed before first expansion");
            Motion.SetContentOpen(content, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Motion.Allowed(content))
            {
                Seek(content, 80);
                window.UpdateLayout();
                check(content.Clip is RectangleGeometry { Rect.Height: > 0 and < 80 } && content.ActualHeight > 0 && content.ActualHeight < 80,
                    "Inline disclosure animates its occupied height so following rows move continuously");
                var openingOpacity = content.Opacity;
                Motion.SetContentOpen(content, false);
                check(content.Visibility == Visibility.Visible && !content.IsHitTestVisible,
                    "Inline closing keeps content visible but noninteractive for its reverse animation");
                Seek(content, 70);
                var closingOpacity = content.Opacity;
                check(closingOpacity > 0 && closingOpacity < openingOpacity, "Inline content retracts visibly before collapsing");
                Motion.SetContentOpen(content, true);
                Seek(content, 0);
                check(Math.Abs(content.Opacity - closingOpacity) < .001 && content.IsHitTestVisible,
                    "Inline rapid reversal preserves its current visible progress");
                await Complete(content);
                check(ReferenceEquals(content.Clip, originalClip) && content.Visibility == Visibility.Visible,
                    "Completed inline opening restores the original clip so later content can grow");
            }
            Motion.SetContentOpen(content, false);
            await Complete(content);
            check(content.Visibility == Visibility.Collapsed && ReferenceEquals(content.Clip, originalClip)
                && content.Opacity == 1 && content.IsHitTestVisible && Motion.GetActiveStoryboard(content) is null,
                "Completed inline closing collapses content and restores its original visual state");
            Motion.SetContentOpen(content, true);
            Motion.SetContentOpen(content, false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(content.Visibility == Visibility.Collapsed && ReferenceEquals(content.Clip, originalClip) && Motion.GetActiveStoryboard(content) is null,
                "Rapid inline open and close cannot resurrect a queued reveal");
            Motion.SetReduce(content, true);
            Motion.SetContentOpen(content, true);
            check(content.Visibility == Visibility.Visible && ReferenceEquals(content.Clip, originalClip) && content.Opacity == 1 && Motion.GetActiveStoryboard(content) is null,
                "Reduced motion reveals inline content immediately and preserves its clip");
            Motion.SetContentOpen(content, false);
            check(content.Visibility == Visibility.Collapsed, "Reduced motion immediately collapses inline content");
            Motion.SetReduce(content, false);
            Motion.SetContentOpen(content, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Motion.SetContentOpen(content, false);
            panel.Children.Remove(content);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(content.Visibility == Visibility.Collapsed && Motion.GetActiveStoryboard(content) is null && ReferenceEquals(content.Clip, originalClip),
                "Unloading during inline closing finishes cleanup without stale animation completion");
            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            panel.Children.Remove(combo);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(!popup.IsOpen && !combo.IsDropDownOpen && Motion.GetActiveStoryboard(surface) is null,
                "Unloading a dropdown owner closes its popup even during an active reveal");
            var nestedChild = new Border { Width = 160, Height = 40, Background = Brushes.White };
            var nestedParent = new Border { Child = nestedChild };
            Motion.SetContentOpen(nestedChild, true);
            Motion.SetContentOpen(nestedParent, true);
            panel.Children.Add(nestedParent);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(nestedParent.IsHitTestVisible && nestedChild.IsHitTestVisible, "Nested disclosures initially accept pointer input");
            Motion.SetContentOpen(nestedParent, false);
            Motion.SetContentOpen(nestedChild, false);
            await Complete(nestedChild);
            await Complete(nestedParent);
            Motion.SetContentOpen(nestedParent, true);
            Motion.SetContentOpen(nestedChild, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Complete(nestedParent);
            await Complete(nestedChild);
            check(nestedParent.IsHitTestVisible && nestedChild.IsHitTestVisible,
                $"Nested disclosures remain clickable after parent-first closing and reopening (parent={nestedParent.IsHitTestVisible}, child={nestedChild.IsHitTestVisible}, local={nestedChild.ReadLocalValue(UIElement.IsHitTestVisibleProperty)})");
            check(nestedChild.ReadLocalValue(UIElement.IsHitTestVisibleProperty) == DependencyProperty.UnsetValue,
                "Nested disclosure input masking does not create a local hit-test override");
            nestedChild.IsHitTestVisible = false;
            await CloseAndOpenNested(nestedParent, nestedChild);
            check(!nestedChild.IsHitTestVisible && Equals(nestedChild.ReadLocalValue(UIElement.IsHitTestVisibleProperty), false),
                "Nested disclosure preserves an explicitly disabled hit-test value");
            var hitTestSource = new CheckBox { IsChecked = true };
            BindingOperations.SetBinding(nestedChild, UIElement.IsHitTestVisibleProperty,
                new Binding(nameof(CheckBox.IsChecked)) { Source = hitTestSource, Mode = BindingMode.OneWay });
            await CloseAndOpenNested(nestedParent, nestedChild);
            check(nestedChild.IsHitTestVisible && BindingOperations.IsDataBound(nestedChild, UIElement.IsHitTestVisibleProperty),
                "Nested disclosure retains its input binding after closing and reopening");
            hitTestSource.IsChecked = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(!nestedChild.IsHitTestVisible, "Restored input binding still responds to its disabled source value");
            hitTestSource.IsChecked = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(nestedChild.IsHitTestVisible, "Restored input binding still responds to its enabled source value");

        }
        finally
        {
            combo.IsDropDownOpen = false;
            Motion.SetContentOpen(content, false);
            window.Close();
        }
    }

    private static async Task Complete(FrameworkElement element)
    {
        if (Motion.GetActiveStoryboard(element) is not null) Seek(element, 200);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task CloseAndOpenNested(FrameworkElement parent, FrameworkElement child)
    {
        Motion.SetContentOpen(parent, false);
        Motion.SetContentOpen(child, false);
        await Complete(child);
        await Complete(parent);
        Motion.SetContentOpen(parent, true);
        Motion.SetContentOpen(child, true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Complete(parent);
        await Complete(child);
    }

    private static double Angle(FrameworkElement arrow) => ((RotateTransform)arrow.RenderTransform).Angle;
    private static void Seek(FrameworkElement element, int milliseconds)
    {
        var storyboard = Motion.GetActiveStoryboard(element) ?? throw new InvalidOperationException("Missing disclosure animation");
        storyboard.Pause(element);
        storyboard.SeekAlignedToLastTick(element, TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime);
    }
}
