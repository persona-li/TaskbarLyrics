using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarLyrics.Cache;

public sealed class TrackSettingEntry
{
    public long LyricsOffsetMs { get; set; }
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class TrackSettingsFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, TrackSettingEntry> Tracks { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Per-track settings (offset). File: track-settings.json
/// </summary>
public sealed class TrackSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ICacheLogger _logger;
    private readonly object _lock = new();
    private readonly string _filePath;
    private TrackSettingsFile _file = new();

    public TrackSettingsStore(ICacheLogger logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = Path.GetFullPath(filePath ?? CachePaths.TrackSettingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        Load();
    }

    public int Count
    {
        get { lock (_lock) return _file.Tracks.Count; }
    }

    public long GetTrackOffsetMs(string? cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return 0;
        }

        lock (_lock)
        {
            return _file.Tracks.TryGetValue(cacheKey, out var e) ? e.LyricsOffsetMs : 0;
        }
    }

    public void SetTrackOffsetMs(string cacheKey, long offsetMs, bool requirePersistence = false)
    {
        offsetMs = Math.Clamp(offsetMs, -5000, 5000);
        lock (_lock)
        {
            var before = new Dictionary<string, TrackSettingEntry>(_file.Tracks, StringComparer.Ordinal);
            if (offsetMs == 0)
            {
                _file.Tracks.Remove(cacheKey);
            }
            else
            {
                _file.Tracks[cacheKey] = new TrackSettingEntry
                {
                    LyricsOffsetMs = offsetMs,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
                };
            }

            try { PersistUnlocked(requirePersistence); }
            catch { _file.Tracks = before; throw; }
        }

        _logger.Info($"TrackSettings SET {cacheKey} offset={offsetMs}");
    }

    public void ResetTrackOffset(string cacheKey) => SetTrackOffsetMs(cacheKey, 0);

    public int ClearAllOffsets(bool requirePersistence = false)
    {
        int n;
        lock (_lock)
        {
            var before = new Dictionary<string, TrackSettingEntry>(_file.Tracks, StringComparer.Ordinal);
            n = _file.Tracks.Count;
            _file.Tracks.Clear();
            try { PersistUnlocked(requirePersistence); }
            catch { _file.Tracks = before; throw; }
        }

        _logger.Info($"TrackSettings CLEAR ALL count={n}");
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
            var file = JsonSerializer.Deserialize<TrackSettingsFile>(json, JsonOptions);
            if (file is not null)
            {
                _file = file;
                _file.Tracks ??= new Dictionary<string, TrackSettingEntry>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("TrackSettingsStore Load", ex);
            _file = new TrackSettingsFile();
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
            _logger.Error("TrackSettingsStore Persist", ex);
            if (requirePersistence) throw;
        }
    }
}
