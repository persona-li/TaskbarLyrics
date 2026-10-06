namespace TaskbarLyrics.Core.Models;

public sealed record CurrentLyricState(
    long PositionMs,
    int LineIndex,
    string? LineText,
    int WordIndex,
    string? CurrentWord,
    double WordProgress,
    string? PreviousLine,
    string? NextLine,
    int WordCount = 0,
    string? CompletedPrefix = null,
    string? RemainingSuffix = null,
    bool InInterlude = false,
    long? CurrentLineStartMs = null,
    long? CurrentWordStartMs = null);
