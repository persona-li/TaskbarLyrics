namespace TaskbarLyrics.Cache;

public static class CachePaths
{
    public static string AppRoot => ResolveAppRoot(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    // Portable mode must be explicit; never fall back silently if Data is read-only.
    public static string ResolveAppRoot(string applicationDirectory, string localAppData) =>
        File.Exists(Path.Combine(applicationDirectory, "portable.flag"))
            ? Path.Combine(applicationDirectory, "Data")
            : Path.Combine(localAppData, "TaskbarLyrics");

    public static string CacheRoot => Path.Combine(AppRoot, "Cache");
    public static string LyricsRoot => Path.Combine(CacheRoot, "Lyrics");
    public static string LogsRoot => Path.Combine(AppRoot, "Logs");
    public static string TrackIndexPath => Path.Combine(CacheRoot, "track-index.json");
    public static string TrackSettingsPath => Path.Combine(CacheRoot, "track-settings.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(LyricsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(CacheRoot);
    }

    public static string SongDirectory(string cacheKey) =>
        Path.Combine(LyricsRoot, cacheKey);
}
