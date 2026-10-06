namespace TaskbarLyrics.QQMusic.Services;

/// <summary>
/// Central place for QQ Music anonymous Web API endpoints and parameter sets.
/// Primary path follows Widdit/now-playing-service (2026).
/// Do not silently switch endpoints — any fallback must be explicit.
/// </summary>
public static class QQMusicEndpoints
{
    public const string SearchUrl = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";
    public const string LrcUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";
    public const string QrcUrl = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg";

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    public const string Accept = "*/*";
    public const string CacheControl = "no-cache";
    public const string Referer = "https://c.y.qq.com/";

    public static Dictionary<string, string> BuildSearchParams(string keyword, int page = 1, int pageSize = 8)
    {
        return new Dictionary<string, string>
        {
            ["ct"] = "24",
            ["qqmusic_ver"] = "1298",
            ["remoteplace"] = "txt.yqq.center",
            ["t"] = "0",
            ["aggr"] = "1",
            ["cr"] = "1",
            ["catZhida"] = "1",
            ["lossless"] = "0",
            ["flag_qc"] = "0",
            ["p"] = page.ToString(),
            ["n"] = pageSize.ToString(),
            ["w"] = keyword,
            ["g_tk"] = "5381",
            ["loginUin"] = "0",
            ["hostUin"] = "0",
            ["format"] = "json",
            ["inCharset"] = "utf8",
            ["outCharset"] = "utf-8",
            ["notice"] = "0",
            ["platform"] = "yqq",
            ["needNewCode"] = "0",
        };
    }

    public static Dictionary<string, string> BuildLrcParams(string songId)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Dictionary<string, string>
        {
            ["musicid"] = songId,
            ["callback"] = "MusicJsonCallback_lrc",
            ["pcachetime"] = ms.ToString(),
            ["g_tk"] = "5381",
            ["jsonpCallback"] = "MusicJsonCallback_lrc",
            ["loginUin"] = "0",
            ["hostUin"] = "0",
            ["format"] = "json",
            ["inCharset"] = "utf8",
            ["outCharset"] = "utf8",
            ["notice"] = "0",
            ["platform"] = "yqq",
            ["needNewCode"] = "0",
            ["nobase64"] = "1",
        };
    }

    public static Dictionary<string, string> BuildQrcParams(string songId)
    {
        return new Dictionary<string, string>
        {
            ["musicid"] = songId,
            ["version"] = "15",
            ["miniversion"] = "82",
            ["lrctype"] = "4",
        };
    }
}
