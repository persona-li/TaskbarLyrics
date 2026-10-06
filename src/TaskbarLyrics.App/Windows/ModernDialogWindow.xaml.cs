using System.Windows;
using TaskbarLyrics.App.Services;

namespace TaskbarLyrics.App.Windows;

public partial class ModernDialogWindow : Window
{
    private ModernDialogWindow(Window owner, string title, string message, bool confirm, bool danger, string? confirmText = null)
    {
        InitializeComponent();
        Owner = owner;
        DialogTitle.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirm ? confirmText ?? title : "知道了";
        Loaded += (_, _) => { MessageScroll.MaxHeight = Math.Min(320, Math.Max(64, WindowPlacement.AvailableHeight(this) - 200)); UpdateLayout(); WindowPlacement.ClampToWorkArea(this); };
        if (danger)
        {
            Glyph.Text = "!";
            Glyph.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            GlyphPlate.Background = (System.Windows.Media.Brush)FindResource("DangerSoftBrush");
            ConfirmButton.Style = (Style)FindResource("DangerButton");
        }
    }

    public static bool Confirm(Window owner, string title, string message, bool danger = false, string? confirmText = null) =>
        new ModernDialogWindow(owner, title, message, true, danger, confirmText).ShowDialog() == true;

    public static void ShowMessage(Window owner, string title, string message, bool danger = false) =>
        new ModernDialogWindow(owner, title, message, false, danger).ShowDialog();

    private void Confirm_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
