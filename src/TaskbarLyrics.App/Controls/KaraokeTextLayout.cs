using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.App.Controls;

/// <summary>
/// Cached FormattedText layout + per-word pixel widths. Rebuild only when text/fonts/dpi change.
/// </summary>
public sealed class KaraokeTextLayout
{
    public required string Text { get; init; }
    public required FormattedText Formatted { get; init; }
    public required double TotalWidth { get; init; }
    public required double Height { get; init; }
    public required double[] WordWidths { get; init; }
    public required double[] WordStartX { get; init; }
    public required IReadOnlyList<QrcWord> Words { get; init; }
    public required double FontSize { get; init; }
    public required double PixelsPerDip { get; init; }
    public required string FontFingerprint { get; init; }

    public static KaraokeTextLayout Build(
        string text,
        IReadOnlyList<QrcWord> words,
        DisplayConfig display,
        double pixelsPerDip)
    {
        text ??= string.Empty;
        words ??= Array.Empty<QrcWord>();

        // Prefer concatenated words when available (matches QRC)
        if (words.Count > 0)
        {
            var joined = string.Concat(words.Select(w => w.Text ?? string.Empty));
            if (!string.IsNullOrEmpty(joined))
            {
                text = joined;
            }
        }

        var ft = CreateFormattedText(text, display, pixelsPerDip);

        var wordWidths = new double[Math.Max(words.Count, 0)];
        var wordStarts = new double[Math.Max(words.Count, 0)];

        if (words.Count > 0)
        {
            var charIndex = 0;
            for (var i = 0; i < words.Count; i++)
            {
                var wText = words[i].Text ?? string.Empty;
                var len = wText.Length;
                double width;
                if (len <= 0)
                {
                    width = 0;
                }
                else
                {
                    // Measure substring width via FormattedText geometry
                    width = MeasureSubstringWidth(ft, text, charIndex, len);
                }

                wordWidths[i] = width;
                wordStarts[i] = i == 0 ? 0 : wordStarts[i - 1] + wordWidths[i - 1];
                charIndex += len;
            }
        }

        var fingerprint =
            $"{display.Fonts.Chinese}|{display.Fonts.Japanese}|{display.Fonts.Korean}|" +
            $"{display.Fonts.Latin}|{display.Fonts.Cyrillic}|{display.Fonts.Arabic}|{display.Fonts.Other}|" +
            $"{display.FontSize:F2}";

        return new KaraokeTextLayout
        {
            Text = text,
            Formatted = ft,
            TotalWidth = ft.WidthIncludingTrailingWhitespace,
            Height = ft.Height,
            WordWidths = wordWidths,
            WordStartX = wordStarts,
            Words = words,
            FontSize = display.FontSize,
            PixelsPerDip = pixelsPerDip,
            FontFingerprint = fingerprint
        };
    }

    public double ComputeHighlightWidth(int currentWordIndex, double wordProgress)
    {
        if (WordWidths.Length == 0)
        {
            // Static line: whole line progress if any
            return Math.Clamp(wordProgress, 0, 1) * TotalWidth;
        }

        if (currentWordIndex < 0)
        {
            return 0;
        }

        var idx = Math.Min(currentWordIndex, WordWidths.Length - 1);
        var completed = WordStartX[idx];
        var progress = Math.Clamp(wordProgress, 0, 1);
        return completed + WordWidths[idx] * progress;
    }

    private static double MeasureSubstringWidth(FormattedText ft, string full, int start, int length)
    {
        if (length <= 0 || start < 0 || start >= full.Length)
        {
            return 0;
        }

        length = Math.Min(length, full.Length - start);
        // BuildHighlightGeometry for range gives accurate glyph advance for that span
        try
        {
            var geo = ft.BuildHighlightGeometry(new Point(0, 0), start, length);
            if (geo is not null && !geo.Bounds.IsEmpty)
            {
                return geo.Bounds.Width;
            }
        }
        catch
        {
            // fallback below
        }

        // Fallback: proportional by character count (last resort)
        if (full.Length == 0)
        {
            return 0;
        }

        return ft.WidthIncludingTrailingWhitespace * (length / (double)full.Length);
    }

    /// <summary>Shared typography for the taskbar and the in-app lyric reader, including language-specific fonts.</summary>
    public static FormattedText CreateFormattedText(string text, DisplayConfig display, double pixelsPerDip,
        double? fontSize = null, Brush? brush = null)
    {
        var typeface = new Typeface(new FontFamily(display.Fonts.Latin), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, fontSize ?? display.FontSize, brush ?? Brushes.White, pixelsPerDip);
        ApplyScriptFonts(formatted, text, display);
        return formatted;
    }
    public static string FontFamiliesKey(DisplayConfig display)
    {
        var f = display.Fonts;
        return $"{f.Chinese}|{f.Japanese}|{f.Korean}|{f.Latin}|{f.Cyrillic}|{f.Arabic}|{f.Other}";
    }

    private static void ApplyScriptFonts(FormattedText ft, string text, DisplayConfig display)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var hasKana = false;
        var scripts = new TextScript[text.Length];

        for (var i = 0; i < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            var script = UnicodeScriptDetector.Detect(rune);
            if (script is TextScript.Hiragana or TextScript.Katakana)
            {
                hasKana = true;
            }

            var len = rune.Utf16SequenceLength;
            for (var k = 0; k < len && i + k < text.Length; k++)
            {
                scripts[i + k] = script;
            }

            i += len;
        }

        // Resolve Han -> Japanese if line has kana
        for (var i = 0; i < scripts.Length; i++)
        {
            if (scripts[i] == TextScript.Han && hasKana)
            {
                scripts[i] = TextScript.Hiragana; // marker for Japanese font family
            }
        }

        // Inherit punctuation fonts from neighbors
        for (var i = 0; i < scripts.Length; i++)
        {
            if (scripts[i] != TextScript.Other)
            {
                continue;
            }

            TextScript? prev = null;
            for (var p = i - 1; p >= 0; p--)
            {
                if (scripts[p] != TextScript.Other)
                {
                    prev = scripts[p];
                    break;
                }
            }

            TextScript? next = null;
            for (var n = i + 1; n < scripts.Length; n++)
            {
                if (scripts[n] != TextScript.Other)
                {
                    next = scripts[n];
                    break;
                }
            }

            scripts[i] = prev ?? next ?? TextScript.Latin;
        }

        // Apply runs
        var runStart = 0;
        var runScript = scripts[0];
        for (var i = 1; i <= scripts.Length; i++)
        {
            if (i < scripts.Length && scripts[i] == runScript)
            {
                continue;
            }

            var familyName = ScriptToFamily(runScript, display.Fonts);
            try
            {
                ft.SetFontFamily(new FontFamily(familyName), runStart, i - runStart);
            }
            catch
            {
                ft.SetFontFamily(new FontFamily("Segoe UI"), runStart, i - runStart);
            }

            if (i < scripts.Length)
            {
                runStart = i;
                runScript = scripts[i];
            }
        }
    }

    private static string ScriptToFamily(TextScript script, FontConfig fonts)
    {
        return script switch
        {
            TextScript.Han => fonts.Chinese,
            TextScript.Hiragana or TextScript.Katakana => fonts.Japanese,
            TextScript.Hangul => fonts.Korean,
            TextScript.Latin => fonts.Latin,
            TextScript.Cyrillic => fonts.Cyrillic,
            TextScript.Arabic => fonts.Arabic,
            _ => fonts.Other
        };
    }
}

