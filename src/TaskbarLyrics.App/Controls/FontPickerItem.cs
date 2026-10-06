using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TaskbarLyrics.App.Controls;

/// <summary>Immediate pointer feedback shared by the row fill and font-name scrolling.</summary>
public sealed class FontPickerItem : ComboBoxItem
{
    private static readonly DependencyPropertyKey IsActivePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsActive), typeof(bool), typeof(FontPickerItem), new PropertyMetadata(false));
    public static readonly DependencyProperty IsActiveProperty = IsActivePropertyKey.DependencyProperty;
    public bool IsActive => (bool)GetValue(IsActiveProperty);
    internal void SetActive(bool value) => SetValue(IsActivePropertyKey, value);
    private FontPicker? Owner => ItemsControl.ItemsControlFromItemContainer(this) as FontPicker;
    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Owner?.SetActiveItem(this, true);
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Owner?.SetActiveItem(this, true);
    }
    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (Owner is { } owner && ReferenceEquals(owner.ActiveItem, this)) owner.SetActiveItem(null, true);
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Owner?.KeyboardHighlight(this);
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsHighlightedProperty && e.NewValue is true) Owner?.KeyboardHighlight(this);
    }
}
