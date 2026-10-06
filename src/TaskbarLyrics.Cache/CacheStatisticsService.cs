namespace TaskbarLyrics.Cache;

public sealed class CacheStatistics
{
    public int LyricsCacheEntryCount { get; init; }
    public long LyricsCacheTotalBytes { get; init; }
    public int TrackLookupCount { get; init; }
    public int ManualMatchCount { get; init; }
    public int TrackSettingsCount { get; init; }
    public string CacheRoot { get; init; } = string.Empty;
}

public sealed class CacheStatisticsService
{
    private readonly TrackLookupCache _lookup;
    private readonly TrackSettingsStore _settings;

    public CacheStatisticsService(TrackLookupCache lookup, TrackSettingsStore settings)
    {
        _lookup = lookup;
        _settings = settings;
    }

    public Task<CacheStatistics> ComputeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = CachePaths.LyricsRoot;
            var count = 0;
            long bytes = 0;
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count++;
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            bytes += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }

            return new CacheStatistics
            {
                LyricsCacheEntryCount = count,
                LyricsCacheTotalBytes = bytes,
                TrackLookupCount = _lookup.Count,
                ManualMatchCount = _lookup.ManualCount,
                TrackSettingsCount = _settings.Count,
                CacheRoot = CachePaths.CacheRoot
            };
        }, cancellationToken);
    }

    public static void DeleteSongCacheDirectory(string cacheKey)
    {
        var dir = CachePaths.SongDirectory(cacheKey);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public static void ClearAllLyricsCacheStrict(string? lyricsRoot = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(lyricsRoot ?? CachePaths.LyricsRoot);
        if (!Directory.Exists(root)) return;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.GetFullPath(dir).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Cache directory escaped its root.");
            Directory.Delete(dir, recursive: true);
        }
    }

    public static void ClearAllLyricsCache()
    {
        if (!Directory.Exists(CachePaths.LyricsRoot))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(CachePaths.LyricsRoot))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // ignore single failures
            }
        }
    }
}
