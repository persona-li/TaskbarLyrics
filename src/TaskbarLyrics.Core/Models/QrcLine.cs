namespace TaskbarLyrics.Core.Models;

public sealed class QrcLine
{
    public int StartMs { get; set; }
    public int DurationMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<QrcWord> Words { get; set; } = new();
    public int EndMs => StartMs + DurationMs;
}
