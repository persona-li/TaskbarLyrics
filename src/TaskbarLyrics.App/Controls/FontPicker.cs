using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
namespace TaskbarLyrics.App.Controls;
public sealed class FontPicker : ComboBox
{
    private static Task<string[]>? _fontNames;
    private bool _loadedFonts, _loading;
    private Popup? _popup;
    private Task? _catalogLoad;
    private long _openRevision;
    private bool _pointerNavigation;
    public FontPickerItem? ActiveItem { get; private set; }
    public Func<Task<string[]>>? CatalogProvider { get; set; }
    public FontPicker()
    {
        SetResourceReference(StyleProperty, "FontPickerStyle");
        SetResourceReference(ItemContainerStyleProperty, "FontPickerItemStyle");
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        BorderThickness = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Left;
        IsEditable = false; IsTextSearchEnabled = false; MinHeight = 32;
        ItemsSource = new[] { "Microsoft YaHei UI", "Segoe UI", "Yu Gothic UI", "Malgun Gothic" };
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        DropDownOpened += async (_, _) =>
        {
            var revision = ++_openRevision;
            _pointerNavigation = false;
            ConstrainPopup();
            if (GetTemplateChild("PART_EditableTextBox") is TextBox editor)
            {
                // ComboBox selects all text when focused; opening its list should not show that selection.
                _ = Dispatcher.BeginInvoke(() => { if (IsDropDownOpen) editor.Select(editor.CaretIndex, 0); });
            }
            await EnsureCatalogAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (revision == _openRevision && IsDropDownOpen && !_pointerNavigation)
                    RevealCurrentFont();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        DropDownClosed += (_, _) =>
        {
            ++_openRevision;
            _pointerNavigation = false;
            SetActiveItem(null, false);
            if (GetTemplateChild("PART_EditableTextBox") is TextBox editor)
            {
                editor.Select(editor.CaretIndex, 0);
            }
        };
        SelectionChanged += (_, _) =>
        {
            if (_loading || SelectedItem is not string selected) return;
            Dispatcher.BeginInvoke(() =>
            {
                // Commit an explicit installed-font choice immediately.
                if (!_loading && SelectedItem as string == selected && Text == selected)
                    GetBindingExpression(TextProperty)?.UpdateSource();
            });
        };
    }
    protected override DependencyObject GetContainerForItemOverride() => new FontPickerItem();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is FontPickerItem;
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (ReferenceEquals(element, ActiveItem)) SetActiveItem(null, false);
        base.ClearContainerForItemOverride(element, item);
    }
    internal void SetActiveItem(FontPickerItem? item, bool pointer)
    {
        if (pointer) _pointerNavigation = true;
        if (ReferenceEquals(ActiveItem, item)) return;
        ActiveItem?.SetActive(false);
        ActiveItem = item;
        ActiveItem?.SetActive(true);
    }
    internal void KeyboardHighlight(FontPickerItem item)
    {
        if (!_pointerNavigation && IsDropDownOpen) SetActiveItem(item, false);
    }
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down
            or System.Windows.Input.Key.Home or System.Windows.Input.Key.End
            or System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown)
            _pointerNavigation = false;
        base.OnPreviewKeyDown(e);
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_EditableTextBox") is TextBox editor)
        {
            editor.TextAlignment = TextAlignment.Left;
            editor.HorizontalContentAlignment = HorizontalAlignment.Left;
            editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            editor.SetResourceReference(TextBox.SelectionBrushProperty, "PlaybackHoverBrush");
            editor.SetResourceReference(TextBox.SelectionTextBrushProperty, "TextPrimaryBrush");
            editor.BorderThickness = new Thickness(0);
        }
        _popup = GetTemplateChild("PART_Popup") as Popup;
        if (_popup is null) return;
        _popup.Placement = PlacementMode.Custom;
        _popup.PlacementTarget = this;
        _popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
        {
            var owner = Window.GetWindow(this);
            // WPF supplies callback sizes in device pixels. Keep the owner edge in that same
            // space; mixing TranslatePoint's DIPs with these sizes shifts the menu at 125–200% DPI.
            var dpi = VisualTreeHelper.GetDpi(this);
            var right = owner is null ? targetSize.Width
                : owner.PointToScreen(new Point(owner.ActualWidth, 0)).X - PointToScreen(new Point()).X;
            var x = ComputePopupX(targetSize.Width / dpi.DpiScaleX,
                popupSize.Width / dpi.DpiScaleX, right / dpi.DpiScaleX) * dpi.DpiScaleX;
            var vertical = owner is null ? (MaxHeight: 320d, Above: false)
                : ComputePopupVertical(TranslatePoint(new Point(), owner).Y, ActualHeight, owner.ActualHeight);
            var y = vertical.Above ? -popupSize.Height - 4 * dpi.DpiScaleY : targetSize.Height + 4 * dpi.DpiScaleY;
            return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Vertical) };
        };
    }
    public static double ComputePopupX(double targetWidth, double popupWidth, double ownerRight) =>
        Math.Min(targetWidth - popupWidth, ownerRight - popupWidth - 8);

    public static (double MaxHeight, bool Above) ComputePopupVertical(double fieldTop, double fieldHeight, double ownerHeight)
    {
        const double inset = 8, gap = 4;
        var below = Math.Max(0, ownerHeight - inset - fieldTop - fieldHeight - gap);
        var above = Math.Max(0, fieldTop - inset - gap);
        // Keep a useful downward list when possible; near the bottom, open upward within the window.
        var useAbove = below < 120 && above > below;
        return (Math.Max(1, Math.Min(320, useAbove ? above : below)), useAbove);
    }

    private void ConstrainPopup()
    {
        if (_popup?.Child is not FrameworkElement child) return;
        var owner = Window.GetWindow(this);
        var width = Math.Min(ActualWidth, Math.Max(1, (owner?.ActualWidth ?? ActualWidth + 16) - 16));
        // A fixed viewport keeps long font names from widening the popup beyond the window.
        child.MaxHeight = owner is null ? 320 : ComputePopupVertical(TranslatePoint(new Point(), owner).Y, ActualHeight, owner.ActualHeight).MaxHeight;
        child.MinWidth = 0;
        child.Width = width;
        child.MaxWidth = width;
    }
    private void RevealCurrentFont()
    {
        if (_popup?.Child is not FrameworkElement surface || !_popup.IsOpen) return;
        var index = -1;
        for (var i = 0; i < Items.Count; i++)
            if (Items[i] is string name && string.Equals(name, Text?.Trim(), StringComparison.OrdinalIgnoreCase))
            { index = i; break; }
        if (index < 0) return; // A missing font must never select/save an unrelated replacement.
        surface.UpdateLayout();
        FindVisual<VirtualizingStackPanel>(surface)?.BringIndexIntoViewPublic(index);
        surface.UpdateLayout();
        if (ItemContainerGenerator.ContainerFromIndex(index) is FontPickerItem current)
        {
            current.BringIntoView();
            if (!_pointerNavigation) SetActiveItem(current, false);
        }
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindVisual<T>(child) is T descendant) return descendant;
        }
        return null;
    }

    public async Task EnsureCatalogAsync()
    {
        if (_loadedFonts) return;
        var pending = _catalogLoad ??= LoadCatalogAsync();
        try { await pending; }
        finally { if (ReferenceEquals(_catalogLoad, pending)) _catalogLoad = null; }
    }

    private async Task LoadCatalogAsync()
    {
        if (_loadedFonts || _loading) return;
        _loading = true;
        try
        {
            var names = await (CatalogProvider?.Invoke() ?? (_fontNames ??= Task.Run(() => Fonts.SystemFontFamilies
                .Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToArray())));
            var text = Text;
            SetCurrentValue(ItemsSourceProperty, names);
            // SetValue/Text= would remove the view-model binding after opening the dropdown once.
            SetCurrentValue(TextProperty, text);
            _loadedFonts = true;
        }
        catch { /* Keep the known font choices and saved name if enumeration fails. */ }
        finally { _loading = false; }
    }
}
