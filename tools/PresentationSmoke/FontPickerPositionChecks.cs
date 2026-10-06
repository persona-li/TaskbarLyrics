using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarLyrics.App.Controls;

internal static class FontPickerPositionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var names = Enumerable.Range(0, 120).Select(i => $"Fixture Font {i:000}").ToArray();
        var pending = new TaskCompletionSource<string[]>();
        var picker = new FontPicker { Width = 260, VerticalAlignment = VerticalAlignment.Top,
            Text = names[95], CatalogProvider = () => pending.Task };
        var window = new Window { Width = 500, Height = 550, Content = picker,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        Motion.SetReduce(window, true);
        async Task Layout()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        void CheckCurrent(int index)
        {
            var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
            var scroll = Descendants(popup.Child).OfType<ScrollViewer>().First();
            var viewport = Descendants(scroll).OfType<ScrollContentPresenter>().First();
            var row = picker.ItemContainerGenerator.ContainerFromIndex(index) as FontPickerItem;
            check(row is not null, "Opening a virtual font list realizes the saved font far from the start");
            var bounds = row!.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            check(bounds.Top >= -.5 && bounds.Bottom <= viewport.ActualHeight + .5,
                "Current font is fully visible inside the opened menu");
            check(picker.Text == names[index] && ReferenceEquals(picker.ActiveItem, row),
                "Current font is the active row without replacing the configured font");
        }
        try
        {
            window.Show(); await Layout();
            picker.IsDropDownOpen = true;
            picker.IsDropDownOpen = false;
            picker.IsDropDownOpen = true;
            pending.SetResult(names);
            await Layout();
            CheckCurrent(95);
            picker.IsDropDownOpen = false;
            picker.SetCurrentValue(ComboBox.TextProperty, names[20]);
            picker.IsDropDownOpen = true;
            await Layout();
            CheckCurrent(20);
            picker.IsDropDownOpen = false;
            picker.IsDropDownOpen = true;
            await Layout();
            CheckCurrent(20);
            picker.IsDropDownOpen = false;
            picker.SetCurrentValue(ComboBox.TextProperty, "Missing fixture font");
            picker.IsDropDownOpen = true;
            await Layout();
            check(picker.Text == "Missing fixture font", "Unavailable fonts are never silently replaced when the menu opens");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
