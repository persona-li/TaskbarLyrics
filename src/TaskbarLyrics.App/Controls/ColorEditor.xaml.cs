using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
namespace TaskbarLyrics.App.Controls;
public partial class ColorEditor : UserControl
{
    private bool _sync;
    private string? _openedHex;
    public static readonly DependencyProperty RecentColorsProperty = DependencyProperty.Register(nameof(RecentColors), typeof(IEnumerable<string>), typeof(ColorEditor), new PropertyMetadata(null, RecentChanged));
    public static readonly DependencyProperty RememberColorCommandProperty = DependencyProperty.Register(nameof(RememberColorCommand), typeof(ICommand), typeof(ColorEditor));
    public IEnumerable<string>? RecentColors { get => (IEnumerable<string>?)GetValue(RecentColorsProperty); set => SetValue(RecentColorsProperty,value); }
    public ICommand? RememberColorCommand { get => (ICommand?)GetValue(RememberColorCommandProperty); set => SetValue(RememberColorCommandProperty,value); }
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(nameof(Hex), typeof(string), typeof(ColorEditor), new FrameworkPropertyMetadata("#FFFFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty AccessibleNameProperty = DependencyProperty.Register(nameof(AccessibleName), typeof(string), typeof(ColorEditor), new PropertyMetadata("歌词颜色"));
    public string Hex { get => (string)GetValue(HexProperty); set => SetValue(HexProperty, value); }
    public string AccessibleName { get => (string)GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    public ColorEditor()
    {
        InitializeComponent(); Loaded += (_, _) => Refresh();
        Picker.CustomPopupPlacementCallback = PlacePicker;
        Unloaded += (_, _) => Picker.IsOpen = false;
        Picker.Child.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { HexInput.Text = Hex; Error.Visibility = Visibility.Collapsed; Picker.IsOpen = false; SwatchButton.Focus(); e.Handled = true; } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Picker.IsOpen) { HexInput.Text = Hex; Error.Visibility = Visibility.Collapsed; Picker.IsOpen = false; SwatchButton.Focus(); e.Handled = true; } };
    }
    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.White;
        if (text is null || text.Length is not (7 or 9) || !text.StartsWith('#')) return false;
        try { color = (Color)ColorConverter.ConvertFromString(text); return true; } catch { return false; }
    }
    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e) => ((ColorEditor)o).Refresh();
    private void Refresh()
    {
        if (_sync || HexInput is null || !TryParse(Hex, out var c)) return;
        _sync = true;
        try
        {
            Swatch.Background = new SolidColorBrush(c);
            if (!HexInput.IsKeyboardFocused) HexInput.Text = Hex;
            var hsv = ColorSpectrum.ToHsv(c);
            if (hsv.Saturation > 0 && hsv.Brightness > 0) Hue.Value = hsv.Hue;
            Spectrum.Hue = Hue.Value;
            Spectrum.Saturation = hsv.Saturation;
            Spectrum.Brightness = hsv.Brightness;
            Alpha.Value = c.A;
            AlphaLabel.Text = $"不透明度  {c.A / 255.0:P0}";
            Alpha.Background = new LinearGradientBrush(Color.FromArgb(0,c.R,c.G,c.B),Color.FromRgb(c.R,c.G,c.B),0);
        }
        finally { _sync = false; }
    }
    private void Apply(Color color) { Error.Visibility = Visibility.Collapsed; SetCurrentValue(HexProperty, color.ToString()); Refresh(); }
    private void OpenPicker(object sender, RoutedEventArgs e)
    {
        if (!Picker.IsOpen) ConstrainPicker();
        Picker.IsOpen = !Picker.IsOpen;
    }
    private void ConstrainPicker()
    {
        var owner = Window.GetWindow(this);
        if (owner is null) return;
        PickerSurface.Width = Math.Min(280, Math.Max(1, owner.ActualWidth - 16));
        var vertical = ComputePopupVertical(TranslatePoint(new Point(), owner).Y, EditorSurface.ActualHeight, owner.ActualHeight);
        PickerSurface.MaxHeight = vertical.MaxHeight;
    }
    private CustomPopupPlacement[] PlacePicker(System.Windows.Size popupSize, System.Windows.Size targetSize, Point offset)
    {
        var owner = Window.GetWindow(this);
        var dpi = VisualTreeHelper.GetDpi(this);
        var right = owner is null ? targetSize.Width
            : owner.PointToScreen(new Point(owner.ActualWidth, 0)).X - PointToScreen(new Point()).X;
        var x = FontPicker.ComputePopupX(targetSize.Width / dpi.DpiScaleX,
            popupSize.Width / dpi.DpiScaleX, right / dpi.DpiScaleX) * dpi.DpiScaleX;
        var above = owner is not null && ComputePopupVertical(
            TranslatePoint(new Point(), owner).Y, EditorSurface.ActualHeight, owner.ActualHeight).Above;
        var y = above ? -popupSize.Height - 4 * dpi.DpiScaleY : (EditorSurface.ActualHeight + 4) * dpi.DpiScaleY;
        return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Vertical) };
    }
    private void PickerOpened(object? sender, EventArgs e) { _openedHex=Hex; HexInput.Text = Hex; Error.Visibility = Visibility.Collapsed; Refresh(); RefreshRecent(); Spectrum.Focus(); }
    private void PickerClosed(object? sender, EventArgs e)
    {
        if (_openedHex is not null) Commit();
        RememberSelection();
        _openedHex=null;
    }
    private void RememberSelection()
    {
        if (_openedHex is null || _openedHex == Hex) return;
        Remember(_openedHex);
        Remember(Hex);
        _openedHex = Hex;
    }
    private static void RecentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ColorEditor)d).RefreshRecent();
    private void RefreshRecent()
    {
        if (RecentItems is null) return;
        var colors=RecentColors?.ToArray() ?? Array.Empty<string>();
        RecentItems.ItemsSource=colors;
        RecentSection.Visibility=colors.Length>0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Remember(string hex) { if (RememberColorCommand?.CanExecute(hex)==true) RememberColorCommand.Execute(hex); }
    private void RecentColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } || !TryParse(hex,out var color)) return;
        Apply(color); HexInput.Text=Hex;
    }
    public static (double MaxHeight, bool Above) ComputePopupVertical(double top, double height, double windowHeight)
    {
        var below = Math.Max(0, windowHeight - top - height - 12);
        var above = Math.Max(0, top - 12);
        var useAbove = below < 350 && above > below;
        return (Math.Max(1, useAbove ? above : below), useAbove);
    }
    private void PaletteChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyPalette();
    private void SpectrumChanged(object? sender, EventArgs e) => ApplyPalette();
    private void ApplyPalette()
    {
        if (_sync || AlphaLabel is null) return;
        Spectrum.Hue = Hue.Value;
        var color = ColorSpectrum.FromHsv(Hue.Value, Spectrum.Saturation, Spectrum.Brightness, (byte)Alpha.Value);
        // Avoid RGB round-trip quantization moving the user's selection while dragging.
        _sync = true;
        try
        {
            SetCurrentValue(HexProperty, color.ToString());
            Swatch.Background = new SolidColorBrush(color);
            HexInput.Text = Hex;
            AlphaLabel.Text = $"不透明度  {color.A / 255.0:P0}";
            Alpha.Background = new LinearGradientBrush(Color.FromArgb(0,color.R,color.G,color.B),Color.FromRgb(color.R,color.G,color.B),0);
            Error.Visibility = Visibility.Collapsed;
        }
        finally { _sync = false; }
    }
    private void HexKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            HexInput.Text = Hex;
            Error.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }
    private void HexFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();
    private void Commit()
    {
        if (TryParse(HexInput.Text.Trim(), out var color))
        {
            Apply(color);
        }
        else { Error.Text = "请输入 #RRGGBB 或 #AARRGGBB；前两位为透明度。"; Error.Visibility = Visibility.Visible; }
    }
}
