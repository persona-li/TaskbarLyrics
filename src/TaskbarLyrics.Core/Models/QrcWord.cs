namespace TaskbarLyrics.Core.Models;

public sealed class QrcWord
{
    public string Text { get; set; } = string.Empty;
    public int StartMs { get; set; }
    public int DurationMs { get; set; }
    public int EndMs => StartMs + DurationMs;
}
