// QRC line/word parser based on the format used by QQ Music and
// WXRIW/Lyricify-Lyrics-Helper QrcParser.
//
// Typical line:
//   [18210,3410]故(18210,270)事(18480,280)的(18760,260)小(19020,320)黄(19340,380)花(19720,380)
//
// Metadata lines such as [ti:...] are skipped for timing parse.

using System.Text.RegularExpressions;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.QQMusic.Qrc;

public sealed class QrcParseResult
{
    public bool Success { get; set; }
    public bool Unsupported { get; set; }
    public string? Error { get; set; }
    public List<QrcLine> Lines { get; set; } = new();
    public int WordCount => Lines.Sum(l => l.Words.Count);
    public int TimedLineCount => Lines.Count(l => l.StartMs >= 0 && (l.DurationMs > 0 || l.Words.Count > 0));
}

public static class QrcParser
{
    private static readonly Regex LineHeaderRegex = new(
        @"^\[(\d+)\s*,\s*(\d+)\](.*)$",
        RegexOptions.Compiled);

    private static readonly Regex WordRegex = new(
        @"(.*?)\((\d+)\s*,\s*(\d+)\)",
        RegexOptions.Compiled);

    private static readonly Regex MetaTagRegex = new(
        @"^\[(ti|ar|al|by|offset|total|duration):",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static QrcParseResult Parse(string? qrcText)
    {
        var result = new QrcParseResult();

        if (string.IsNullOrWhiteSpace(qrcText))
        {
            result.Error = "QRC text is empty.";
            result.Unsupported = true;
            return result;
        }

        // Nested XML may still be present; extract LyricContent first.
        var text = QrcXmlParser.ExtractLyricContentIfXml(qrcText);

        var rawLines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var parsedAnyTiming = false;

        foreach (var raw in rawLines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (MetaTagRegex.IsMatch(line))
            {
                continue;
            }

            var parsed = ParseLyricsLine(line);
            if (parsed is null)
            {
                continue;
            }

            result.Lines.Add(parsed);
            if (parsed.Words.Count > 0 || parsed.DurationMs > 0)
            {
                parsedAnyTiming = true;
            }
        }

        if (result.Lines.Count == 0)
        {
            // Maybe the whole text is one long line without newlines.
            if (text.Contains('(') && text.Contains('[') && !text.Contains('\n'))
            {
                var single = ParseLyricsLine(text.Trim());
                if (single is not null && single.Words.Count > 0)
                {
                    result.Lines.Add(single);
                    parsedAnyTiming = true;
                }
            }
        }

        if (result.Lines.Count == 0)
        {
            result.Unsupported = true;
            result.Error = "Parser Unsupported: no timed QRC lines recognized. Raw decrypted text was preserved.";
            return result;
        }

        result.Success = parsedAnyTiming || result.Lines.Count > 0;
        if (!parsedAnyTiming)
        {
            result.Unsupported = true;
            result.Error = "Parser Unsupported: lines found but no word/line timing extracted.";
        }

        return result;
    }

    public static QrcLine? ParseLyricsLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        int lineStart = 0;
        int lineDuration = 0;
        string body = line;

        var header = LineHeaderRegex.Match(line);
        if (header.Success)
        {
            lineStart = int.Parse(header.Groups[1].Value);
            lineDuration = int.Parse(header.Groups[2].Value);
            body = header.Groups[3].Value;
        }
        else
        {
            // Lyricify style: strip first [...] if present, even without pure timing header.
            var bracket = line.IndexOf(']');
            if (bracket >= 0 && line.StartsWith('['))
            {
                body = line[(bracket + 1)..];
            }
        }

        var words = new List<QrcWord>();
        foreach (Match match in WordRegex.Matches(body))
        {
            if (match.Groups.Count != 4)
            {
                continue;
            }

            var text = match.Groups[1].Value;
            var start = int.Parse(match.Groups[2].Value);
            var duration = int.Parse(match.Groups[3].Value);

            words.Add(new QrcWord
            {
                Text = text,
                StartMs = start,
                DurationMs = duration
            });
        }

        if (words.Count == 0 && header.Success)
        {
            // Timed line without word-level tags: treat remaining body as whole line text.
            var textOnly = body.Trim();
            if (textOnly.Length == 0)
            {
                return null;
            }

            return new QrcLine
            {
                StartMs = lineStart,
                DurationMs = lineDuration,
                Text = textOnly,
                Words = new List<QrcWord>()
            };
        }

        if (words.Count == 0)
        {
            return null;
        }

        var lineText = string.Concat(words.Select(w => w.Text));
        if (lineDuration <= 0 && words.Count > 0)
        {
            var last = words[^1];
            lineDuration = Math.Max(0, last.EndMs - (words[0].StartMs));
        }

        if (lineStart <= 0 && words.Count > 0)
        {
            lineStart = words[0].StartMs;
        }

        return new QrcLine
        {
            StartMs = lineStart,
            DurationMs = lineDuration,
            Text = lineText,
            Words = words
        };
    }
}
