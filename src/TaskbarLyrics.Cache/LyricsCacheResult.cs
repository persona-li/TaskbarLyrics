using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Cache;

public enum LyricsCacheStatus
{
    Hit,
    Miss,
    Invalid,
    Corrupted
}

public sealed class LyricsCacheResult
{
    public required LyricsCacheStatus Status { get; init; }
    public string? CacheKey { get; init; }
    public string? DirectoryPath { get; init; }
    public string? Reason { get; init; }
    public LyricsDocument? Document { get; init; }
    public LyricsCacheMetadata? Metadata { get; init; }
    public bool Repaired { get; init; }
}
