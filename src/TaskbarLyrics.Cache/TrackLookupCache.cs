using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarLyrics.Cache;

public enum MatchSourceKind
{
    Automatic,
    Manual
}

public sealed class TrackLookupEntry
{
    public string TitleNormalized { get; set; } = string.Empty;
    public string ArtistNormalized { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
    public string SongMid { get; set; } = string.Empty;
    public string SongId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artists { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public int MatchedDurationSeconds { get; set; }
    public string MatchSource { get; set; } = nameof(MatchSourceKind.Automatic);
    public string LastUsedAt { get; set; } = string.Empty;
    public double? MatchScore { get; set; }
}

public sealed class TrackIndexFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, TrackLookupEntry> Tracks { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Maps GSMTC track identity → QQ SongMid. Manual bindings permanently preferred.
/// File: %LOCALAPPDATA%\TaskbarLyrics\Cache\track-index.json
/// </summary>
public sealed class TrackLookupCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ICacheLogger _logger;
    private readonly object _lock = new();
    private readonly string _filePath;
    private TrackIndexFile _file = new();

    public TrackLookupCache(ICacheLogger logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = Path.GetFullPath(filePath ?? CachePaths.TrackIndexPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        Load();
    }

    public int Count
    {
        get { lock (_lock) return _file.Tracks.Count; }
    }

    public int ManualCount
    {
        get
        {
            lock (_lock)
            {
                return _file.Tracks.Values.Count(t =>
                    string.Equals(t.MatchSource, nameof(MatchSourceKind.Manual), StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public static string BuildKey(string title, string artist, int? durationSeconds)
    {
        var t = Normalize(title);
        var a = Normalize(artist);
        var d = durationSeconds.HasValue && durationSeconds.Value > 0
            ? durationSeconds.Value.ToString()
            : "na";
        return $"{t}|{a}|{d}";
    }

    public TrackLookupEntry? TryGet(string title, string artist, int? durationSeconds)
    {
        var key = BuildKey(title, artist, durationSeconds);
        lock (_lock)
        {
            if (_file.Tracks.TryGetValue(key, out var e))
            {
                e.LastUsedAt = DateTimeOffset.UtcNow.ToString("o");
                return Clone(e);
            }

            // Soft match: same title+artist ignoring duration if exact miss
            var soft = _file.Tracks.Values.FirstOrDefault(x =>
                string.Equals(x.TitleNormalized, Normalize(title), StringComparison.Ordinal)
                && string.Equals(x.ArtistNormalized, Normalize(artist), StringComparison.Ordinal)
                && string.Equals(x.MatchSource, nameof(MatchSourceKind.Manual), StringComparison.OrdinalIgnoreCase));
            return soft is null ? null : Clone(soft);
        }
    }

    public void SaveAutomatic(
        string title,
        string artist,
        int? durationSeconds,
        QQSongIdentity song,
        double? matchScore)
    {
        var key = BuildKey(title, artist, durationSeconds);
        lock (_lock)
        {
            if (_file.Tracks.TryGetValue(key, out var existing)
                && string.Equals(existing.MatchSource, nameof(MatchSourceKind.Manual), StringComparison.OrdinalIgnoreCase))
            {
                // Never overwrite Manual with Automatic
                existing.LastUsedAt = DateTimeOffset.UtcNow.ToString("o");
                PersistUnlocked();
                return;
            }

            _file.Tracks[key] = new TrackLookupEntry
            {
                TitleNormalized = Normalize(title),
                ArtistNormalized = Normalize(artist),
                DurationSeconds = durationSeconds,
                SongMid = song.SongMid,
                SongId = song.SongId,
                Title = song.Title,
                Artists = song.Artists,
                Album = song.Album,
                MatchedDurationSeconds = song.DurationSeconds,
                MatchSource = nameof(MatchSourceKind.Automatic),
                LastUsedAt = DateTimeOffset.UtcNow.ToString("o"),
                MatchScore = matchScore
            };
            PersistUnlocked();
        }

        _logInfo($"TrackLookup SAVE Automatic key={key} mid={song.SongMid}");
    }

    public void SaveManual(
        string title,
        string artist,
        int? durationSeconds,
        QQSongIdentity song, bool requirePersistence = false)
    {
        var key = BuildKey(title, artist, durationSeconds);
        lock (_lock)
        {
            var before = new Dictionary<string, TrackLookupEntry>(_file.Tracks, StringComparer.Ordinal);
            _file.Tracks[key] = new TrackLookupEntry
            {
                TitleNormalized = Normalize(title),
                ArtistNormalized = Normalize(artist),
                DurationSeconds = durationSeconds,
                SongMid = song.SongMid,
                SongId = song.SongId,
                Title = song.Title,
                Artists = song.Artists,
                Album = song.Album,
                MatchedDurationSeconds = song.DurationSeconds,
                MatchSource = nameof(MatchSourceKind.Manual),
                LastUsedAt = DateTimeOffset.UtcNow.ToString("o")
            };
            try { PersistUnlocked(requirePersistence); }
            catch { _file.Tracks = before; throw; }
        }

        _logInfo($"TrackLookup SAVE Manual key={key} mid={song.SongMid}");
    }

    public bool ClearManual(string title, string artist, int? durationSeconds, bool requirePersistence = false)
    {
        var key = BuildKey(title, artist, durationSeconds);
        lock (_lock)
        {
            var before = new Dictionary<string, TrackLookupEntry>(_file.Tracks, StringComparer.Ordinal);
            if (!_file.Tracks.TryGetValue(key, out var e))
            {
                return false;
            }

            if (!string.Equals(e.MatchSource, nameof(MatchSourceKind.Manual), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _file.Tracks.Remove(key);
            try { PersistUnlocked(requirePersistence); }
            catch { _file.Tracks = before; throw; }
        }

        _logInfo($"TrackLookup CLEAR Manual key={key}");
        return true;
    }

    public int ClearAllManual(bool requirePersistence = false)
    {
        int n;
        lock (_lock)
        {
            var before = new Dictionary<string, TrackLookupEntry>(_file.Tracks, StringComparer.Ordinal);
            var keys = _file.Tracks
                .Where(kv => string.Equals(kv.Value.MatchSource, nameof(MatchSourceKind.Manual), StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in keys)
            {
                _file.Tracks.Remove(k);
            }

            n = keys.Count;
            if (n > 0)
            {
                try { PersistUnlocked(requirePersistence); }
            catch { _file.Tracks = before; throw; }
            }
        }

        _logInfo($"TrackLookup CLEAR ALL Manual count={n}");
        return n;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<TrackIndexFile>(json, JsonOptions);
            if (file is not null)
            {
                _file = file;
                _file.Tracks ??= new Dictionary<string, TrackLookupEntry>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("TrackLookupCache Load", ex);
            _file = new TrackIndexFile();
        }
    }

    private void PersistUnlocked(bool requirePersistence = false)
    {
        try
        {
            var json = JsonSerializer.Serialize(_file, JsonOptions);
            AtomicFile.WriteAllTextAsync(_filePath, json, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error("TrackLookupCache Persist", ex);
            if (requirePersistence) throw;
        }
    }

    private void _logInfo(string m) => _logger.Info(m);

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        return string.Join(' ', s.Trim().ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static TrackLookupEntry Clone(TrackLookupEntry e) => new()
    {
        TitleNormalized = e.TitleNormalized,
        ArtistNormalized = e.ArtistNormalized,
        DurationSeconds = e.DurationSeconds,
        SongMid = e.SongMid,
        SongId = e.SongId,
        Title = e.Title,
        Artists = e.Artists,
        Album = e.Album,
        MatchedDurationSeconds = e.MatchedDurationSeconds,
        MatchSource = e.MatchSource,
        LastUsedAt = e.LastUsedAt,
        MatchScore = e.MatchScore
    };
}
