using System.Windows;
using System.Windows.Media;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.App.Controls;

/// <summary>
/// Dual-pass FormattedText karaoke renderer with clip progressive fill.
/// </summary>
public sealed class KaraokeTextControl : FrameworkElement
{
    private AppConfig _config = AppConfig.CreateDefault();
    private KaraokeRenderState? _state;
    private KaraokeTextLayout? _layout;
    private double _panOffsetX;
    private double _targetPanOffsetX;
    private string _layoutKey = string.Empty;

    public KaraokeTextControl()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    private string? _placementAlignment;
    public string? PlacementAlignment
    {
        get => _placementAlignment;
        set { if (_placementAlignment == value) return; _placementAlignment = value; InvalidateVisual(); }
    }

    public void ApplyConfig(AppConfig config)
    {
        _config = config ?? AppConfig.CreateDefault();
        _layout = null;
        _layoutKey = string.Empty;
        InvalidateVisual();
    }

    public void UpdateState(KaraokeRenderState? state)
    {
        _state = state;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var state = _state;
        if (state is null || string.IsNullOrEmpty(state.Text))
        {
            return;
        }

        var display = _config.Display;
        var karaoke = _config.Karaoke;
        var dpi = VisualTreeHelper.GetDpi(this);
        var ppd = dpi.PixelsPerDip;
        if (ppd <= 0)
        {
            ppd = 1.0;
        }

        var availableWidth = Math.Max(1, ActualWidth);
        var availableHeight = Math.Max(1, ActualHeight);

        EnsureLayout(state, display, ppd);

        var layout = _layout;
        if (layout is null)
        {
            return;
        }

        // Highlight width
        double highlightWidth = 0;
        var karaokeOn = karaoke.Enabled && !state.StaticLineOnly && state.Words.Count > 0;
        if (karaokeOn)
        {
            highlightWidth = layout.ComputeHighlightWidth(state.CurrentWordIndex, state.CurrentWordProgress);
        }
        else if (!state.StaticLineOnly && state.CurrentWordProgress > 0 && state.Words.Count == 0)
        {
            // LRC line progress
            highlightWidth = layout.TotalWidth * Math.Clamp(state.CurrentWordProgress, 0, 1);
            karaokeOn = karaoke.Enabled;
        }

        // Auto-pan
        if (karaoke.AutoPanLongLyrics && layout.TotalWidth > availableWidth + 0.5)
        {
            UpdatePan(layout, state, availableWidth, karaoke.AutoPanRightPaddingPx);
        }
        else
        {
            _panOffsetX = 0;
            _targetPanOffsetX = 0;
        }

        // Base X for text (alignment inside control, then pan)
        if (layout.TotalWidth <= availableWidth)
        {
            _panOffsetX = 0;
        }
        var baseX = KaraokeTextPosition.ComputeOriginX(
            availableWidth, layout.TotalWidth, PlacementAlignment ?? display.TextAlignment, _panOffsetX);

        var baseY = (availableHeight - layout.Height) / 2.0 + display.VerticalOffsetPx;

        // Clip to control bounds
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, availableWidth, availableHeight)));

        var origin = new Point(baseX, baseY);

        // Shadow (once, normal text)
        if (display.ShadowEnabled)
        {
            var shadowBrush = new SolidColorBrush(display.ParsedShadowColor);
            shadowBrush.Freeze();
            var shadowOrigin = new Point(
                origin.X + display.ShadowOffsetX,
                origin.Y + display.ShadowOffsetY);
            DrawText(dc, layout, shadowBrush, shadowOrigin);
        }

        // Normal full line
        var normalBrush = new SolidColorBrush(display.ParsedNormalColor);
        normalBrush.Freeze();
        DrawText(dc, layout, normalBrush, origin);

        // Highlight clipped layer
        if (karaokeOn && highlightWidth > 0.5)
        {
            var highlightBrush = new SolidColorBrush(display.ParsedHighlightColor);
            highlightBrush.Freeze();

            // Clip in control coordinates: from text left to highlight edge
            var clipLeft = origin.X;
            var clipWidth = Math.Min(highlightWidth, layout.TotalWidth + 2);
            // Only show portion of highlight within viewport
            var clipRect = new Rect(clipLeft, 0, Math.Max(0, clipWidth), availableHeight);
            dc.PushClip(new RectangleGeometry(clipRect));
            DrawText(dc, layout, highlightBrush, origin);
            dc.Pop();
        }

        dc.Pop(); // outer clip
    }

    private void EnsureLayout(KaraokeRenderState state, DisplayConfig display, double ppd)
    {
        var words = state.Words ?? Array.Empty<QrcWord>();
        var text = state.Text ?? string.Empty;
        var key =
            $"{text}|{words.Count}|{display.FontSize:F2}|{ppd:F3}|" +
            $"{display.Fonts.Chinese}|{display.Fonts.Japanese}|{display.Fonts.Korean}|{display.Fonts.Latin}";

        if (_layout is not null && _layoutKey == key)
        {
            return;
        }

        _layout = KaraokeTextLayout.Build(text, words, display, ppd);
        _layoutKey = key;
    }

    private void UpdatePan(
        KaraokeTextLayout layout,
        KaraokeRenderState state,
        double availableWidth,
        int rightPadding)
    {
        if (state.CurrentWordIndex < 0 || layout.WordStartX.Length == 0)
        {
            _targetPanOffsetX = 0;
            _panOffsetX = Smooth(_panOffsetX, _targetPanOffsetX);
            return;
        }

        var idx = Math.Clamp(state.CurrentWordIndex, 0, layout.WordStartX.Length - 1);
        var wordLeft = layout.WordStartX[idx];
        var wordRight = wordLeft + layout.WordWidths[idx];
        var progress = Math.Clamp(state.CurrentWordProgress, 0, 1);
        var cursorX = wordLeft + layout.WordWidths[idx] * progress;

        // Desired: keep cursor around 70% of viewport
        var desiredCursorInView = availableWidth * 0.70;
        var maxPan = Math.Max(0, layout.TotalWidth - availableWidth);
        var target = Math.Clamp(cursorX - desiredCursorInView, 0, maxPan);

        // If word fully visible with small margin, don't pan aggressively
        var viewLeft = _panOffsetX;
        var viewRight = _panOffsetX + availableWidth - rightPadding;
        if (wordLeft >= viewLeft + 8 && wordRight <= viewRight)
        {
            // keep current target gently
            _targetPanOffsetX = _panOffsetX;
        }
        else
        {
            _targetPanOffsetX = target;
        }

        _panOffsetX = Smooth(_panOffsetX, _targetPanOffsetX);
    }

    private static double Smooth(double current, double target)
    {
        var d = target - current;
        if (Math.Abs(d) < 0.5)
        {
            return target;
        }

        // Light lerp (~half way per frame at 30Hz feels smooth)
        return current + d * 0.35;
    }

    private static void DrawText(DrawingContext dc, KaraokeTextLayout layout, Brush brush, Point origin)
    {
        // FormattedText is immutable regarding brush used at draw time via DrawText overload
        // Recreate draw with brush: use BuildGeometry for fill with custom brush
        try
        {
            var geo = layout.Formatted.BuildGeometry(origin);
            if (geo is not null)
            {
                dc.DrawGeometry(brush, null, geo);
                return;
            }
        }
        catch
        {
            // fallback
        }

        // Fallback DrawText (uses FormattedText's own brush — set via SetForegroundBrush)
        layout.Formatted.SetForegroundBrush(brush);
        dc.DrawText(layout.Formatted, origin);
    }
}
