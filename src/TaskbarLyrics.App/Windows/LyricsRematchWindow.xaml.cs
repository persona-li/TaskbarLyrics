using System.Windows;
using System.Windows.Input;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.App.Presentation;
namespace TaskbarLyrics.App.Windows;
public partial class LyricsRematchWindow : Window
{
    private readonly RematchViewModel _model;
    public LyricsRematchWindow(TrackCoordinator coordinator) : this(new RematchViewModel(new RematchBackend(coordinator))) { }
    public LyricsRematchWindow(RematchViewModel model, bool autoSearch = true, bool constrainToScreen = true)
    {
        InitializeComponent(); _model = model; DataContext = model;
        _model.Applied += OnApplied;
        Loaded += async (_, _) => { if (constrainToScreen) WindowPlacement.ClampToWorkArea(this); DialogContent.Focus(); if (autoSearch) await model.SearchAsync(true); };
        Closed += (_, _) => { _model.Applied -= OnApplied; _model.Dispose(); };
    }
    private void OnApplied() { DialogResult = true; Close(); }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
