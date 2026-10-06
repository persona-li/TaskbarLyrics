using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Lyrics;
namespace TaskbarLyrics.App.Presentation;
public sealed record LyricReaderRow(int Index, string Text, string? Translation);
public static class LyricReaderRows
{
    public static IReadOnlyList<LyricReaderRow> Build(LyricsDocument document)
    {
        var translations = LrcLineParser.Parse(document.Translation);
        return document.Lines.Select((line, index) =>
        {
            var text = line.Words.Count > 0 ? string.Concat(line.Words.Select(w => w.Text)) : line.Text;
            var translated = translations.Where(t => Math.Abs((long)t.StartMs - line.StartMs) <= 120)
                .OrderBy(t => Math.Abs((long)t.StartMs - line.StartMs)).FirstOrDefault()?.Text;
            return new LyricReaderRow(index, text, translated == text ? null : translated);
        }).Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToArray();
    }
}
