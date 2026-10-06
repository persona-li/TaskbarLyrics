using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.Core.Models;
namespace TaskbarLyrics.App.Controls;
public partial class LyricsPreview : UserControl
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private bool _paused, _light;
    private string? _measuredText;
    private PlaybackPresentation? _pausedState;
    private double _position = 1800;
    private static readonly QrcWord[] DemoWords = "此刻，风经过窗前".Select((c, i) => new QrcWord { Text = c.ToString(), StartMs = i * 400, DurationMs = 400 }).ToArray();
    public static readonly DependencyProperty ConfigProperty = DependencyProperty.Register(nameof(Config), typeof(AppConfig), typeof(LyricsPreview), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty StateReaderProperty = DependencyProperty.Register(nameof(StateReader), typeof(Func<PlaybackPresentation>), typeof(LyricsPreview));
    public Func<PlaybackPresentation>? StateReader { get => (Func<PlaybackPresentation>?)GetValue(StateReaderProperty); set => SetValue(StateReaderProperty, value); }
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(PlaybackPresentation), typeof(LyricsPreview), new PropertyMetadata(null, Changed));
    public AppConfig? Config { get => (AppConfig?)GetValue(ConfigProperty); set => SetValue(ConfigProperty, value); }
    public PlaybackPresentation? State { get => (PlaybackPresentation?)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public bool IsPreviewRunning => _timer.IsEnabled;
    public bool IsPaused => _paused;
    public static readonly DependencyProperty CurrentSongOnlyProperty = DependencyProperty.Register(nameof(CurrentSongOnly), typeof(bool), typeof(LyricsPreview), new PropertyMetadata(false, CurrentSongOnlyChanged));
    public bool CurrentSongOnly { get => (bool)GetValue(CurrentSongOnlyProperty); set => SetValue(CurrentSongOnlyProperty,value); }
    private static void CurrentSongOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p=(LyricsPreview)d;
        if(p.PreviewToolbar is null) return;
        p.PreviewToolbar.Visibility=p.CurrentSongOnly ? Visibility.Collapsed : Visibility.Visible;
        p.PreviewLayout.RowDefinitions[0].Height=new GridLength(p.CurrentSongOnly ? 0 : 32);
        p.PreviewLayout.RowDefinitions[1].Height=new GridLength(p.CurrentSongOnly ? 0 : 12);
        if(p.CurrentSongOnly) p.SourceChoice.SelectedIndex=1;
    }
    public LyricsPreview()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _timer.Tick += (_, _) => { _position += _clock.Elapsed.TotalMilliseconds; _clock.Restart(); RenderFrame(_position); };
        IsVisibleChanged += (_, _) => UpdateTimer(); Loaded += (_, _) => { ApplyConfig(); UpdateTimer(); }; Unloaded += (_, _) => { _timer.Stop(); _clock.Stop(); ApplyBackground(false); };
    }
    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        var p = (LyricsPreview)o; if (p.Karaoke is null || p._timer is null) return;
        if (e.Property == ConfigProperty) p.ApplyConfig();
        if (e.Property != StateProperty || !p._paused) p.RenderFrame(p._position);
        p.UpdateTimer();
    }
    public void ApplyConfig() { _measuredText = null; if (Config is not null) Karaoke.ApplyConfig(Config); RenderFrame(_position); }
    public void RenderFrame(double positionMs)
    {
        if (SourceChoice.SelectedIndex == 1)
        {
            var current = _paused ? _pausedState : StateReader?.Invoke() ?? State;
            UpdatePreviewState(current?.HasTrack == true ? current.Render : null);
            PreviewHint.Visibility = current?.HasTrack == true ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            var p = positionMs % (DemoWords.Length * 400);
            UpdatePreviewState(new("此刻，风经过窗前", DemoWords, (int)(p / 400), p % 400 / 400, true, false));
            PreviewHint.Visibility = Visibility.Collapsed;
        }
    }
    private void UpdatePreviewState(KaraokeRenderState? state)
    {
        var text = state?.Text ?? "";
        if (_measuredText != text)
        {
            _measuredText = text;
            var display = Config?.Display ?? new DisplayConfig();
            var layout = KaraokeTextLayout.Build(text, Array.Empty<QrcWord>(), display, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            // Reserve room for the configured vertical displacement and shadow before
            // fitting the complete render into the preview; never shrink the config itself.
            var shadow = display.ShadowEnabled ? Math.Abs(display.ShadowOffsetY) : 0;
            Karaoke.Height = Math.Max(48, layout.Height + 2 * (Math.Abs(display.VerticalOffsetPx) + shadow + 2));
        }
        Karaoke.UpdateState(state);
    }
    private void PreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Karaoke is not null) Karaoke.Width = Math.Max(1, e.NewSize.Width / 1.15);
    }
    private void UpdateTimer()
    {
        if (_timer is null) return;
        var run = IsVisible && !_paused && Config?.General.ReduceMotion != true && Motion.Allowed(this);
        if (run && !_timer.IsEnabled) { _clock.Restart(); _timer.Start(); }
        else if (!run) { _timer.Stop(); _clock.Stop(); }
        var reduced = Config?.General.ReduceMotion == true || !Motion.Allowed(this);
        var label = reduced ? "下一步" : _paused ? "继续预览" : "暂停预览";
        PlayButton.Content = reduced || _paused ? "\uE768" : "\uE769";
        PlayButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, label);
    }
    private void PlayClick(object sender, RoutedEventArgs e)
    {
        if (Config?.General.ReduceMotion == true || !Motion.Allowed(this)) { RenderFrame(_position += 800); return; }
        SetPaused(!_paused);
    }
    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pausedState = paused ? StateReader?.Invoke() ?? State : null;
        RenderFrame(_position);
        UpdateTimer();
    }
    private void BackgroundSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string background }) return;
        _light = background == "Light";
        // Selection only changes the simulated taskbar, never the app or lyric palette.
        if (TaskbarSurface is null || PreviewHint is null) return;
        ApplyBackground(IsLoaded && Config?.General.ReduceMotion != true && Motion.Allowed(this));
    }
    private void ApplyBackground(bool animate)
    {
        var duration = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fromX = BackgroundIndicatorTranslate.X;
        var targetX = _light ? 0 : 48;
        BackgroundIndicatorTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        BackgroundIndicatorTranslate.X = targetX;
        if (animate)
            BackgroundIndicatorTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(fromX, targetX, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });

        var target = (Color)ColorConverter.ConvertFromString(_light ? "#F0F0F2" : "#242426");
        var from = (TaskbarSurface.Background as SolidColorBrush)?.Color ?? target;
        // A local brush avoids animating shared theme resources. Repeated clicks start
        // from the currently displayed color, without touching lyric playback or layout.
        var brush = new SolidColorBrush(target);
        TaskbarSurface.Background = brush;
        if (animate)
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(from, target, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        PreviewHint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_light ? "#636369" : "#BFBFC5"));
    }
    private void SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_timer is null) return;
        if (_paused && SourceChoice.SelectedIndex == 1) _pausedState = StateReader?.Invoke() ?? State;
        RenderFrame(_position);
    }
}
