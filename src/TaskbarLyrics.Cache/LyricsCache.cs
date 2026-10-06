using System.Text.Json;
using System.Text.Json.Serialization;
using TaskbarLyrics.Core.Lyrics;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Qrc;

namespace TaskbarLyrics.Cache;

/// <summary>
/// Local lyrics cache under %LOCALAPPDATA%\TaskbarLyrics\Cache\Lyrics\{qq-SongMid}\
/// Priority: Cache first, then network (caller).
/// </summary>
public sealed class LyricsCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly ICacheLogger _logger;

    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }
    public long CacheRepairs { get; private set; }
    public long CacheInvalids { get; private set; }

    public LyricsCache(ICacheLogger logger)
    {
        _logger = logger;
        CachePaths.EnsureDirectories();
    }

    public static async Task<string?> ReadOptionalTranslationAsync(string path, CancellationToken cancellationToken = default)
    {
        try { return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<LyricsCacheResult> TryLoadAsync(
        QQSongIdentity song,
        CancellationToken cancellationToken = default)
    {
        string cacheKey;
        try
        {
            cacheKey = song.CacheKey;
        }
        catch (Exception ex)
        {
            CacheMisses++;
            return new LyricsCacheResult
            {
                Status = LyricsCacheStatus.Miss,
                Reason = ex.Message
            };
        }

        var dir = CachePaths.SongDirectory(cacheKey);
        var metaPath = Path.Combine(dir, "metadata.json");
        var qrcPath = Path.Combine(dir, "lyrics.qrc");
        var lrcPath = Path.Combine(dir, "lyrics.lrc");
        var parsedPath = Path.Combine(dir, "parsed-qrc.json");

        _logger.Info(
            $"LYRICS CACHE LOOKUP{Environment.NewLine}" +
            $"SongMid: {song.SongMid}{Environment.NewLine}" +
            $"SongId: {song.SongId}{Environment.NewLine}" +
            $"Path: {dir}");

        if (!Directory.Exists(dir) || !File.Exists(metaPath))
        {
            CacheMisses++;
            _logger.Info("Result: MISS");
            return new LyricsCacheResult
            {
                Status = LyricsCacheStatus.Miss,
                CacheKey = cacheKey,
                DirectoryPath = dir,
                Reason = "Directory or metadata missing"
            };
        }

        LyricsCacheMetadata? meta;
        try
        {
            var json = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
            meta = JsonSerializer.Deserialize<LyricsCacheMetadata>(json, JsonOptions);
            if (meta is null)
            {
                throw new InvalidDataException("metadata.json deserialized to null");
            }
        }
        catch (Exception ex)
        {
            CacheInvalids++;
            _logger.Warn(
                $"CACHE CORRUPTED{Environment.NewLine}" +
                $"File: {metaPath}{Environment.NewLine}" +
                $"Reason: {ex.Message}{Environment.NewLine}" +
                $"Action: Treat as invalid → network");
            return new LyricsCacheResult
            {
                Status = LyricsCacheStatus.Corrupted,
                CacheKey = cacheKey,
                DirectoryPath = dir,
                Reason = "metadata.json corrupt: " + ex.Message
            };
        }

        if (meta.CacheVersion != CacheVersions.CurrentCacheVersion)
        {
            CacheInvalids++;
            _logger.Info($"Result: INVALID (cacheVersion {meta.CacheVersion} != {CacheVersions.CurrentCacheVersion})");
            return new LyricsCacheResult
            {
                Status = LyricsCacheStatus.Invalid,
                CacheKey = cacheKey,
                DirectoryPath = dir,
                Metadata = meta,
                Reason = "Unsupported cacheVersion"
            };
        }

        try
        {
            var repaired = false;
            List<QrcLine> lines;
            LyricsMode mode;

            if (meta.IsInstrumental)
            {
                CacheHits++;
                _logger.Info("Result: HIT (instrumental)");
                return new LyricsCacheResult
                {
                    Status = LyricsCacheStatus.Hit,
                    CacheKey = cacheKey,
                    DirectoryPath = dir,
                    Metadata = meta,
                    Document = new LyricsDocument
                    {
                        SongId = meta.SongId,
                        SongMid = meta.SongMid,
                        DisplayName = string.IsNullOrWhiteSpace(meta.Title)
                            ? null
                            : (meta.Artists.Count > 0
                                ? $"{meta.Title} - {string.Join(" / ", meta.Artists)}"
                                : meta.Title),
                        Mode = LyricsMode.None,
                        Source = LyricsSourceKind.Cache,
                        IsInstrumental = true,
                        Lines = Array.Empty<QrcLine>()
                    }
                };
            }

            if (meta.HasQrc && File.Exists(qrcPath))
            {
                mode = LyricsMode.Qrc;
                if (meta.ParserVersion == CacheVersions.CurrentParserVersion
                    && File.Exists(parsedPath))
                {
                    try
                    {
                        var parsedJson = await File.ReadAllTextAsync(parsedPath, cancellationToken)
                            .ConfigureAwait(false);
                        lines = JsonSerializer.Deserialize<List<QrcLine>>(parsedJson, JsonOptions)
                                ?? new List<QrcLine>();
                        if (lines.Count == 0)
                        {
                            throw new InvalidDataException("parsed-qrc.json empty");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(
                            $"CACHE CORRUPTED{Environment.NewLine}" +
                            $"File: {parsedPath}{Environment.NewLine}" +
                            $"Reason: {ex.Message}{Environment.NewLine}" +
                            $"Action: Re-parse lyrics.qrc");
                        lines = await ReparseAndRepairAsync(qrcPath, parsedPath, meta, metaPath, cancellationToken)
                            .ConfigureAwait(false);
                        repaired = true;
                        CacheRepairs++;
                    }
                }
                else
                {
                    // Parser upgrade or missing parsed: reparse qrc text, no network
                    lines = await ReparseAndRepairAsync(qrcPath, parsedPath, meta, metaPath, cancellationToken)
                        .ConfigureAwait(false);
                    repaired = meta.ParserVersion != CacheVersions.CurrentParserVersion;
                    if (repaired)
                    {
                        CacheRepairs++;
                    }
                }
            }
            else if (meta.HasLrc && File.Exists(lrcPath))
            {
                mode = LyricsMode.Lrc;
                var lrc = await File.ReadAllTextAsync(lrcPath, cancellationToken).ConfigureAwait(false);
                lines = LrcLineParser.Parse(lrc);
            }
            else
            {
                CacheInvalids++;
                _logger.Info("Result: INVALID (no usable lyrics files)");
                return new LyricsCacheResult
                {
                    Status = LyricsCacheStatus.Invalid,
                    CacheKey = cacheKey,
                    DirectoryPath = dir,
                    Metadata = meta,
                    Reason = "No lyrics.qrc / lyrics.lrc"
                };
            }

            var translation = meta.HasTranslation
                ? await ReadOptionalTranslationAsync(Path.Combine(dir, "translation.lrc"), cancellationToken).ConfigureAwait(false)
                : null;
            lines = lines.OrderBy(l => l.StartMs).ToList();
            CacheHits++;
            _logger.Info(
                $"Result: HIT{Environment.NewLine}" +
                $"Mode: {mode}{Environment.NewLine}" +
                $"Lines: {lines.Count}{Environment.NewLine}" +
                $"Words: {lines.Sum(l => l.Words.Count)}{Environment.NewLine}" +
                $"Repaired: {repaired}");

            return new LyricsCacheResult
            {
                Status = LyricsCacheStatus.Hit,
                CacheKey = cacheKey,
                DirectoryPath = dir,
                Metadata = meta,
                Repaired = repaired,
                Document = new LyricsDocument
                {
                    SongId = meta.SongId,
                    SongMid = meta.SongMid,
                    DisplayName = string.IsNullOrWhiteSpace(meta.Title)
                        ? null
                        : (meta.Artists.Count > 0
                            ? $"{meta.Title} - {string.Join(" / ", meta.Artists)}"
                            : meta.Title),
                    Mode = mode,
                    Translation = translation,
                    Source = LyricsSourceKind.Cache,
                    Lines = lines,
                    IsInstrumental = meta.IsInstrumental
                }
            };
        }
        catch (Exception ex)
        {
            CacheInvalids++;
            _logger.Error("TryLoadAsync", ex);
            return new LyricsCacheResult
            {
                Status = LyricsCacheStatus.Corrupted,
                CacheKey = cacheKey,
                DirectoryPath = dir,
                Metadata = meta,
                Reason = ex.Message
            };
        }
    }

    public async Task SaveAsync(
        QQSongIdentity song,
        QQMusicLyricsResult lyrics,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = song.CacheKey;
        var dir = CachePaths.SongDirectory(cacheKey);
        Directory.CreateDirectory(dir);

        var hasLrc = !string.IsNullOrWhiteSpace(lyrics.Lrc) && !lyrics.IsInstrumental;
        var hasQrc = !string.IsNullOrWhiteSpace(lyrics.Qrc);
        var hasTrans = !string.IsNullOrWhiteSpace(lyrics.Translation);
        var hasRoma = !string.IsNullOrWhiteSpace(lyrics.Romanization);

        if (hasLrc)
        {
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(dir, "lyrics.lrc"),
                lyrics.Lrc!,
                cancellationToken).ConfigureAwait(false);
        }

        if (hasQrc)
        {
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(dir, "lyrics.qrc"),
                lyrics.Qrc!,
                cancellationToken).ConfigureAwait(false);
        }

        if (hasTrans)
        {
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(dir, "translation.lrc"),
                lyrics.Translation!,
                cancellationToken).ConfigureAwait(false);
        }

        if (hasRoma)
        {
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(dir, "romanization.lrc"),
                lyrics.Romanization!,
                cancellationToken).ConfigureAwait(false);
        }

        if (lyrics.ParsedQrcLines.Count > 0)
        {
            var parsedJson = JsonSerializer.Serialize(lyrics.ParsedQrcLines, JsonOptions);
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(dir, "parsed-qrc.json"),
                parsedJson,
                cancellationToken).ConfigureAwait(false);
        }

        var artists = string.IsNullOrWhiteSpace(song.Artists)
            ? new List<string>()
            : song.Artists.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        var meta = new LyricsCacheMetadata
        {
            CacheVersion = CacheVersions.CurrentCacheVersion,
            Provider = "QQMusic",
            SongId = song.SongId,
            SongMid = song.SongMid,
            Title = song.Title,
            Artists = artists,
            Album = song.Album,
            DurationMs = song.DurationSeconds > 0 ? song.DurationSeconds * 1000L : 0,
            HasLrc = hasLrc,
            HasQrc = hasQrc,
            HasTranslation = hasTrans,
            HasRomanization = hasRoma,
            CachedAt = DateTimeOffset.UtcNow.ToString("o"),
            ParserVersion = CacheVersions.CurrentParserVersion,
            UserModified = false,
            IsInstrumental = lyrics.IsInstrumental
        };

        var metaJson = JsonSerializer.Serialize(meta, JsonOptions);
        await AtomicFile.WriteAllTextAsync(
            Path.Combine(dir, "metadata.json"),
            metaJson,
            cancellationToken).ConfigureAwait(false);

        _logger.Info(
            $"LYRICS CACHE WRITE{Environment.NewLine}" +
            $"SongMid: {song.SongMid}{Environment.NewLine}" +
            $"Path: {dir}{Environment.NewLine}" +
            $"QRC: {hasQrc}{Environment.NewLine}" +
            $"LRC: {hasLrc}{Environment.NewLine}" +
            $"Parsed lines: {lyrics.ParsedQrcLines.Count}{Environment.NewLine}" +
            $"Success: true");
    }

    private async Task<List<QrcLine>> ReparseAndRepairAsync(
        string qrcPath,
        string parsedPath,
        LyricsCacheMetadata meta,
        string metaPath,
        CancellationToken cancellationToken)
    {
        var qrcText = await File.ReadAllTextAsync(qrcPath, cancellationToken).ConfigureAwait(false);
        var parsed = QrcParser.Parse(qrcText);
        var lines = parsed.Lines;
        if (lines.Count == 0)
        {
            throw new InvalidDataException("Re-parse produced zero lines");
        }

        var json = JsonSerializer.Serialize(lines, JsonOptions);
        await AtomicFile.WriteAllTextAsync(parsedPath, json, cancellationToken).ConfigureAwait(false);

        meta.ParserVersion = CacheVersions.CurrentParserVersion;
        meta.HasQrc = true;
        var metaJson = JsonSerializer.Serialize(meta, JsonOptions);
        await AtomicFile.WriteAllTextAsync(metaPath, metaJson, cancellationToken).ConfigureAwait(false);

        _logger.Info($"CACHE REPAIR: reparsed lyrics.qrc → parsed-qrc.json lines={lines.Count}");
        return lines;
    }
}
