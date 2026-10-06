using System.Text.Json;
using TaskbarLyrics.QQMusic.Logging;
using TaskbarLyrics.QQMusic.Models;

namespace TaskbarLyrics.QQMusic.Services;

/// <summary>
/// QQ Music song search via client_search_cp.
/// Supports paging, keyword search, and concurrency limiting.
/// </summary>
public sealed class QQMusicSearchService
{
    public const int DefaultAutoPageSize = 20;
    public const int DefaultManualPageSize = 20;
    public const int MaxAutoCandidates = 60;
    public const int MaxManualResults = 100;
    public const int MaxAutoPagesPerQuery = 2;

    private readonly QQMusicHttpClient _http;
    private readonly IQqMusicLogger _logger;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public QQMusicSearchService(QQMusicHttpClient http, IQqMusicLogger logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Legacy entry: single page title+artist, n=20.</summary>
    public Task<SearchServiceResult> SearchAsync(
        string title,
        string? artist,
        int? durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var keyword = string.IsNullOrWhiteSpace(artist)
            ? title.Trim()
            : $"{title.Trim()} {artist.Trim()}";
        return SearchKeywordAsync(keyword, page: 1, pageSize: DefaultAutoPageSize, cancellationToken);
    }

    public async Task<SearchServiceResult> SearchKeywordAsync(
        string keyword,
        int page = 1,
        int pageSize = DefaultAutoPageSize,
        CancellationToken cancellationToken = default)
    {
        keyword = (keyword ?? string.Empty).Trim();
        var result = new SearchServiceResult { Keyword = keyword, Page = page, PageSize = pageSize };
        if (keyword.Length == 0)
        {
            result.Error = "Empty search keyword";
            return result;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.Info($"Search keyword=\"{keyword}\" page={page} n={pageSize}");

            var query = QQMusicEndpoints.BuildSearchParams(keyword, page, pageSize);
            var http = await _http.GetAsync("search", QQMusicEndpoints.SearchUrl, query, cancellationToken)
                .ConfigureAwait(false);
            result.Http = http;
            result.RawJson = http.Body;

            if (!http.Success)
            {
                result.Error = $"Search HTTP failed: {http.StatusCode} {http.ReasonPhrase}";
                _logger.Error(result.Error);
                if (http.StatusCode == 403)
                {
                    result.Error += " — 当前匿名请求方式可能被 QQ 音乐拒绝。";
                }
                else if (http.StatusCode == 429)
                {
                    result.Error += " — 可能触发 QQ 音乐请求频率限制。";
                }

                return result;
            }

            if (string.IsNullOrWhiteSpace(http.Body))
            {
                result.Error = "Search response body is empty.";
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(http.Body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("code", out var codeEl))
                {
                    result.Error = "Search JSON missing 'code'. Structure may have changed.";
                    _logger.Error(result.Error + " Preview: " + http.BodyPreview());
                    return result;
                }

                var code = codeEl.GetInt32();
                result.ApiCode = code;
                if (code != 0)
                {
                    result.Error = $"Search API code={code}";
                    _logger.Error(result.Error + " Preview: " + http.BodyPreview());
                    return result;
                }

                if (!root.TryGetProperty("data", out var data)
                    || !data.TryGetProperty("song", out var song)
                    || !song.TryGetProperty("list", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    result.Error = "Search JSON missing data.song.list. Structure may have changed.";
                    _logger.Error(result.Error + " Preview: " + http.BodyPreview());
                    return result;
                }

                var rank = 0;
                foreach (var item in list.EnumerateArray())
                {
                    rank++;
                    if (rank > pageSize)
                    {
                        break;
                    }

                    var candidate = ParseCandidate(item, rank);
                    if (candidate is not null)
                    {
                        result.Candidates.Add(candidate);
                    }
                }

                result.Success = true;
                result.HasMore = list.GetArrayLength() >= pageSize;
                _logger.Info($"Search candidates: {result.Candidates.Count} (page={page})");
            }
            catch (JsonException ex)
            {
                result.Error = $"Search JSON parse failed: {ex.Message}";
                _logger.Error("search-json", ex);
                _logger.Error("Preview: " + http.BodyPreview());
            }
            catch (Exception ex)
            {
                result.Error = $"Search parse failed: {ex.Message}";
                _logger.Error("search", ex);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fetch up to maxPages for one keyword, capped by maxTotal.</summary>
    public async Task<SearchServiceResult> SearchKeywordPagesAsync(
        string keyword,
        int pageSize,
        int maxPages,
        int maxTotal,
        CancellationToken cancellationToken = default)
    {
        var merged = new SearchServiceResult { Keyword = keyword, Page = 1, PageSize = pageSize };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= maxPages && merged.Candidates.Count < maxTotal; page++)
        {
            var pageResult = await SearchKeywordAsync(keyword, page, pageSize, cancellationToken)
                .ConfigureAwait(false);
            merged.Http = pageResult.Http;
            merged.ApiCode = pageResult.ApiCode;
            if (!pageResult.Success)
            {
                if (merged.Candidates.Count == 0)
                {
                    merged.Error = pageResult.Error;
                    return merged;
                }

                break;
            }

            foreach (var c in pageResult.Candidates)
            {
                if (merged.Candidates.Count >= maxTotal)
                {
                    break;
                }

                if (!seen.Add(c.DedupKey))
                {
                    continue;
                }

                merged.Candidates.Add(c);
            }

            if (!pageResult.HasMore || pageResult.Candidates.Count == 0)
            {
                merged.HasMore = false;
                break;
            }

            merged.HasMore = true;
        }

        merged.Success = merged.Candidates.Count > 0 || string.IsNullOrEmpty(merged.Error);
        return merged;
    }

    private static QQSongCandidate? ParseCandidate(JsonElement item, int rank)
    {
        try
        {
            var songId = GetStringish(item, "songid");
            var songMid = GetString(item, "songmid") ?? string.Empty;
            var title = GetString(item, "songname") ?? string.Empty;
            var album = GetString(item, "albumname") ?? string.Empty;
            var albumMid = GetString(item, "albummid") ?? string.Empty;
            var interval = 0;
            if (item.TryGetProperty("interval", out var iv))
            {
                if (iv.ValueKind == JsonValueKind.Number)
                {
                    interval = iv.GetInt32();
                }
                else if (iv.ValueKind == JsonValueKind.String && int.TryParse(iv.GetString(), out var parsed))
                {
                    interval = parsed;
                }
            }

            var artists = new List<string>();
            if (item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in singers.EnumerateArray())
                {
                    var name = GetString(s, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        artists.Add(name!);
                    }
                }
            }

            return new QQSongCandidate
            {
                SongId = songId,
                SongMid = songMid,
                Title = title,
                Artists = string.Join(" / ", artists),
                Album = album,
                AlbumMid = albumMid,
                DurationSeconds = interval,
                Rank = rank
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => p.ToString()
        };
    }

    private static string GetStringish(JsonElement el, string name) =>
        GetString(el, name) ?? string.Empty;
}
