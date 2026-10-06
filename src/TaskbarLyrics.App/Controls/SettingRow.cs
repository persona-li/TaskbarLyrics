using System.Windows;
using System.Windows.Controls;
namespace TaskbarLyrics.App.Controls;
public sealed class SettingRow : ContentControl
{
    public SettingRow() => SetResourceReference(StyleProperty, typeof(SettingRow));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
}
