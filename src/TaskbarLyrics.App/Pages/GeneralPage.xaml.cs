using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TaskbarLyrics.App.Pages;

public partial class GeneralPage : UserControl
{
    public GeneralPage()
    {
        InitializeComponent();
        GeneralScroll.PreviewMouseWheel += OnPreviewMouseWheel;
        GeneralScroll.ScrollChanged += OnScrollChanged;
        MaintenanceSettings.Expanded += OnMaintenanceExpanded;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (GeneralScroll.ScrollableHeight > 0) return;

        // WPF retains a requested offset even when its visible offset is clamped to
        // zero. Do not let an otherwise invisible wheel movement reappear on expand.
        e.Handled = true;
        GeneralScroll.ScrollToVerticalOffset(0);
    }

    private void OnMaintenanceExpanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MaintenanceSettings)) return;
        // Discard latent End/ScrollToBottom requests as well, without changing the
        // current visible position or intercepting keyboard access to the contents.
        GeneralScroll.ScrollToVerticalOffset(GeneralScroll.VerticalOffset);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, GeneralScroll)) return;
        if (e.ExtentHeightChange < 0 || e.ViewportHeightChange > 0
            || (e.ExtentHeightChange != 0 && GeneralScroll.ScrollableHeight == 0))
        {
            // Collapsing or enlarging the viewport can clamp the visible offset
            // while leaving the old request in IScrollInfo. Commit the clamped value.
            GeneralScroll.ScrollToVerticalOffset(GeneralScroll.VerticalOffset);
        }
    }
}
