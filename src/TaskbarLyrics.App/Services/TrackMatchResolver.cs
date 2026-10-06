using TaskbarLyrics.Cache;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Services;

namespace TaskbarLyrics.App.Services;

public interface ITrackLookupStore
{
    TrackLookupEntry? TryGet(string title, string artist, int? durationSeconds);

    void SaveAutomatic(
        string title,
        string artist,
        int? durationSeconds,
        QQSongIdentity song,
        double? matchScore);
}

public sealed class TrackLookupStoreAdapter : ITrackLookupStore
{
    private readonly TrackLookupCache _cache;

    public TrackLookupStoreAdapter(TrackLookupCache cache) => _cache = cache;

    public TrackLookupEntry? TryGet(string title, string artist, int? durationSeconds) =>
        _cache.TryGet(title, artist, durationSeconds);

    public void SaveAutomatic(
        string title,
        string artist,
        int? durationSeconds,
        QQSongIdentity song,
        double? matchScore) =>
        _cache.SaveAutomatic(title, artist, durationSeconds, song, matchScore);
}

public enum TrackMatchResolutionKind
{
    Bound,
    ManualRequired
}

public sealed record TrackMatchResolution(
    TrackMatchResolutionKind Kind,
    QQSongIdentity? Song,
    string MatchSource,
    double? MatchScore,
    string Reason,
    int CandidateCount,
    bool FromCache)
{
    public bool CanRetryAfterMissingLyrics =>
        FromCache
        && string.Equals(
            MatchSource,
            nameof(MatchSourceKind.Automatic),
            StringComparison.OrdinalIgnoreCase);

    public static TrackMatchResolution Bound(
        QQSongIdentity song,
        string matchSource,
        double? matchScore = null,
        bool fromCache = false) =>
        new(TrackMatchResolutionKind.Bound, song, matchSource, matchScore, string.Empty, 0, fromCache);

    public static TrackMatchResolution ManualRequired(string reason, int candidateCount) =>
        new(TrackMatchResolutionKind.ManualRequired, null, "None", null, reason, candidateCount, false);
}

/// <summary>
/// Deterministic binding policy around the external automatic search operation.
/// Manual bindings always win; automatic bindings must still agree with the requested version.
/// Only a high-confidence search result is persisted automatically.
/// </summary>
public sealed class TrackMatchResolver
{
    private readonly ITrackLookupStore _lookup;
    private readonly IAutomaticSongSearch _search;
    private static readonly SongMatcher CachedMetadataMatcher = new(new ArtistAliasStore(loadFromDisk: false));

    public TrackMatchResolver(ITrackLookupStore lookup, IAutomaticSongSearch search)
    {
        _lookup = lookup;
        _search = search;
    }

    public async Task<TrackMatchResolution> ResolveAsync(
        TrackIdentity track,
        CancellationToken cancellationToken,
        bool retryAutomaticBinding = false,
        QQSongIdentity? confirmedSelection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (confirmedSelection is not null)
            return TrackMatchResolution.Bound(confirmedSelection, nameof(MatchSourceKind.Manual));

        var cached = _lookup.TryGet(track.Title, track.Artist, track.DurationSeconds);
        if (cached is not null
            && (!string.IsNullOrWhiteSpace(cached.SongMid) || !string.IsNullOrWhiteSpace(cached.SongId))
            && (!retryAutomaticBinding
                || !string.Equals(
                    cached.MatchSource,
                    nameof(MatchSourceKind.Automatic),
                    StringComparison.OrdinalIgnoreCase))
            && CanReuseCachedVersion(cached, track))
        {
            return TrackMatchResolution.Bound(
                ToIdentity(cached, track),
                cached.MatchSource,
                cached.MatchScore,
                fromCache: true);
        }

        var decision = await _search.AutoMatchAsync(
            track.Title,
            track.Artist,
            track.DurationSeconds,
            track.Album,
            cancellationToken).ConfigureAwait(false);

        if (!decision.ShouldAutoBind || decision.Best is null)
        {
            var reason = decision.UniqueCount == 0
                ? (decision.Reason.Length > 0 ? decision.Reason : "No match")
                : "Match ambiguous — open rematch to choose";
            return TrackMatchResolution.ManualRequired(reason, decision.UniqueCount);
        }

        var best = decision.Best;
        var identity = new QQSongIdentity(
            best.SongId,
            best.SongMid,
            best.Title,
            best.Artists,
            best.Album,
            best.DurationSeconds);
        _lookup.SaveAutomatic(
            track.Title,
            track.Artist,
            track.DurationSeconds,
            identity,
            best.MatchScore);
        return TrackMatchResolution.Bound(
            identity,
            nameof(MatchSourceKind.Automatic),
            best.MatchScore);
    }

    private static bool CanReuseCachedVersion(TrackLookupEntry entry, TrackIdentity track)
    {
        if (!string.Equals(entry.MatchSource, nameof(MatchSourceKind.Automatic), StringComparison.OrdinalIgnoreCase))
            return true;

        // The persisted key does not include album. Revalidate automatic entries instead
        // of allowing one release/language to silently reuse another release's lyrics.
        if (!string.IsNullOrWhiteSpace(track.Album)
            && CachedMetadataMatcher.ScoreAlbum(track.Album, entry.Album) < 95)
            return false;

        const SongVersionFlags languages = SongVersionFlags.JapaneseVersion
            | SongVersionFlags.ChineseVersion | SongVersionFlags.EnglishVersion | SongVersionFlags.KoreanVersion;
        var requestedLanguage = SongVersionDetector.Detect(track.Title, track.Album) & languages;
        var cachedLanguage = SongVersionDetector.Detect(entry.Title, entry.Album) & languages;
        return requestedLanguage == cachedLanguage;
    }

    private static QQSongIdentity ToIdentity(TrackLookupEntry entry, TrackIdentity track) =>
        new(
            entry.SongId,
            entry.SongMid,
            string.IsNullOrEmpty(entry.Title) ? track.Title : entry.Title,
            string.IsNullOrEmpty(entry.Artists) ? track.Artist : entry.Artists,
            entry.Album,
            entry.MatchedDurationSeconds > 0
                ? entry.MatchedDurationSeconds
                : track.DurationSeconds ?? 0);
}
