using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace TaskbarLyrics.App.Controls;
public partial class NumberEditor : UserControl
{
    private string? _editContext;
    public NumberEditor() { InitializeComponent(); Loaded += (_, _) => Refresh(); }
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(NumberEditor), new PropertyMetadata(false, Changed));
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumberEditor), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberEditor), new PropertyMetadata(-5000d));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberEditor), new PropertyMetadata(5000d));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberEditor), new PropertyMetadata(1d, Changed));
    public static readonly DependencyProperty OffsetModeProperty = DependencyProperty.Register(nameof(OffsetMode), typeof(bool), typeof(NumberEditor), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ContextKeyProperty = DependencyProperty.Register(nameof(ContextKey), typeof(string), typeof(NumberEditor), new PropertyMetadata(""));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumberEditor), new PropertyMetadata(""));
    public static readonly DependencyProperty AccessibleNameProperty = DependencyProperty.Register(nameof(AccessibleName), typeof(string), typeof(NumberEditor), new PropertyMetadata("数值"));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public bool OffsetMode { get => (bool)GetValue(OffsetModeProperty); set => SetValue(OffsetModeProperty, value); }
    public string ContextKey { get => (string)GetValue(ContextKeyProperty); set => SetValue(ContextKeyProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string AccessibleName { get => (string)GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e) => ((NumberEditor)o).Refresh();
    private void Refresh()
    {
        if (Input is null || ZeroButton is null || Steps is null) return;
        if (!Input.IsKeyboardFocused) Input.Text = Value.ToString("0.##", CultureInfo.CurrentCulture);
        MinusButton.Content = "−";
        MinusButton.ToolTip = OffsetMode ? $"延后 {Step:0} ms" : "减小";
        MinusButton.Visibility = PlusButton.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        PlusButton.Content = "+";
        PlusButton.ToolTip = OffsetMode ? $"提前 {Step:0} ms" : "增大";
        System.Windows.Automation.AutomationProperties.SetName(MinusButton, OffsetMode ? MinusButton.ToolTip.ToString() : AccessibleName + "减小");
        System.Windows.Automation.AutomationProperties.SetName(PlusButton, OffsetMode ? PlusButton.ToolTip.ToString() : AccessibleName + "增大");
        Steps.Visibility = ZeroButton.Visibility = OffsetMode && !Compact ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Set(double value) { Error.Visibility = Visibility.Collapsed; SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum)); Input.Text = Value.ToString("0.##", CultureInfo.CurrentCulture); }
    private void MinusClick(object sender, RoutedEventArgs e) => Set(Value - Step);
    private void PlusClick(object sender, RoutedEventArgs e) => Set(Value + Step);
    private void ZeroClick(object sender, RoutedEventArgs e) => Set(0);
    private void StepChanged(object sender, SelectionChangedEventArgs e) { if (!IsInitialized) return; if (StepChoice.SelectedValue is string s && double.TryParse(s, out var v)) SetCurrentValue(StepProperty, v); }
    private void BeginEdit(object sender, KeyboardFocusChangedEventArgs e) => _editContext = ContextKey;
    private void CommitFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();
    private void InputKey(object sender, KeyEventArgs e)
    {
        if (Compact && (e.Key == Key.Up || e.Key == Key.Down)) { if (Commit()) Set(Value + (e.Key == Key.Up ? Step : -Step)); e.Handled = true; }
        else if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Input.Text = Value.ToString("0.##"); _editContext = ContextKey; Error.Visibility = Visibility.Collapsed; e.Handled = true; }
    }
    public bool Commit()
    {
        if (_editContext is not null && _editContext != ContextKey) { ShowError("歌曲已变化，未应用此输入。按 Esc 使用当前歌曲的值。"); return false; }
        if (!double.TryParse(Input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value) || value < Minimum || value > Maximum)
        { ShowError($"请输入 {Minimum:0.##} 至 {Maximum:0.##} 之间的数值。"); return false; }
        if (OffsetMode && value != Math.Truncate(value)) { ShowError("时间偏移请使用整数毫秒。"); return false; }
        Set(value); return true;
    }
    private void ShowError(string text) { Error.Text = text; Error.Visibility = Visibility.Visible; }
}
