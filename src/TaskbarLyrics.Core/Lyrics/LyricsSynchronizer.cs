using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>
/// Maps a playback position (ms) to current QRC line / word / progress.
/// Uses binary search over sorted StartMs lines.
/// Adaptive interlude: short gaps hold previous completed line until next starts;
/// long gaps hold for <see cref="LongInterludeHoldMs"/> then clear.
/// </summary>
public sealed class LyricsSynchronizer
{
    /// <summary>
    /// Gaps ≤ this (ms) keep the previous completed line until the next line starts (no blank).
    /// </summary>
    public const int ShortInterludeThresholdMs = 2000;

    /// <summary>
    /// For gaps &gt; <see cref="ShortInterludeThresholdMs"/>, hold previous completed line this long then empty.
    /// Also used after the last line (no next).
    /// </summary>
    public const int LongInterludeHoldMs = 1500;

    /// <summary>
    /// Legacy alias for long interlude hold (last line / long instrumental gap).
    /// Prefer <see cref="LongInterludeHoldMs"/> constant for fixed product defaults.
    /// </summary>
    public int InterludeHoldMs { get; set; } = LongInterludeHoldMs;

    public CurrentLyricState Resolve(IReadOnlyList<QrcLine>? lines, TimeSpan position)
    {
        var positionMs = (long)position.TotalMilliseconds;
        if (positionMs < 0)
        {
            positionMs = 0;
        }

        if (lines is null || lines.Count == 0)
        {
            return Empty(positionMs);
        }

        var lineIndex = FindActiveLineIndex(
            lines,
            positionMs,
            ShortInterludeThresholdMs,
            InterludeHoldMs);

        if (lineIndex < 0)
        {
            return Empty(positionMs) with
            {
                PreviousLine = null,
                NextLine = lines.Count > 0 ? lines[0].Text : null
            };
        }

        var line = lines[lineIndex];
        var inInterlude = positionMs > line.EndMs;
        var prev = lineIndex > 0 ? lines[lineIndex - 1].Text : null;
        var next = lineIndex + 1 < lines.Count ? lines[lineIndex + 1].Text : null;

        // Past line end during adaptive hold: always fully completed KTV state
        if (inInterlude)
        {
            return CreateCompletedLineState(positionMs, lineIndex, line, prev, next);
        }

        if (line.Words is null || line.Words.Count == 0)
        {
            double lineProgress = 0;
            if (line.DurationMs > 0)
            {
                lineProgress = Clamp01((positionMs - line.StartMs) / (double)line.DurationMs);
            }
            else if (positionMs >= line.StartMs)
            {
                lineProgress = 0.0;
            }

            return new CurrentLyricState(
                PositionMs: positionMs,
                LineIndex: lineIndex,
                LineText: line.Text,
                WordIndex: -1,
                CurrentWord: null,
                WordProgress: lineProgress,
                PreviousLine: prev,
                NextLine: next,
                WordCount: 0,
                CompletedPrefix: null,
                RemainingSuffix: line.Text,
                InInterlude: false,
                CurrentLineStartMs: line.StartMs,
                CurrentWordStartMs: null);
        }

        var wordIndex = FindActiveWordIndex(line.Words, positionMs);
        string? currentWord = null;
        double wordProgress = 0;
        long? wordStart = null;
        string completed = string.Empty;
        string remaining = string.Empty;

        if (wordIndex < 0)
        {
            if (positionMs < line.Words[0].StartMs)
            {
                remaining = line.Text;
            }
            else
            {
                completed = line.Text;
                wordProgress = 1.0;
            }
        }
        else
        {
            var word = line.Words[wordIndex];
            currentWord = word.Text;
            wordStart = word.StartMs;
            if (word.DurationMs <= 0)
            {
                wordProgress = positionMs >= word.StartMs ? 1.0 : 0.0;
            }
            else
            {
                wordProgress = Clamp01((positionMs - word.StartMs) / (double)word.DurationMs);
            }

            for (var i = 0; i < wordIndex; i++)
            {
                completed += line.Words[i].Text;
            }

            remaining = string.Concat(line.Words.Skip(wordIndex + 1).Select(w => w.Text));
        }

        return new CurrentLyricState(
            PositionMs: positionMs,
            LineIndex: lineIndex,
            LineText: line.Text,
            WordIndex: wordIndex,
            CurrentWord: currentWord,
            WordProgress: wordProgress,
            PreviousLine: prev,
            NextLine: next,
            WordCount: line.Words.Count,
            CompletedPrefix: completed,
            RemainingSuffix: remaining,
            InInterlude: false,
            CurrentLineStartMs: line.StartMs,
            CurrentWordStartMs: wordStart);
    }

    /// <summary>
    /// Finds the line to display at <paramref name="positionMs"/> with adaptive interlude hold.
    /// Priority: active line by Start/End first; only then previous-line hold in gaps.
    /// </summary>
    public static int FindActiveLineIndex(
        IReadOnlyList<QrcLine> lines,
        long positionMs,
        int shortInterludeThresholdMs,
        int longInterludeHoldMs)
    {
        if (lines.Count == 0)
        {
            return -1;
        }

        // Binary search: last StartMs <= positionMs
        var lo = 0;
        var hi = lines.Count - 1;
        var best = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (lines[mid].StartMs <= positionMs)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best < 0)
        {
            return -1; // before first line — never preview first
        }

        var line = lines[best];

        // Active line window (highest priority once Start has been reached)
        if (positionMs < line.EndMs || line.DurationMs <= 0)
        {
            return best;
        }

        // Past line end — adaptive hold until next line or long-hold timeout
        var elapsedSinceEnd = positionMs - line.EndMs;
        if (elapsedSinceEnd < 0)
        {
            return best;
        }

        QrcLine? nextLine = best + 1 < lines.Count ? lines[best + 1] : null;

        // Next line already started → should not happen if StartMs ordering holds
        // (best is last with Start <= position, so next.Start > position).
        if (nextLine is not null && positionMs >= nextLine.StartMs)
        {
            return best + 1;
        }

        if (nextLine is not null)
        {
            var gapToNext = nextLine.StartMs - line.EndMs;
            if (gapToNext < 0)
            {
                gapToNext = 0;
            }

            // Short phrasing gap: hold completed line until next Start (no blank)
            if (gapToNext <= shortInterludeThresholdMs)
            {
                return best;
            }

            // Long instrumental-like gap: hold LongInterludeHoldMs then empty
            if (elapsedSinceEnd <= longInterludeHoldMs)
            {
                return best;
            }

            return -1;
        }

        // Last line: hold LongInterludeHoldMs then empty (never forever)
        if (elapsedSinceEnd <= longInterludeHoldMs)
        {
            return best;
        }

        return -1;
    }

    /// <summary>Legacy overload: fixed hold uses long-hold value only (no short-gap awareness).</summary>
    public static int FindActiveLineIndex(IReadOnlyList<QrcLine> lines, long positionMs, int interludeHoldMs) =>
        FindActiveLineIndex(lines, positionMs, shortInterludeThresholdMs: 0, longInterludeHoldMs: interludeHoldMs);

    public static int FindActiveWordIndex(IReadOnlyList<QrcWord> words, long positionMs)
    {
        if (words.Count == 0)
        {
            return -1;
        }

        var lo = 0;
        var hi = words.Count - 1;
        var best = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (words[mid].StartMs <= positionMs)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best < 0)
        {
            return -1;
        }

        var w = words[best];
        if (w.DurationMs <= 0)
        {
            if (best + 1 < words.Count)
            {
                return positionMs < words[best + 1].StartMs ? best : -1;
            }

            return best;
        }

        if (positionMs < w.StartMs + w.DurationMs)
        {
            return best;
        }

        if (best + 1 < words.Count && positionMs < words[best + 1].StartMs)
        {
            return best;
        }

        return best;
    }

    private static CurrentLyricState CreateCompletedLineState(
        long positionMs,
        int lineIndex,
        QrcLine line,
        string? previousLineText,
        string? nextLineText)
    {
        if (line.Words is null || line.Words.Count == 0)
        {
            return new CurrentLyricState(
                PositionMs: positionMs,
                LineIndex: lineIndex,
                LineText: line.Text,
                WordIndex: -1,
                CurrentWord: null,
                WordProgress: 1.0,
                PreviousLine: previousLineText,
                NextLine: nextLineText,
                WordCount: 0,
                CompletedPrefix: line.Text,
                RemainingSuffix: string.Empty,
                InInterlude: true,
                CurrentLineStartMs: line.StartMs,
                CurrentWordStartMs: null);
        }

        var last = line.Words.Count - 1;
        var lastWord = line.Words[last];
        return new CurrentLyricState(
            PositionMs: positionMs,
            LineIndex: lineIndex,
            LineText: line.Text,
            WordIndex: last,
            CurrentWord: lastWord.Text,
            WordProgress: 1.0,
            PreviousLine: previousLineText,
            NextLine: nextLineText,
            WordCount: line.Words.Count,
            CompletedPrefix: line.Text,
            RemainingSuffix: string.Empty,
            InInterlude: true,
            CurrentLineStartMs: line.StartMs,
            CurrentWordStartMs: lastWord.StartMs);
    }

    private static CurrentLyricState Empty(long positionMs) =>
        new(
            PositionMs: positionMs,
            LineIndex: -1,
            LineText: null,
            WordIndex: -1,
            CurrentWord: null,
            WordProgress: 0,
            PreviousLine: null,
            NextLine: null);

    private static double Clamp01(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v))
        {
            return 0;
        }

        if (v < 0) return 0;
        if (v > 1) return 1;
        return v;
    }
}
