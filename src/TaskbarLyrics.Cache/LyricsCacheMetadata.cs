namespace TaskbarLyrics.Cache;

public sealed class LyricsCacheMetadata
{
    public int CacheVersion { get; set; } = CacheVersions.CurrentCacheVersion;
    public string Provider { get; set; } = "QQMusic";
    public string SongId { get; set; } = string.Empty;
    public string SongMid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Artists { get; set; } = new();
    public string Album { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public bool HasLrc { get; set; }
    public bool HasQrc { get; set; }
    public bool HasTranslation { get; set; }
    public bool HasRomanization { get; set; }
    public string CachedAt { get; set; } = string.Empty;
    public int ParserVersion { get; set; } = CacheVersions.CurrentParserVersion;
    public bool UserModified { get; set; }
    public bool IsInstrumental { get; set; }
}
