using Control = System.Windows.Controls.Control;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Pages;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.Cache;
namespace TaskbarLyrics.App.Windows;
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _model;
    private PlaybackTaskbarPreview? _taskbarPreview;
    private readonly Dictionary<SettingsPage, UserControl> _pages = new();
    private readonly bool _constrainToScreen;
    private SettingsPage _page = SettingsPage.Overview;
    private readonly SettingsNavigation _navigation = new();
    public bool CanGoBack => _navigation.CanGoBack;
    public SettingsPage CurrentPage => _page;
    public bool AllowClose { get; set; }
    public SettingsViewModel ViewModel => _model;
    public SettingsWindow(ConfigService config, TrackCoordinator coordinator, CacheStatisticsService statistics, Action openRematch)
        : this(new SettingsViewModel(config, new SettingsBackend(coordinator, statistics))) { _model.RematchRequested += openRematch; }
    public SettingsWindow(SettingsViewModel model, bool constrainToScreen = true)
    {
        _model = model; _constrainToScreen = constrainToScreen;
        InitializeComponent(); DataContext = model;
        SourceInitialized += (_, _) =>
        {
            _taskbarPreview = new PlaybackTaskbarPreview(this, model);
            if (SystemParameters.HighContrast || !TaskbarLyrics.Windows.WindowFrame.UseRoundedBorderlessFrame(new WindowInteropHelper(this).Handle)) return;
            System.Windows.Shell.WindowChrome.GetWindowChrome(this).CornerRadius = new CornerRadius(0);
            WindowSurface.CornerRadius = new CornerRadius(0);
            WindowSurface.BorderThickness = new Thickness(0);
        };
        model.NavigateRequested += Navigate;
        model.Confirm = (title, message) => ModernDialogWindow.Confirm(this, title, message, danger: !title.StartsWith("恢复"),
            confirmText: title.StartsWith("清除", StringComparison.Ordinal) ? "清除" : title);
        Loaded += (_, _) => { if (_constrainToScreen) ClampToWorkArea(); _model.Activate(_page); MoveNavigationIndicator(NavList.SelectedItem as Control ?? AboutButton); };
        IsVisibleChanged += (_, _) => { if (IsVisible) _model.Activate(_page); else _model.Deactivate(); };
        Closed += (_, _) => { _taskbarPreview?.Dispose(); _model.NavigateRequested -= Navigate; _model.Dispose(); };
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        Navigate(SettingsPage.Overview);
    }
    public void Navigate(SettingsPage page)
    {
        page = SettingsNavigation.Normalize(page);
        if (!_navigation.Navigate(page) && PageHost.Content is not null) return;
        ShowPage(page);
    }
    public bool GoBack()
    {
        if (!_navigation.TryGoBack(out var page)) return false;
        ShowPage(page, backwards: true);
        return true;
    }
    private void ShowPage(SettingsPage page, bool backwards = false)
    {
        if (PageHost is null) return;
        page = SettingsNavigation.Normalize(page);
        _page = page;
        if (!_pages.TryGetValue(page, out var content))
        {
            content = page switch { SettingsPage.Appearance => new AppearancePage(), SettingsPage.Synchronization => new SynchronizationPage(), SettingsPage.General => new GeneralPage(), SettingsPage.About => new AboutPage(), _ => new OverviewPage() };
            _pages.Add(page, content);
        }
        PageHost.Content = content;
        var item = NavList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == page.ToString());
        if (!Equals(NavList.SelectedItem, item)) NavList.SelectedItem = item;
        if (IsVisible) _model.Activate(page);
        if (IsVisible) Motion.Reveal(PageHost, backwards);
        MoveNavigationIndicator(item as Control ?? AboutButton);
    }
    private Border? _movingNavigationRoot;
    private Control? _navigationTarget;
    private long _navigationRevision;
    private void MoveNavigationIndicator(Control target)
    {
        var revision = ++_navigationRevision;
        var previous = _navigationTarget;
        _navigationTarget = target;
        NavigationIndicator.BeginAnimation(UIElement.OpacityProperty, null);
        _movingNavigationRoot?.BeginAnimation(Border.BackgroundProperty, null);
        _movingNavigationRoot = null;
        NavigationIndicator.Visibility = Visibility.Collapsed;
        target.ApplyTemplate();
        if (!IsVisible || !target.IsVisible) return;
        var point = target.TranslatePoint(new Point(), SidebarLayout);
        if (previous is null || ReferenceEquals(previous, target) || !Motion.Allowed(this)) return;
        if (target.Template.FindName("Root", target) is not Border root) return;
        _movingNavigationRoot = root;
        var mask = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(180) };
        mask.KeyFrames.Add(new DiscreteObjectKeyFrame(Brushes.Transparent, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        root.BeginAnimation(Border.BackgroundProperty, mask);
        NavigationIndicator.Width = target.ActualWidth;
        NavigationIndicator.Height = target.ActualHeight;
        Canvas.SetLeft(NavigationIndicator, point.X);
        Canvas.SetTop(NavigationIndicator, point.Y);
        NavigationIndicator.Visibility = Visibility.Visible;
        // Keep the selection surface inside its destination; never paint across sidebar gaps.
        var move = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        move.Completed += (_, _) =>
        {
            if (revision != _navigationRevision) return;
            root.BeginAnimation(Border.BackgroundProperty, null);
            _movingNavigationRoot = null;
            NavigationIndicator.Visibility = Visibility.Collapsed;
        };
        NavigationIndicator.BeginAnimation(UIElement.OpacityProperty, move);
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != Motion.ReduceProperty || e.NewValue is not true || NavigationIndicator is null) return;
        ++_navigationRevision;
        NavigationIndicator.BeginAnimation(UIElement.OpacityProperty, null);
        NavigationIndicator.Visibility = Visibility.Collapsed;
        _movingNavigationRoot?.BeginAnimation(Border.BackgroundProperty, null);
        _movingNavigationRoot = null;
    }
    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, NavList)) return;
        if (NavList.SelectedItem is ListBoxItem { Tag: string tag } && Enum.TryParse<SettingsPage>(tag, out var page) && (page != _page || PageHost.Content is null)) Navigate(page);
    }
    private void ClampToWorkArea() => WindowPlacement.ClampToWorkArea(this);
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e) { if (!AllowClose) { e.Cancel = true; Hide(); } base.OnClosing(e); }
}
