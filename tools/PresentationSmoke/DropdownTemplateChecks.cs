using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Pages;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Control = System.Windows.Controls.Control;
using RadioButton = System.Windows.Controls.RadioButton;
using WpfPath = System.Windows.Shapes.Path;

internal static class DropdownTemplateChecks
{
    // Drive the actual dependency properties consumed by WPF template triggers.
    // Raising MouseEnter alone would not update IsMouseOver, while SendInput would
    // interfere with the user's desktop. These keys are confined to this fixture.
    private static readonly DependencyPropertyKey MouseOverKey = ReadKey(typeof(UIElement), "IsMouseOverPropertyKey");
    private static readonly DependencyPropertyKey PressedKey = ReadKey(typeof(ButtonBase), "IsPressedPropertyKey");
    private static readonly DependencyPropertyKey FocusWithinKey = ReadKey(typeof(UIElement), "IsKeyboardFocusWithinPropertyKey");

    public static async Task RunAsync(Action<bool, string> check)
    {
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
        async Task ShowAsync(FrameworkElement content)
        {
            window.Content = content;
            await LayoutAsync();
        }
        try
        {
            window.Show();
            var combo = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Top };
            combo.ItemsSource = new[] { "First", "Second" };
            combo.SelectedIndex = 0;
            await ShowAsync(combo);
            await CheckChoiceAsync(combo, "Generic choice");

            combo.IsEditable = true;
            combo.IsReadOnly = true;
            await LayoutAsync();
            await CheckChoiceAsync(combo, "Read-only editable choice");
            combo.IsReadOnly = false;
            await LayoutAsync();
            var genericRoot = (Border)combo.Template.FindName("Root", combo);
            var genericBorder = genericRoot.BorderBrush;
            WithState(combo, MouseOverKey, () => check(!SameBrush(genericRoot.BorderBrush, genericBorder),
                "Editable generic field retains its hover border"));
            WithState(combo, FocusWithinKey, () => check(!SameBrush(genericRoot.BorderBrush, genericBorder),
                "Editable generic field retains its input focus border"));

            var preview = new LyricsPreview();
            await ShowAsync(preview);
            await CheckChoiceAsync((ComboBox)preview.FindName("SourceChoice"), "Preview source");

            var timing = new SynchronizationPage();
            await ShowAsync(timing);
            foreach (var name in new[] { "TimingStepChoice", "GlobalTimingStepChoice" })
            {
                var step = (ComboBox)timing.FindName(name);
                step.ItemsSource = new[] { 50d, 100d, 500d };
                step.SelectedIndex = 1;
                await LayoutAsync();
                await CheckChoiceAsync(step, name);
            }

            var appearance = new AppearancePage();
            foreach (var picker in Descendants(appearance).OfType<FontPicker>())
                picker.CatalogProvider = () => Task.FromResult(new[] { "Fixture Alpha", "Fixture Beta" });
            await ShowAsync(appearance);
            var font = (FontPicker)appearance.FindName("PrimaryFontPicker");
            font.CatalogProvider = () => Task.FromResult(new[] { "Fixture Alpha", "Fixture Beta" });
            font.Text = "Fixture Alpha";
            await CheckChoiceAsync(font, "Selection-only font", checkSelection: false);
            font.IsEditable = true;
            await LayoutAsync();
            var fontRoot = (Border)font.Template.FindName("Root", font);
            var fontToggle = (ToggleButton)font.Template.FindName("DropDownToggle", font);
            var editor = (TextBox)font.Template.FindName("PART_EditableTextBox", font);
            var fontBackground = fontRoot.Background;
            foreach (var source in new UIElement[] { fontToggle, editor })
            {
                WithState(source, MouseOverKey, () => check(!SameBrush(fontRoot.Background, fontBackground),
                    "Editable font field retains hover feedback over " + source.GetType().Name));
            }
            await CheckArrowAsync(font, "Editable font");
            font.IsReadOnly = true;
            await LayoutAsync();
            await CheckChoiceAsync(font, "Read-only font", checkSelection: false);
            font.IsReadOnly = false;
            font.IsEditable = false;

            var languages = (Expander)appearance.FindName("LanguageSettings");
            await CheckExpanderAsync(languages, "Language settings");
            ((RadioButton)appearance.FindName("ColorsTab")).IsChecked = true;
            await LayoutAsync();
            foreach (var name in new[] { "CustomPaletteToggle", "SeparateColors" })
            {
                var disclosure = (ToggleButton)appearance.FindName(name);
                disclosure.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                await LayoutAsync();
                await CheckDisclosureAsync(disclosure, name);
                disclosure.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                await LayoutAsync();
            }
            check(BindingOperations.IsDataBound((FrameworkElement)appearance.FindName("CustomPaletteSettings"), Motion.ContentOpenProperty)
                && BindingOperations.IsDataBound((FrameworkElement)appearance.FindName("ManualColors"), Motion.ContentOpenProperty),
                "Both palette detail panels participate in downward reveal motion");

            var general = new GeneralPage();
            await ShowAsync(general);
            await CheckExpanderAsync((Expander)general.FindName("MaintenanceSettings"), "Reset and cleanup");

            var expander = new Expander { Header = "Fixture disclosure", Content = new TextBlock { Text = "Fixture content" } };
            await ShowAsync(expander);
            await CheckExpanderAsync(expander, "Generic expander");
        }
        finally { window.Close(); }

        async Task CheckChoiceAsync(ComboBox choice, string label, bool checkSelection = true)
        {
            choice.ApplyTemplate();
            var toggle = (ToggleButton)choice.Template.FindName("DropDownToggle", choice);
            toggle.ApplyTemplate();
            var before = SurfaceSignature(choice);
            foreach (var source in new UIElement[] { choice, toggle }.Concat(Descendants(choice).OfType<TextBox>()))
            {
                WithState(source, MouseOverKey, () => check(SurfaceSignature(choice) == before,
                    label + " trigger keeps its surface while hovered over " + source.GetType().Name));
            }
            WithState(toggle, PressedKey, () => check(SurfaceSignature(choice) == before,
                label + " trigger keeps its surface while pressed"));
            WithState(choice, FocusWithinKey, () => check(SurfaceSignature(choice) == before,
                label + " trigger has no input focus recoloring"));
            await CheckArrowAsync(choice, label);
            choice.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
            await LayoutAsync();
            try
            {
                check(SurfaceSignature(choice) == before, label + " trigger keeps its surface while open");
                if (checkSelection && choice.ItemContainerGenerator.ContainerFromIndex(choice.SelectedIndex) is ComboBoxItem selected)
                {
                    selected.ApplyTemplate();
                    var selectedRoot = selected.Template.FindName("Root", selected) as Border;
                    var outline = selected.Template.FindName("SelectionOutline", selected) as Border;
                    check(selectedRoot?.Background is SolidColorBrush { Color.A: > 0 } && outline?.Visibility == Visibility.Visible,
                        label + " menu retains its selected-item feedback");
                }
            }
            finally { choice.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false); }
            await LayoutAsync();
        }

        async Task CheckArrowAsync(ComboBox choice, string label)
        {
            choice.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            await LayoutAsync();
            var arrow = Descendants(choice).OfType<WpfPath>().First(p => BindingOperations.IsDataBound(p, Motion.DisclosureOpenProperty));
            CheckArrow(arrow, false, label);
            choice.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
            await LayoutAsync();
            CheckArrow(arrow, true, label);
            choice.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            await LayoutAsync();
            CheckArrow(arrow, false, label);
        }

        async Task CheckDisclosureAsync(ToggleButton toggle, string label)
        {
            toggle.ApplyTemplate();
            var before = SurfaceSignature(toggle);
            WithState(toggle, MouseOverKey, () => check(SurfaceSignature(toggle) == before, label + " has no hover fill"));
            WithState(toggle, PressedKey, () => check(SurfaceSignature(toggle) == before, label + " has no press fill"));
            var arrow = Descendants(toggle).OfType<WpfPath>().First(p => BindingOperations.IsDataBound(p, Motion.DisclosureOpenProperty));
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await LayoutAsync();
            CheckArrow(arrow, false, label);
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            await LayoutAsync();
            CheckArrow(arrow, true, label);
            check(SurfaceSignature(toggle) == before, label + " remains visually neutral when expanded");
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await LayoutAsync();
            CheckArrow(arrow, false, label);
        }

        async Task CheckExpanderAsync(Expander expander, string label)
        {
            expander.ApplyTemplate();
            var toggle = Descendants(expander).OfType<ToggleButton>().First();
            await CheckDisclosureAsync(toggle, label);
            check(Motion.GetExpand(expander), label + " retains its animated content expansion");
            var host = (Border)expander.Template.FindName("ExpansionHost", expander);
            check(host.Visibility == Visibility.Collapsed, label + " closes its content under reduced motion");
            expander.IsExpanded = true;
            await LayoutAsync();
            check(host.Visibility == Visibility.Visible && host.ActualHeight > 0,
                label + " reveals usable content under reduced motion");
            expander.IsExpanded = false;
            await LayoutAsync();
        }

        void CheckArrow(WpfPath arrow, bool expanded, string label)
        {
            var angle = (arrow.RenderTransform as RotateTransform)?.Angle ?? 0;
            check(SameBrush(arrow.Stroke, (Brush)arrow.FindResource("TextSecondaryBrush")), label + " uses the shared neutral chevron color");
            check(Motion.GetDisclosureOpen(arrow) == expanded && Math.Abs(angle - (expanded ? 90 : 0)) < .001,
                label + (expanded ? " rotates its chevron downward" : " restores its right-pointing chevron"));
            var points = arrow.Data.GetFlattenedPathGeometry().Figures.SelectMany(f => new[] { f.StartPoint }
                .Concat(f.Segments.SelectMany(s => s is PolyLineSegment poly ? poly.Points.AsEnumerable()
                    : s is LineSegment line ? new[] { line.Point } : Array.Empty<Point>()))).ToArray();
            check(points.Length == 3 && points[0].X < points[1].X && points[2].X < points[1].X
                && points[0].Y < points[1].Y && points[1].Y < points[2].Y,
                label + " uses a right-pointing base chevron before rotation");
        }
    }

    private static DependencyPropertyKey ReadKey(Type type, string name) =>
        (DependencyPropertyKey)(type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Missing WPF fixture key: " + name));

    private static void WithState(DependencyObject target, DependencyPropertyKey key, Action action)
    {
        var previous = target.GetValue(key.DependencyProperty);
        try { target.SetValue(key, true); action(); }
        finally { target.SetValue(key, previous); }
    }

    private static bool SameBrush(Brush? left, Brush? right) => left?.ToString() == right?.ToString();

    private static string SurfaceSignature(DependencyObject root) => string.Join("|", Descendants(root).Prepend(root)
        .Where(element => element is UIElement { IsVisible: true })
        .Select(element => element switch
        {
            Border border => $"B:{border.Background}:{PaintedBorder(border.BorderBrush, border.BorderThickness)}:{border.BorderThickness}:{border.Opacity}",
            Control control => $"C:{control.Background}:{PaintedBorder(control.BorderBrush, control.BorderThickness)}:{control.Foreground}:{control.Opacity}",
            _ => null
        }).Where(value => value is not null));

    private static string? PaintedBorder(Brush? brush, Thickness thickness) => thickness == new Thickness(0) ? null : brush?.ToString();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
