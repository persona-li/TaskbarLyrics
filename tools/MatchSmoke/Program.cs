using TaskbarLyrics.QQMusic.Logging;
using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Services;

var log = new ConsoleLog();
var http = new QQMusicHttpClient(log);
var search = new QQMusicSearchService(http, log);
var aliases = new ArtistAliasStore(log, loadFromDisk: false);
var matcher = new SongMatcher(aliases);
var multi = new MultiPhaseSongSearch(search, matcher, aliases, log);

// Optional targeted live check: title, artist, duration seconds, album. No local user cache is used.
if (args.Length == 4)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var result = await multi.AutoMatchAsync(args[0], args[1], int.Parse(args[2]), args[3], timeout.Token);
    Console.WriteLine($"AutoBind={result.ShouldAutoBind}; Confidence={result.Confidence}; Reason={result.Reason}");
    if (!result.ShouldAutoBind || result.Best is null) return 1;
    Console.WriteLine($"Best={result.Best.SongMid}; Score={result.Best.MatchScore}; AlbumScore={result.Best.AlbumScore}");
    Console.WriteLine($"Title={result.Best.Title}; Artist={result.Best.Artists}; Album={result.Best.Album}");
    var lyrics = await new QQMusicLyricsService(http, log).GetLyricsAsync(result.Best, timeout.Token);
    var hasTimed = lyrics.ParsedQrcLines.Count > 0 || TaskbarLyrics.Core.Lyrics.LrcLineParser.Parse(lyrics.Lrc).Count > 0;
    Console.WriteLine($"QrcLines={lyrics.ParsedQrcLines.Count}; LrcPresent={!string.IsNullOrWhiteSpace(lyrics.Lrc)}; TimedLyrics={hasTimed}");
    return hasTimed ? 0 : 1;
}
Console.WriteLine("=== AutoMatch: You(=I) / 脸红的思春期 ===");
var d = await multi.AutoMatchAsync("You(=I)", "脸红的思春期", durationSeconds: null, album: null);
Console.WriteLine($"ShouldAutoBind={d.ShouldAutoBind} Conf={d.Confidence} Quality={d.ResultQuality}");
Console.WriteLine($"Reason={d.Reason}");
Console.WriteLine($"Raw={d.RawCount} Unique={d.UniqueCount}");
if (d.Best is not null)
    Console.WriteLine($"Best: {d.Best.Title} | {d.Best.Artists} | {d.Best.MatchScore} | mid={d.Best.SongMid} | ver={d.Best.VersionFlags}");
Console.WriteLine("--- Top 8 ---");
foreach (var (c, i) in d.RankedCandidates.Take(8).Select((c, i) => (c, i)))
    Console.WriteLine($"#{i+1} {c.MatchScore,5:0.0} {c.Confidence,-8} {c.VersionFlags,-20} {c.Title} | {c.Artists}");

Console.WriteLine("\n=== Manual: You(=I) BOL4 ===");
var (items, more, err) = await multi.ManualSearchAsync("You(=I) BOL4", 1, 20, "You(=I)", "脸红的思春期", null, null);
Console.WriteLine($"err={err} count={items.Count} hasMore={more}");
foreach (var (c, i) in items.Take(8).Select((c, i) => (c, i)))
    Console.WriteLine($"#{i+1} {c.MatchScore,5:0.0} {c.VersionFlags,-20} {c.Title} | {c.Artists} | {c.SongMid}");

var failures = new List<string>();
if (d.RawCount == 0 || d.Best is null)
    failures.Add("automatic search returned no usable candidate");
if (!string.IsNullOrWhiteSpace(err) || items.Count == 0)
    failures.Add($"manual search failed: {err ?? "no results"}");

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine("\nLIVE CONTRACT PASSED");
return 0;

sealed class ConsoleLog : IQqMusicLogger
{
    public void Info(string message) { /* quiet for smoke */ }
    public void Warn(string message) => Console.WriteLine("[W] " + message.Split('\n')[0]);
    public void Error(string message) => Console.WriteLine("[E] " + message);
    public void Error(string stage, Exception ex) => Console.WriteLine($"[E] {stage}: {ex.Message}");
    public void Http(string stage, HttpDiagnostics d) { }
}
