using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TaskbarLyrics.App.Controls;

internal static class ColorEditorChecks
{
    public static void Run(Action<bool, string> check)
    {
        var editor = new ColorEditor { Hex = "#496DBF" };
        check(ColorEditor.ComputePopupVertical(430,32,720).Above,
            "Color popup opens upward when that keeps the spectrum and Hex field visible");
        check(!ColorEditor.ComputePopupVertical(100,32,720).Above,
            "Color popup opens downward when it has enough room");
        check(editor.Width == 38, "Color editor exposes only a compact swatch");
        var input = (TextBox)editor.FindName("HexInput");
        var picker = (Popup)editor.FindName("Picker");
        check(picker.Placement == PlacementMode.Custom && picker.CustomPopupPlacementCallback is not null,
            "Color popup uses the shared DPI-aware edge placement rules");
        check(editor.FindName("Swatches") is null && editor.FindName("Red") is null,
            "Color popup removes preset chips and RGB channel sliders");
        DependencyObject? ancestor=input;
        while(ancestor is not null && !ReferenceEquals(ancestor,picker.Child)) ancestor=LogicalTreeHelper.GetParent(ancestor);
        check(ReferenceEquals(ancestor,picker.Child),
            "Hex input lives inside the popup, not beside the swatch");
        var host = new Window { Content = editor, Width = 300, Height = 100,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual };
        host.Show(); host.UpdateLayout();
        try
        {
            editor.SetCurrentValue(ColorEditor.HexProperty,"#80496DBF");
            var hue=(Slider)editor.FindName("Hue");
            hue.Value=120;
            check(ColorEditor.TryParse(editor.Hex,out var changed) && changed.A==128 && changed.G>changed.R,
                "Hue selection updates the color while preserving opacity");
            var saved=editor.Hex;
            input.Text = "invalid unfinished color";
            input.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(host)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            check(editor.Hex == saved && input.Text == editor.Hex,
                "Escape restores the saved color without committing an unfinished edit");
            check(((TextBlock)editor.FindName("Error")).Visibility == Visibility.Collapsed,
                "Escape clears hex validation feedback");
        }
        finally { host.Close(); }
    }
}
