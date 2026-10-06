using System.Text.RegularExpressions;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>
/// Minimal LRC parser for line-only fallback when QRC is unavailable.
/// Does not fabricate word timings.
/// </summary>
public static class LrcLineParser
{
    private static readonly Regex TimeTag = new(
        @"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]",
        RegexOptions.Compiled);

    public static List<QrcLine> Parse(string? lrc)
    {
        var result = new List<QrcLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return result;
        }

        var raw = lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var timed = new List<(int StartMs, string Text)>();

        foreach (var line in raw)
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var matches = TimeTag.Matches(text);
            if (matches.Count == 0)
            {
                continue;
            }

            var last = matches[^1];
            var body = text[(last.Index + last.Length)..].Trim();
            if (body.Length == 0)
            {
                continue;
            }

            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = int.Parse(m.Groups[2].Value);
                var frac = m.Groups[3].Success ? m.Groups[3].Value : "0";
                // Normalize fractional seconds to ms
                if (frac.Length == 1) frac += "00";
                else if (frac.Length == 2) frac += "0";
                else if (frac.Length > 3) frac = frac[..3];
                var ms = int.Parse(frac.PadRight(3, '0'));
                var startMs = (min * 60 + sec) * 1000 + ms;
                timed.Add((startMs, body));
            }
        }

        timed.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        for (var i = 0; i < timed.Count; i++)
        {
            var start = timed[i].StartMs;
            var end = i + 1 < timed.Count ? timed[i + 1].StartMs : start + 5000;
            var duration = Math.Max(0, end - start);
            result.Add(new QrcLine
            {
                StartMs = start,
                DurationMs = duration,
                Text = timed[i].Text,
                Words = new List<QrcWord>()
            });
        }

        return result;
    }
}
