using System.Windows;
using System.Windows.Controls;
namespace TaskbarLyrics.App.Pages;
public partial class OverviewPage : UserControl
{
    private bool _compact;
    public OverviewPage() { InitializeComponent(); }
    private void HeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _compact = e.NewSize.Width < 620;
        Grid.SetRow(Actions, _compact ? 1 : 0);
        Grid.SetColumn(Actions, _compact ? 0 : 1);
        Grid.SetColumnSpan(Actions, _compact ? 2 : 1);
        Grid.SetColumnSpan(SongDetails, _compact ? 2 : 1);
        Actions.Margin = _compact ? new Thickness(0, 18, 0, 0) : new Thickness(0);
        CurrentLyric.FontSize = _compact ? 23 : 26;
        ResizeContent();
    }
    private void ViewportSizeChanged(object sender, SizeChangedEventArgs e) => ResizeContent();
    private void ResizeContent()
    {
        if (PlaybackLayout is null || PlaybackViewport is null) return;
        // Fit the viewport; only very short windows scroll to preserve usable controls and lyrics.
        var height = Math.Max(_compact ? 500 : 420, PlaybackViewport.ActualHeight - PlaybackLayout.Margin.Top - PlaybackLayout.Margin.Bottom);
        if (Math.Abs(PlaybackLayout.Height - height) > .1 || double.IsNaN(PlaybackLayout.Height)) PlaybackLayout.Height = height;
    }
}
