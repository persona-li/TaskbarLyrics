using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Control = System.Windows.Controls.Control;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.Core.Lyrics;
using TaskbarLyrics.Core.Models;
namespace TaskbarLyrics.App.Controls;

/// <summary>Wrapped lyric reader. Only the visible rows are painted; document layouts are cached.</summary>
public sealed class ScrollingLyrics : Control
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(PlaybackPresentation), typeof(ScrollingLyrics), new PropertyMetadata(null, StateChanged));
    public PlaybackPresentation? State { get => (PlaybackPresentation?)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public static readonly DependencyProperty ConfigProperty = DependencyProperty.Register(nameof(Config), typeof(AppConfig), typeof(ScrollingLyrics), new PropertyMetadata(null, (o, _) => ((ScrollingLyrics)o).ResetLayout()));
    public AppConfig? Config { get => (AppConfig?)GetValue(ConfigProperty); set => SetValue(ConfigProperty, value); }
    public static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(nameof(ScrollOffset), typeof(double), typeof(ScrollingLyrics), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double ScrollOffset { get => (double)GetValue(ScrollOffsetProperty); private set => SetValue(ScrollOffsetProperty, value); }
    public int VisibleLineCount => _rows.Count;
    public int ActiveLineIndex { get; private set; } = -1;
    private readonly LyricsSynchronizer _synchronizer = new();
    private readonly Stopwatch _sinceState = new();
    private readonly DispatcherTimer _timer;
    private LyricsDocument? _document;
    private object? _track;
    private IReadOnlyList<LyricReaderRow> _rows = Array.Empty<LyricReaderRow>();
    private readonly List<LayoutRow> _layouts = new();
    private sealed record LayoutRow(LyricReaderRow Row, FormattedText Text, FormattedText? Translation, double Top, double Height);
    private double _layoutWidth = -1, _layoutFontSize, _extent, _targetOffset = double.NaN;
    private double _layoutDpi;
    private string _fontKey = "", _colorKey = "";
    private Brush _normal = Brushes.SteelBlue, _highlight = Brushes.LightBlue;
    public ScrollingLyrics()
    {
        FontSize = 26;
        FontWeight = FontWeights.SemiBold;
        ClipToBounds = true;
        Focusable = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(32) };
        _timer.Tick += (_, _) => { Follow(false); InvalidateVisual(); };
        IsVisibleChanged += (_, _) => UpdateTimer();
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => { _timer.Stop(); BeginAnimation(ScrollOffsetProperty, null); };
        SizeChanged += (_, e) =>
        {
            if (OpacityMask is LinearGradientBrush gradient)
            {
                var mask = gradient.Clone();
                mask.MappingMode = BrushMappingMode.Absolute;
                mask.StartPoint = new Point(0, 0);
                mask.EndPoint = new Point(0, Math.Max(1, e.NewSize.Height));
                OpacityMask = mask;
            }
            if (e.WidthChanged) _layoutWidth = -1;
            else Follow(true);
            InvalidateVisual();
        };
    }
    private void UpdateTimer()
    {
        if (IsLoaded && IsVisible && State?.IsPlaying == true) _timer.Start(); else _timer.Stop();
    }
    private static void StateChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        var view = (ScrollingLyrics)o;
        var state = view.State;
        view._sinceState.Restart();
        var doc = state?.HasTrack == true && state.Health == LyricsHealth.Ready ? state.Raw.Document : null;
        if (!ReferenceEquals(doc, view._document) || !Equals(view._track, state?.EditContext))
        {
            view._track = state?.EditContext;
            view._document = doc;
            view._layouts.Clear();
            view._extent = 0;
            view._targetOffset = double.NaN;
            view._rows = doc is null ? Array.Empty<LyricReaderRow>() : LyricReaderRows.Build(doc);
            view.BeginAnimation(ScrollOffsetProperty, null);
            view.ScrollOffset = 0;
            view.ResetLayout();
        }
        view.UpdateColors();
        view.UpdateTimer();
        view.Follow(false);
        view.InvalidateVisual();
    }
    private void ResetLayout()
    {
        _layoutWidth = -1;
        UpdateColors();
        InvalidateVisual();
    }
    private void UpdateColors()
    {
        var colors = Config?.Display ?? new DisplayConfig();
        var key = colors.NormalColor + colors.HighlightColor;
        if (_colorKey == key) return;
        _colorKey = key;
        _normal = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.NormalColor)); _normal.Freeze();
        _highlight = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.HighlightColor)); _highlight.Freeze();
        InvalidateVisual();
    }
    private FormattedText Format(string text, double size, Brush color)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), size, color,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        formatted.MaxTextWidth = Math.Max(1, ActualWidth - 8);
        formatted.TextAlignment = TextAlignment.Left;
        return formatted;
    }
    private void EnsureLayout()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fonts = FontFamily.Source + "|" + FontWeight;
        if (_layoutWidth == ActualWidth && _layoutFontSize == FontSize && _layoutDpi == dpi && _fontKey == fonts) return;
        _layoutWidth = ActualWidth; _layoutFontSize = FontSize; _layoutDpi = dpi; _fontKey = fonts;
        _layouts.Clear(); _extent = 0; _targetOffset = double.NaN;
        foreach (var row in _rows)
        {
            var text = Format(row.Text, FontSize, Foreground);
            var translated = string.IsNullOrWhiteSpace(row.Translation) ? null : Format(row.Translation, FontSize * .60, Foreground);
            var height = text.Height + (translated?.Height + 5 ?? 0) + 18;
            _layouts.Add(new(row, text, translated, _extent, height));
            _extent += height;
        }
        Follow(true);
    }
    private CurrentLyricState? ReadLyric()
    {
        if (_document is null || State is null) return null;
        // Extrapolate only inside a bounded state update interval; paused state never advances.
        var elapsed = State.IsPlaying ? Math.Min(400, _sinceState.ElapsedMilliseconds) : 0;
        return _synchronizer.Resolve(_document.Lines, TimeSpan.FromMilliseconds(State.Raw.Lyric.PositionMs + elapsed));
    }
    private void Follow(bool immediately)
    {
        var lyric = ReadLyric();
        ActiveLineIndex = lyric?.LineIndex ?? -1;
        if (_layouts.Count == 0 || lyric is null) return;
        var row = _layouts.FirstOrDefault(r => r.Row.Index == ActiveLineIndex)
            ?? _layouts.LastOrDefault(r => _document!.Lines[r.Row.Index].StartMs <= lyric.PositionMs) ?? _layouts[0];
        var target = row.Top + (row.Height - 18) / 2 - ActualHeight / 2;
        if (Math.Abs(target - _targetOffset) < .1) return;
        var from = ScrollOffset;
        _targetOffset = target;
        BeginAnimation(ScrollOffsetProperty, null);
        ScrollOffset = target;
        if (!immediately && IsLoaded && Motion.Allowed(this) && Config?.General.ReduceMotion != true)
            BeginAnimation(ScrollOffsetProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        EnsureLayout();
        if (_layouts.Count == 0)
        {
            var fallback = Format(State?.LyricText ?? "", FontSize, _normal);
            dc.DrawText(fallback, new Point(4, Math.Max(0, (ActualHeight - fallback.Height) / 2)));
            return;
        }
        var lyric = ReadLyric();
        foreach (var row in _layouts)
        {
            var y = row.Top - ScrollOffset;
            if (y + row.Height < 0 || y > ActualHeight) continue;
            var active = row.Row.Index == lyric?.LineIndex;
            row.Text.SetForegroundBrush(active ? _normal : Foreground);
            var origin = new Point(4, y);
            var upcoming = lyric is not null && _document!.Lines[row.Row.Index].StartMs > lyric.PositionMs;
            dc.PushOpacity(active || SystemParameters.HighContrast ? 1 : upcoming ? .42 : .58);
            dc.DrawText(row.Text, origin);
            if (active && lyric is not null && Config?.Karaoke.Enabled != false) DrawHighlight(dc, row, origin, lyric);
            if (row.Translation is not null)
            {
                row.Translation.SetForegroundBrush(Foreground);
                dc.DrawText(row.Translation, new Point(4, y + row.Text.Height + 5));
            }
            dc.Pop();
        }
    }
    private void DrawHighlight(DrawingContext dc, LayoutRow row, Point origin, CurrentLyricState lyric)
    {
        var line = _document!.Lines[row.Row.Index];
        double completed = line.Words.Count == 0 ? row.Row.Text.Length * lyric.WordProgress :
            line.Words.Take(Math.Max(0, lyric.WordIndex)).Sum(w => w.Text.Length)
            + (lyric.WordIndex >= 0 && lyric.WordIndex < line.Words.Count ? line.Words[lyric.WordIndex].Text.Length * lyric.WordProgress : 0);
        completed = Math.Clamp(completed, 0, row.Row.Text.Length);
        var whole = (int)completed;
        row.Text.SetForegroundBrush(_highlight);
        if (whole > 0)
        {
            var clip = row.Text.BuildHighlightGeometry(origin, 0, whole);
            if (clip is not null) { dc.PushClip(clip); dc.DrawText(row.Text, origin); dc.Pop(); }
        }
        if (whole < row.Row.Text.Length && completed > whole)
        {
            var clip = row.Text.BuildHighlightGeometry(origin, whole, 1);
            if (clip is not null)
            {
                var bounds = clip.Bounds;
                bounds.Width *= completed - whole;
                dc.PushClip(new RectangleGeometry(bounds)); dc.PushClip(clip);
                dc.DrawText(row.Text, origin); dc.Pop(); dc.Pop();
            }
        }
    }
}
