using System.Text;
using System.Text.RegularExpressions;
using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;

namespace TaskbarLyrics.QQMusic.Services;

/// <summary>
/// Multi-signal song scorer: Title / Artist / Duration / Album + Version penalty + Confidence.
/// </summary>
public sealed class SongMatcher
{
    public const double DefaultThreshold = 75.0;

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex FeatRegex = new(
        @"\b(feat\.?|ft\.?|featuring)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TitleGroupRegex = new(
        @"[\(\[]([^\(\)\[\]]+)[\)\]]",
        RegexOptions.Compiled);
    private static readonly Regex TrackLanguageAnnotationRegex = new(
        @"^(?:(?:english|japanese|chinese|korean|en|jp|cn|kr)\s*ver(?:sion)?\.?|英文版|英语版|日文版|日语版|日本語|日本版|中文版|国语版|國語版|华语版|韩文版|韩语版|韓文版|韓語版|한국어\s*버전)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrackFeaturedArtistsRegex = new(
        @"^(?:feat(?:uring)?|ft)\.?\s+(?<artists>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ArtistAliasStore _aliases;

    public SongMatcher(ArtistAliasStore? aliases = null)
    {
        _aliases = aliases ?? new ArtistAliasStore();
    }

    public void ScoreAll(
        string title,
        string? artist,
        int? durationSeconds,
        IReadOnlyList<QQSongCandidate> candidates,
        string? album = null)
    {
        var inputFlags = SongVersionDetector.Detect(title, album);
        foreach (var c in candidates)
        {
            ScoreInto(title, artist, durationSeconds, album, inputFlags, c);
        }

        ApplyCompleteArtistPreference(artist, durationSeconds, candidates);
        ApplyCorroboratedRecordingPreference(durationSeconds, candidates);
        ApplyExactAlbumTrackPreference(title, artist, durationSeconds, album, candidates);
        ApplyKnownAlbumPreference(durationSeconds, candidates);
        ApplyRelativeConfidence(candidates);
    }

    /// <summary>Legacy helper — prefer EvaluateAutoMatch for binding decisions.</summary>
    public QQSongCandidate? FindBestMatch(
        string title,
        string? artist,
        int? durationSeconds,
        IReadOnlyList<QQSongCandidate> candidates,
        string? album = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        ScoreAll(title, artist, durationSeconds, candidates, album);
        return candidates
            .OrderByDescending(c => c.MatchScore)
            .ThenBy(c => c.Rank)
            .First();
    }

    public void ScoreInto(
        string queryTitle,
        string? queryArtist,
        int? queryDuration,
        string? queryAlbum,
        SongVersionFlags inputFlags,
        QQSongCandidate candidate)
    {
        candidate.VersionFlags = SongVersionDetector.Detect(candidate.Title, candidate.Album);
        var titleScore = ScoreTitle(queryTitle, candidate.Title);
        var hasArtist = !string.IsNullOrWhiteSpace(queryArtist);
        var artistScore = hasArtist ? ScoreArtist(queryArtist!, candidate.Artists) : 0;
        var hasDuration = queryDuration is > 0;
        var durationScore = hasDuration ? ScoreDuration(queryDuration!.Value, candidate.DurationSeconds) : 0;
        var durationDiff = hasDuration && candidate.DurationSeconds > 0
            ? Math.Abs(queryDuration!.Value - candidate.DurationSeconds)
            : int.MaxValue;
        // GSMTC duration can include leading/trailing media segments. Once the discrepancy
        // exceeds the useful scoring band, rely on strong title/artist/album identity while
        // retaining the existing >30s hard cap against obviously different recordings.
        var useDurationSignal = hasDuration && durationDiff <= 20;
        var hasAlbum = !string.IsNullOrWhiteSpace(queryAlbum) && !string.IsNullOrWhiteSpace(candidate.Album);
        var albumScore = hasAlbum ? ScoreAlbum(queryAlbum!, candidate.Album) : 0;

        double wTitle, wArtist, wDuration, wAlbum;
        if (hasAlbum && hasArtist && useDurationSignal)
        {
            wTitle = 0.40; wArtist = 0.30; wDuration = 0.20; wAlbum = 0.10;
        }
        else if (hasAlbum && hasArtist)
        {
            wTitle = 0.50; wArtist = 0.35; wDuration = 0; wAlbum = 0.15;
        }
        else if (hasArtist && useDurationSignal)
        {
            wTitle = 0.45; wArtist = 0.35; wDuration = 0.20; wAlbum = 0;
        }
        else if (hasArtist)
        {
            wTitle = 0.55; wArtist = 0.45; wDuration = 0; wAlbum = 0;
        }
        else if (!hasArtist && hasDuration)
        {
            wTitle = 0.80; wArtist = 0; wDuration = 0.20; wAlbum = 0;
        }
        else
        {
            wTitle = 1.0; wArtist = 0; wDuration = 0; wAlbum = 0;
        }

        var baseScore = titleScore * wTitle + artistScore * wArtist + durationScore * wDuration + albumScore * wAlbum;
        var extra = SongVersionDetector.ExtraFlags(inputFlags, candidate.VersionFlags);
        var penalty = SongVersionDetector.ComputePenalty(extra | SongVersionDetector.LanguageDifference(inputFlags, candidate.VersionFlags));

        // Matching version flags: small bonus
        var shared = inputFlags & candidate.VersionFlags;
        if (shared != SongVersionFlags.None)
        {
            baseScore = Math.Min(100, baseScore + 3);
        }

        var final = Math.Clamp(baseScore - penalty, 0, 100);
        var exactMetadataConsensus = titleScore >= 98
                                     && artistScore >= 95
                                     && albumScore >= 95
                                     && penalty == 0;

        // Hard caps
        if (titleScore < 45)
        {
            final = Math.Min(final, 50);
        }

        if (hasArtist && artistScore < 40)
        {
            final = Math.Min(final, 60);
        }

        if (hasDuration
            && Math.Abs(queryDuration!.Value - candidate.DurationSeconds) > 30
            && !exactMetadataConsensus)
        {
            final = Math.Min(final, 70);
        }

        candidate.TitleScore = Math.Round(titleScore, 2);
        candidate.ArtistScore = Math.Round(artistScore, 2);
        candidate.DurationScore = Math.Round(durationScore, 2);
        candidate.AlbumScore = Math.Round(albumScore, 2);
        candidate.VersionPenalty = Math.Round(penalty, 2);
        candidate.MatchScore = Math.Round(final, 2);
        candidate.Confidence = ClassifyConfidence(candidate, inputFlags, hasArtist, hasDuration, queryDuration);
        candidate.RejectReason = BuildRejectReason(candidate, inputFlags, hasArtist, hasDuration, queryDuration);
        // Title/artist aliases can identify the composition while the album identifies
        // a different recording or language. Do not stop searching on that evidence.
        if (hasAlbum && albumScore < 50 && candidate.Confidence == MatchConfidence.High)
        {
            candidate.Confidence = MatchConfidence.Medium;
            candidate.RejectReason = "Conflicting album metadata; keep searching for the requested release";
        }
    }

    private static void ApplyCompleteArtistPreference(
        string? queryArtist, int? queryDuration, IReadOnlyList<QQSongCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(queryArtist) || queryDuration is not > 0)
            return;

        var queryArtists = SplitArtists(queryArtist).ToHashSet(StringComparer.Ordinal);
        if (queryArtists.Count == 0)
            return;

        bool StrongMetadata(QQSongCandidate c) =>
            c.TitleScore >= 98 && c.AlbumScore >= 95 && c.VersionPenalty == 0
            && c.DurationSeconds > 0 && Math.Abs(c.DurationSeconds - queryDuration.Value) <= 20;

        // Only break a near tie with corroborating metadata, never manufacture a
        // high-confidence match from artist names alone or an unreliable duration.
        var exact = candidates.Where(c => StrongMetadata(c)
            && c.MatchScore >= 90 && c.Confidence == MatchConfidence.High
            && queryArtists.SetEquals(SplitArtists(c.Artists))).ToArray();
        foreach (var candidate in candidates)
        {
            var artists = SplitArtists(candidate.Artists).ToHashSet(StringComparer.Ordinal);
            if (!StrongMetadata(candidate) || artists.Count == 0 || queryArtists.SetEquals(artists)
                || !(queryArtists.IsSubsetOf(artists) || artists.IsSubsetOf(queryArtists)))
                continue;

            var preferred = exact.Where(c => c.VersionFlags == candidate.VersionFlags
                && Math.Abs(c.DurationSeconds - candidate.DurationSeconds) <= 2
                && Math.Abs(c.MatchScore - candidate.MatchScore) < 5)
                .OrderByDescending(c => c.MatchScore).FirstOrDefault();
            if (preferred is null)
                continue;

            candidate.MatchScore = Math.Round(Math.Min(candidate.MatchScore, preferred.MatchScore - 6), 2);
            candidate.Confidence = MatchConfidence.Medium;
            candidate.RejectReason = string.IsNullOrEmpty(candidate.RejectReason)
                ? "Complete artist list preferred for otherwise comparable metadata"
                : candidate.RejectReason + "; Complete artist list preferred for otherwise comparable metadata";
        }
    }

    private static void ApplyKnownAlbumPreference(int? queryDuration, IReadOnlyList<QQSongCandidate> candidates)
    {
        if (queryDuration is not > 0) return;
        var confirmed = candidates.Where(c => HasExactMetadataConsensus(c)
            && c.AlbumScore == 100 && c.DurationScore == 100 && c.DurationSeconds > 0
            && c.Confidence == MatchConfidence.High && c.MatchScore >= 95).ToArray();
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Album) || candidate.TitleScore < 98
                || candidate.ArtistScore < 95 || candidate.DurationScore != 100
                || candidate.DurationSeconds <= 0 || candidate.VersionPenalty != 0) continue;
            var preferred = confirmed.Where(c => c.VersionFlags == candidate.VersionFlags
                && Math.Abs(c.DurationSeconds - candidate.DurationSeconds) <= 2)
                .OrderByDescending(c => c.MatchScore).FirstOrDefault();
            if (preferred is null || candidate.MatchScore <= preferred.MatchScore - 8) continue;
            // Missing metadata is not contradictory, but must not erase positive album evidence.
            // Two fully identified releases still go through normal ambiguity checks.
            candidate.MatchScore = Math.Round(preferred.MatchScore - 8, 2);
            if (candidate.Confidence == MatchConfidence.High) candidate.Confidence = MatchConfidence.Medium;
            candidate.RejectReason = "Exact album match preferred over otherwise matching candidate with missing album";
        }
    }

    private static void ApplyCorroboratedRecordingPreference(
        int? queryDuration, IReadOnlyList<QQSongCandidate> candidates)
    {
        if (queryDuration is not > 0)
            return;

        // A missing album and a discarded duration signal must not nearly tie a
        // recording corroborated by every input. Without that evidence keep the
        // usual ambiguity rules, including tolerance for stale GSMTC durations.
        var confirmed = candidates.Where(c => HasExactMetadataConsensus(c)
            && c.Confidence == MatchConfidence.High && c.MatchScore >= 95
            && c.DurationSeconds > 0 && c.DurationScore == 100).ToArray();
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Album) || candidate.DurationSeconds <= 0
                || Math.Abs(candidate.DurationSeconds - queryDuration.Value) <= 20)
                continue;

            var preferred = confirmed.Where(c => c.VersionFlags == candidate.VersionFlags)
                .OrderByDescending(c => c.MatchScore).FirstOrDefault();
            if (preferred is null || candidate.MatchScore <= preferred.MatchScore - 8)
                continue;

            candidate.MatchScore = Math.Round(preferred.MatchScore - 8, 2);
            if (candidate.Confidence == MatchConfidence.High)
                candidate.Confidence = MatchConfidence.Medium;
            candidate.RejectReason = "Complete album and duration match preferred over missing album with conflicting duration";
        }
    }

    private void ApplyExactAlbumTrackPreference(
        string title, string? artist, int? duration, string? album, IReadOnlyList<QQSongCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album) || duration is not > 0)
            return;

        var queryTitle = NormalizeTitle(title);
        var (queryCore, queryFeatured) = ReadTrackTitle(title);
        var requestedArtists = RecordingArtists(artist);
        if (queryCore.Length == 0 || requestedArtists.Count == 0) return;
        // A track's own featured credit is authoritative; an album may contain
        // several recordings and must never supply that credit to every track.
        requestedArtists.UnionWith(queryFeatured);
        var exact = candidates.Where(c => NormalizeTitle(c.Title) == queryTitle
            && requestedArtists.SetEquals(RecordingArtists(c.Artists))
            && SameAlbumRelease(album, c.Album) && c.DurationSeconds > 0
            && Math.Abs(c.DurationSeconds - duration.Value) <= 2
            && c.VersionPenalty == 0 && c.Confidence == MatchConfidence.High && c.MatchScore >= 95).ToArray();
        if (exact.Length == 0) return;

        const SongVersionFlags languages = SongVersionFlags.JapaneseVersion | SongVersionFlags.ChineseVersion
            | SongVersionFlags.KoreanVersion | SongVersionFlags.EnglishVersion;
        var requestedLanguage = SongVersionDetector.Detect(title) & languages;
        var albumWithoutFeatured = WithoutFeaturedAnnotations(album);
        foreach (var candidate in candidates)
        {
            if (exact.Contains(candidate) || candidate.DurationSeconds <= 0
                || Math.Abs(candidate.DurationSeconds - duration.Value) > 2)
                continue;
            var (core, featured) = ReadTrackTitle(candidate.Title);
            var artists = RecordingArtists(candidate.Artists);
            if (core != queryCore || artists.Count == 0
                || !(artists.IsSubsetOf(requestedArtists) || requestedArtists.IsSubsetOf(artists)))
                continue;

            // Album language is useful context, but does not make the plain track
            // and an explicitly labelled language version interchangeable.
            var differentTrackLanguage = SameAlbumRelease(album, candidate.Album)
                && (SongVersionDetector.Detect(candidate.Title) & languages) != requestedLanguage;
            var missingTrackFeature = queryFeatured.Any(name => !featured.Contains(name) && !artists.Contains(name))
                && artists.IsSubsetOf(requestedArtists)
                && (SameAlbumRelease(album, candidate.Album)
                    || SameAlbumRelease(albumWithoutFeatured, WithoutFeaturedAnnotations(candidate.Album)));
            var differentReleaseCredit = !HasAnnotatedArtistCredit(artist)
                && !HasAnnotatedArtistCredit(candidate.Artists)
                && featured.IsSubsetOf(artists)
                && exact.All(c => !HasAnnotatedArtistCredit(c.Artists))
                && exact.Any(c => c.VersionFlags == candidate.VersionFlags)
                && HasCorroboratedReleaseCreditDifference(
                    album, requestedArtists, candidate.Album, artists);
            if (!differentTrackLanguage && !missingTrackFeature && !differentReleaseCredit) continue;

            var preferred = exact.Where(c => Math.Abs(c.DurationSeconds - candidate.DurationSeconds) <= 2)
                .OrderByDescending(c => c.MatchScore).FirstOrDefault();
            if (preferred is null || candidate.MatchScore <= preferred.MatchScore - 6) continue;
            candidate.MatchScore = Math.Round(preferred.MatchScore - 6, 2);
            if (candidate.Confidence == MatchConfidence.High) candidate.Confidence = MatchConfidence.Medium;
            candidate.RejectReason = "Exact album track preferred over a different explicit language or featured credit";
        }
    }

    private static HashSet<string> RecordingArtists(string artists) =>
        SplitArtists(artists).Select(StripArtistAnnotations).Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static bool SameAlbumRelease(string left, string right)
    {
        var normalized = CollapseDuplicateTrailingAnnotation(NormalizeTitle(left));
        return normalized.Length > 0
            && normalized == CollapseDuplicateTrailingAnnotation(NormalizeTitle(right));
    }

    private static string NormalizeTrackAnnotations(string title) =>
        ToHalfWidth(title.Trim()).Replace('（', '(').Replace('）', ')').Replace('【', '[').Replace('】', ']');

    private static (string Core, HashSet<string> Featured) ReadTrackTitle(string title)
    {
        var featured = new HashSet<string>(StringComparer.Ordinal);
        var core = TitleGroupRegex.Replace(NormalizeTrackAnnotations(title), match =>
        {
            var annotation = match.Groups[1].Value.Trim();
            if (TrackLanguageAnnotationRegex.IsMatch(annotation)) return " ";
            var credit = TrackFeaturedArtistsRegex.Match(annotation);
            if (!credit.Success) return match.Value; // Unknown annotations remain significant.
            featured.UnionWith(RecordingArtists(credit.Groups["artists"].Value));
            return " ";
        });
        return (NormalizeTitle(core), featured);
    }

    private static string WithoutFeaturedAnnotations(string title) =>
        NormalizeTitle(TitleGroupRegex.Replace(NormalizeTrackAnnotations(title), match =>
            TrackFeaturedArtistsRegex.IsMatch(match.Groups[1].Value.Trim()) ? " " : match.Value));

    private static bool HasCorroboratedReleaseCreditDifference(
        string requestedAlbum, HashSet<string> requestedArtists,
        string candidateAlbum, HashSet<string> candidateArtists)
    {
        // A plain single and its featured edition may have exactly the same title
        // and duration. Prefer a fully corroborated release only when the changed
        // album credit agrees with the actual artist-set difference in both ways.
        // Album credits alone must never add performers to the requested track.
        if (requestedArtists.SetEquals(candidateArtists)
            || SameAlbumRelease(requestedAlbum, candidateAlbum)
            || !SameAlbumRelease(WithoutFeaturedAnnotations(requestedAlbum),
                WithoutFeaturedAnnotations(candidateAlbum)))
            return false;

        var requestedCredits = FeaturedReleaseArtists(requestedAlbum);
        var candidateCredits = FeaturedReleaseArtists(candidateAlbum);
        return requestedCredits.IsSubsetOf(requestedArtists)
            && candidateCredits.IsSubsetOf(candidateArtists)
            && requestedArtists.Except(candidateArtists).ToHashSet(StringComparer.Ordinal)
                .SetEquals(requestedCredits.Except(candidateCredits))
            && candidateArtists.Except(requestedArtists).ToHashSet(StringComparer.Ordinal)
                .SetEquals(candidateCredits.Except(requestedCredits));
    }

    private static HashSet<string> FeaturedReleaseArtists(string album)
    {
        var artists = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match group in TitleGroupRegex.Matches(NormalizeTrackAnnotations(album)))
        {
            var credit = TrackFeaturedArtistsRegex.Match(group.Groups[1].Value.Trim());
            if (credit.Success)
                artists.UnionWith(RecordingArtists(credit.Groups["artists"].Value));
        }
        return artists;
    }

    private static bool HasAnnotatedArtistCredit(string artists) =>
        TitleGroupRegex.Matches(NormalizeTrackAnnotations(artists)).Cast<Match>()
            .Any(group => Regex.IsMatch(group.Groups[1].Value,
                @"\b(?:with|feat(?:uring)?|ft)\b|合唱|合作", RegexOptions.IgnoreCase));

    private static void ApplyRelativeConfidence(IReadOnlyList<QQSongCandidate> candidates)
    {
        var ranked = candidates.OrderByDescending(c => c.MatchScore).ThenBy(c => c.Rank).ToList();
        if (ranked.Count < 2)
        {
            return;
        }

        var top = ranked[0];
        var second = ranked[1];
        var margin = top.MatchScore - second.MatchScore;
        if (margin < 5
            && top.Confidence == MatchConfidence.High
            && !AreEquivalentRecordings(top, second))
        {
            top.Confidence = MatchConfidence.Medium;
            if (string.IsNullOrEmpty(top.RejectReason))
            {
                top.RejectReason = $"ScoreMargin {margin:0.#} < 5 — ambiguous with #{2}";
            }
            else
            {
                top.RejectReason += $"; ScoreMargin {margin:0.#} < 5";
            }
        }
    }

    private MatchConfidence ClassifyConfidence(
        QQSongCandidate c,
        SongVersionFlags inputFlags,
        bool hasArtist,
        bool hasDuration,
        int? queryDuration)
    {
        var extra = SongVersionDetector.ExtraFlags(inputFlags, c.VersionFlags);

        // Hard blocks on High
        if (SongVersionDetector.LanguageDifference(inputFlags, c.VersionFlags) != SongVersionFlags.None
            || extra.HasFlag(SongVersionFlags.Cover)
            || extra.HasFlag(SongVersionFlags.Remix)
            || extra.HasFlag(SongVersionFlags.JapaneseVersion)
            || extra.HasFlag(SongVersionFlags.ChineseVersion)
            || extra.HasFlag(SongVersionFlags.EnglishVersion)
            || extra.HasFlag(SongVersionFlags.Instrumental)
            || extra.HasFlag(SongVersionFlags.SpedUp)
            || extra.HasFlag(SongVersionFlags.Slowed))
        {
            if (c.MatchScore >= 80)
            {
                return MatchConfidence.Medium;
            }

            if (c.MatchScore >= 65)
            {
                return MatchConfidence.Low;
            }

            return MatchConfidence.Rejected;
        }

        if (hasArtist && c.ArtistScore < 50)
        {
            return c.MatchScore >= 65 ? MatchConfidence.Low : MatchConfidence.Rejected;
        }

        if (c.TitleScore < 50)
        {
            return MatchConfidence.Rejected;
        }

        if (hasDuration && queryDuration is > 0
            && Math.Abs(queryDuration.Value - c.DurationSeconds) > 30
            && !HasExactMetadataConsensus(c)
            && c.MatchScore >= 65)
        {
            return MatchConfidence.Low;
        }

        if (HasStrongIdentityConsensus(c))
        {
            return MatchConfidence.High;
        }

        if (c.MatchScore >= 90)
        {
            return MatchConfidence.High;
        }

        if (c.MatchScore >= 80)
        {
            return MatchConfidence.Medium;
        }

        if (c.MatchScore >= 65)
        {
            return MatchConfidence.Low;
        }

        return MatchConfidence.Rejected;
    }

    /// <summary>
    /// Allows automatic binding when independent identity signals agree strongly even
    /// if GSMTC duration drift keeps the weighted score below the generic 90 threshold.
    /// Version conflicts remain excluded. Exact multilingual title aliases plus an exact
    /// artist may substitute for duration when GSMTC is known to be transitioning tracks.
    /// </summary>
    public static bool HasStrongIdentityConsensus(QQSongCandidate candidate)
    {
        var corroboratedByAlbumAndDuration = candidate.AlbumScore >= 80
                                             && (candidate.DurationScore >= 20
                                                 || HasExactMetadataConsensus(candidate));
        var exactTitleArtistWithoutReliableDuration = candidate.TitleScore >= 97
                                                      && candidate.ArtistScore >= 98;
        return candidate.MatchScore >= 80
               && candidate.TitleScore >= 97
               && candidate.ArtistScore >= 95
               && (corroboratedByAlbumAndDuration || exactTitleArtistWithoutReliableDuration)
               && candidate.VersionPenalty == 0;
    }

    public static bool HasExactMetadataConsensus(QQSongCandidate candidate) =>
        candidate.TitleScore >= 98
        && candidate.ArtistScore >= 95
        && candidate.AlbumScore >= 95
        && candidate.VersionPenalty == 0;

    /// <summary>
    /// QQ Music can expose one recording under separate SongMids whose titles use
    /// different language aliases. Such duplicates must not create false ambiguity.
    /// Explicit version flags still have to agree.
    /// </summary>
    public static bool AreEquivalentRecordings(QQSongCandidate left, QQSongCandidate right)
    {
        if (left.VersionFlags != right.VersionFlags
            || left.DurationSeconds <= 0
            || right.DurationSeconds <= 0
            || Math.Abs(left.DurationSeconds - right.DurationSeconds) > 2)
        {
            return false;
        }

        var leftArtist = StripArtistAnnotations(NormalizeArtist(left.Artists));
        var rightArtist = StripArtistAnnotations(NormalizeArtist(right.Artists));
        if (leftArtist.Length == 0
            || !string.Equals(leftArtist, rightArtist, StringComparison.Ordinal))
        {
            return false;
        }

        var leftAlbum = CollapseDuplicateTrailingAnnotation(NormalizeTitle(left.Album));
        var rightAlbum = CollapseDuplicateTrailingAnnotation(NormalizeTitle(right.Album));
        if (leftAlbum.Length == 0
            || !string.Equals(leftAlbum, rightAlbum, StringComparison.Ordinal))
        {
            return false;
        }

        var leftTitle = NormalizeTitle(left.Title);
        var rightTitle = NormalizeTitle(right.Title);
        return string.Equals(leftTitle, rightTitle, StringComparison.Ordinal)
               || HasExactDeclaredTitleAlias(left.Title, right.Title)
               || HasExactDeclaredTitleAlias(right.Title, left.Title);
    }

    private static string BuildRejectReason(
        QQSongCandidate c,
        SongVersionFlags inputFlags,
        bool hasArtist,
        bool hasDuration,
        int? queryDuration)
    {
        var parts = new List<string>();
        var extra = SongVersionDetector.ExtraFlags(inputFlags, c.VersionFlags);
        if (extra != SongVersionFlags.None)
        {
            parts.Add($"Candidate has {SongVersionDetector.Describe(extra)} but input does not (penalty -{c.VersionPenalty:0.#})");
        }

        if (hasArtist && c.ArtistScore < 50)
        {
            parts.Add($"Weak artist match ({c.ArtistScore:0.#})");
        }

        if (c.TitleScore < 50)
        {
            parts.Add($"Weak title match ({c.TitleScore:0.#})");
        }

        if (hasDuration && queryDuration is > 0
            && Math.Abs(queryDuration.Value - c.DurationSeconds) > 30)
        {
            parts.Add($"Duration diff {Math.Abs(queryDuration.Value - c.DurationSeconds)}s > 30");
        }

        return string.Join("; ", parts);
    }

    public double ScoreTitle(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(candidate))
        {
            return 100;
        }

        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        if (string.Equals(query.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var nq = NormalizeTitle(query);
        var nc = NormalizeTitle(candidate);
        if (nq.Length == 0 || nc.Length == 0)
        {
            return 0;
        }

        if (string.Equals(nq, nc, StringComparison.Ordinal))
        {
            return 98;
        }

        var partsQ = CanonicalizeTitleParts(nq);
        var partsC = CanonicalizeTitleParts(nc);
        if (partsQ.Length > 0
            && string.Equals(partsQ, partsC, StringComparison.Ordinal))
        {
            return 97;
        }

        var sq = SearchQueryPlanner.SimplifyTitle(query).ToLowerInvariant();
        var sc = SearchQueryPlanner.SimplifyTitle(candidate).ToLowerInvariant();
        if (sq.Length > 0 && sc.Length > 0 && string.Equals(sq, sc, StringComparison.Ordinal))
        {
            return 96;
        }

        // Providers sometimes publish one multilingual bracket alias as the title:
        // "영웅 (英雄; Kick It)" -> "英雄;Kick It".
        if (HasExactDeclaredTitleAlias(query, candidate)
            || HasExactDeclaredTitleAlias(candidate, query))
        {
            return 97;
        }

        // Core comparison without stripping version words aggressively — only trailing pure brackets
        var cq = StripTrailingParentheticalKeepVersion(nq);
        var cc = StripTrailingParentheticalKeepVersion(nc);
        if (cq.Length > 0 && cc.Length > 0 && string.Equals(cq, cc, StringComparison.Ordinal))
        {
            return 90;
        }

        if (cq.Length >= 2 && (nc.Contains(cq, StringComparison.Ordinal) || nq.Contains(cc, StringComparison.Ordinal)))
        {
            return 85;
        }

        var sim = NormalizedSimilarity(nq, nc) * 100.0;
        var simpSim = (sq.Length > 0 && sc.Length > 0)
            ? NormalizedSimilarity(sq, sc) * 100.0
            : 0;
        var coreSim = (cq.Length > 0 && cc.Length > 0)
            ? NormalizedSimilarity(cq, cc) * 100.0
            : sim;

        return Math.Max(sim, Math.Max(simpSim, coreSim * 0.95));
    }

    public double ScoreArtist(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(candidate))
        {
            return 100;
        }

        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        var qArtists = SplitArtists(query);
        var cArtists = SplitArtists(candidate);
        if (qArtists.Count == 0 || cArtists.Count == 0)
        {
            return 0;
        }

        double best = 0;
        foreach (var q in qArtists)
        {
            double localBest = 0;
            foreach (var c in cArtists)
            {
                if (string.Equals(q, c, StringComparison.Ordinal))
                {
                    localBest = 100;
                    break;
                }

                // Known alias → 95 (do not treat as full exact for pollution)
                if (_aliases.AreAliases(q, c))
                {
                    localBest = Math.Max(localBest, 95);
                    continue;
                }

                var baseQ = StripArtistAnnotations(q);
                var baseC = StripArtistAnnotations(c);
                if (baseQ.Length >= 2
                    && string.Equals(baseQ, baseC, StringComparison.Ordinal))
                {
                    localBest = Math.Max(localBest, 98);
                    continue;
                }

                if (q.Contains(c, StringComparison.Ordinal) || c.Contains(q, StringComparison.Ordinal))
                {
                    // Avoid short substring false positives
                    if (Math.Min(q.Length, c.Length) >= 2)
                    {
                        localBest = Math.Max(localBest, 88);
                    }

                    continue;
                }

                localBest = Math.Max(localBest, NormalizedSimilarity(q, c) * 100.0);
            }

            best = Math.Max(best, localBest);
        }

        return best;
    }

    public double ScoreDuration(int querySeconds, int candidateSeconds)
    {
        if (querySeconds <= 0 || candidateSeconds <= 0)
        {
            return 0;
        }

        var diff = Math.Abs(querySeconds - candidateSeconds);
        if (diff <= 2) return 100;
        if (diff <= 5) return 90;
        if (diff <= 10) return 70;
        if (diff <= 15) return 40;
        if (diff <= 20) return 20;
        return 0;
    }

    public double ScoreAlbum(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        if (string.Equals(query.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var nq = NormalizeTitle(query);
        var nc = NormalizeTitle(candidate);
        if (nq.Length == 0 || nc.Length == 0)
        {
            return 0;
        }

        if (string.Equals(nq, nc, StringComparison.Ordinal))
        {
            return 98;
        }

        var duplicateCollapsedQ = CollapseDuplicateTrailingAnnotation(nq);
        var duplicateCollapsedC = CollapseDuplicateTrailingAnnotation(nc);
        if (string.Equals(duplicateCollapsedQ, duplicateCollapsedC, StringComparison.Ordinal))
        {
            return 98;
        }

        if (nq.Contains(nc, StringComparison.Ordinal) || nc.Contains(nq, StringComparison.Ordinal))
        {
            return 85;
        }

        return NormalizedSimilarity(nq, nc) * 100.0;
    }

    public static string NormalizeTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var s = input.Trim();
        s = s.Replace('\u3000', ' ').Replace('\u00A0', ' ');
        s = MultiSpaceRegex.Replace(s, " ");
        s = s.ToLowerInvariant();
        s = ToHalfWidth(s);
        s = s.Replace('（', '(').Replace('）', ')');
        s = s.Replace('【', '[').Replace('】', ']');
        s = s.Replace('「', '"').Replace('」', '"');
        s = s.Replace('『', '"').Replace('』', '"');
        s = s.Replace('：', ':').Replace('；', ';');
        s = s.Replace('，', ',').Replace('。', '.');
        s = s.Replace('！', '!').Replace('？', '?');
        s = s.Replace('／', '/').Replace('－', '-').Replace('—', '-').Replace('–', '-');
        s = FeatRegex.Replace(s, " ");
        return MultiSpaceRegex.Replace(s, " ").Trim();
    }

    public static string NormalizeArtist(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var s = ToHalfWidth(input.Trim().ToLowerInvariant());
        s = s.Replace('\u3000', ' ');
        s = FeatRegex.Replace(s, "/");
        s = s.Replace('&', '/').Replace(',', '/').Replace('，', '/').Replace('、', '/');
        return MultiSpaceRegex.Replace(s, " ").Trim();
    }

    public static List<string> SplitArtists(string input)
    {
        var n = NormalizeArtist(input);
        if (n.Length == 0)
        {
            return new List<string>();
        }

        return n.Split(new[] { '/', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => MultiSpaceRegex.Replace(a, " ").Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string StripTrailingParentheticalKeepVersion(string normalized)
    {
        // Only strip trailing empty-ish groups; keep Japanese Version etc for version detector
        return Regex.Replace(normalized, @"\s*[\(\[\{（【]\s*[\)\]\}）】]\s*$", string.Empty).Trim();
    }

    private static string StripArtistAnnotations(string normalized) =>
        MultiSpaceRegex.Replace(
            Regex.Replace(normalized, @"\s*[\(\[][^\(\)\[\]]+[\)\]]", " "),
            " ").Trim();

    private static string CollapseDuplicateTrailingAnnotation(string normalized)
    {
        // QQ may repeat an entire album in a trailing translation annotation, including
        // nested parentheses: "Album (Subtitle) (album (subtitle))". Only remove an
        // annotation when its complete content equals the complete preceding name.
        while (normalized.Length > 0 && normalized[^1] is ')' or ']')
        {
            var closing = new Stack<char>();
            var openingIndex = -1;
            for (var i = normalized.Length - 1; i >= 0; i--)
            {
                var ch = normalized[i];
                if (ch is ')' or ']') closing.Push(ch);
                else if (ch is '(' or '[')
                {
                    if (closing.Count == 0 || closing.Pop() != (ch == '(' ? ')' : ']'))
                        return normalized;
                    if (closing.Count == 0) { openingIndex = i; break; }
                }
            }
            if (openingIndex <= 0) return normalized;
            var core = normalized[..openingIndex].TrimEnd();
            var annotation = normalized[(openingIndex + 1)..^1].Trim();
            if (!string.Equals(core, annotation, StringComparison.Ordinal)) return normalized;
            normalized = core;
        }
        return normalized;
    }
    private static string CanonicalizeTitleParts(string normalized)
    {
        var parts = new List<string>();
        foreach (Match match in TitleGroupRegex.Matches(normalized))
        {
            var part = SearchQueryPlanner.SimplifyTitle(match.Groups[1].Value).ToLowerInvariant();
            if (part.Length > 0)
            {
                parts.Add(part);
            }
        }

        var core = SearchQueryPlanner.SimplifyTitle(TitleGroupRegex.Replace(normalized, " "))
            .ToLowerInvariant();
        if (core.Length > 0)
        {
            parts.Add(core);
        }

        if (parts.Count < 2)
        {
            return string.Empty;
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join('\u001f', parts);
    }

    private static bool HasExactDeclaredTitleAlias(string titleWithAliases, string candidateTitle)
    {
        var candidate = SearchQueryPlanner.SimplifyTitle(candidateTitle).ToLowerInvariant();
        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (Match match in TitleGroupRegex.Matches(titleWithAliases))
        {
            var alias = SearchQueryPlanner.SimplifyTitle(match.Groups[1].Value).ToLowerInvariant();
            if (alias.Length > 0 && string.Equals(alias, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static double NormalizedSimilarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1;

        var dist = LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)dist / maxLen;
    }

    public static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    private static string ToHalfWidth(string input)
    {
        try
        {
            return input.Normalize(NormalizationForm.FormKC);
        }
        catch
        {
            return input;
        }
    }
}
