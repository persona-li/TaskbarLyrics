using TaskbarLyrics.QQMusic.Logging;
using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;

namespace TaskbarLyrics.QQMusic.Services;

/// <summary>
/// Multi-phase QQ search → merge by SongMid → score → confidence decision.
/// </summary>
public sealed class MultiPhaseSongSearch : IAutomaticSongSearch
{
    private readonly QQMusicSearchService _search;
    private readonly SongMatcher _matcher;
    private readonly SearchQueryPlanner _planner;
    private readonly ArtistAliasStore _aliases;
    private readonly IQqMusicLogger _logger;

    public MultiPhaseSongSearch(
        QQMusicSearchService search,
        SongMatcher matcher,
        ArtistAliasStore aliases,
        IQqMusicLogger logger)
    {
        _search = search;
        _matcher = matcher;
        _aliases = aliases;
        _logger = logger;
        _planner = new SearchQueryPlanner(aliases);
    }

    public ArtistAliasStore Aliases => _aliases;
    public SongMatcher Matcher => _matcher;

    public async Task<AutoMatchDecision> AutoMatchAsync(
        string title,
        string? artist,
        int? durationSeconds,
        string? album = null,
        CancellationToken cancellationToken = default)
    {
        var inputFlags = SongVersionDetector.Detect(title, album);
        var plans = _planner.Plan(title, artist, album);
        var merged = new Dictionary<string, QQSongCandidate>(StringComparer.OrdinalIgnoreCase);
        var rawCount = 0;
        var phasesRun = new List<string>();

        SearchQueryPhase? lastPhase = null;

        foreach (var plan in plans)
        {
            // Group by phase: after finishing a phase, evaluate early-stop
            if (lastPhase is not null && plan.Phase != lastPhase)
            {
                var midRanked = ScoreAndRank(title, artist, durationSeconds, album, merged.Values.ToList());
                if (SearchResultQualityEvaluator.ShouldStopEarly(midRanked, inputFlags))
                {
                    _logger.Info($"SEARCH early-stop after {lastPhase}: High unique match.");
                    return BuildDecision(midRanked, inputFlags, rawCount, merged.Count, phasesRun, early: true);
                }

                var quality = SearchResultQualityEvaluator.Evaluate(midRanked, inputFlags);
                if (quality == SearchResultQuality.Good
                    && midRanked.Count > 0
                    && midRanked[0].Confidence == MatchConfidence.High)
                {
                    _logger.Info($"SEARCH stop after {lastPhase}: quality=Good.");
                    return BuildDecision(midRanked, inputFlags, rawCount, merged.Count, phasesRun, early: true);
                }
            }

            lastPhase = plan.Phase;
            phasesRun.Add($"{plan.Phase}:{plan.Label}");

            var remaining = QQMusicSearchService.MaxAutoCandidates - merged.Count;
            if (remaining <= 0)
            {
                break;
            }

            var pageSize = Math.Min(QQMusicSearchService.DefaultAutoPageSize, remaining);
            // Prefer 1 page first per query to save quota; expand if phase still poor
            var result = await _search.SearchKeywordPagesAsync(
                plan.Keyword,
                pageSize,
                maxPages: 1,
                maxTotal: remaining,
                cancellationToken).ConfigureAwait(false);

            _logger.Info(
                $"SEARCH PHASE\nPhase: {plan.Phase}\nQuery: {plan.Keyword}\nResults: {result.Candidates.Count}");

            if (!result.Success && result.Candidates.Count == 0)
            {
                _logger.Warn($"SEARCH phase failed: {result.Error}");
                continue;
            }

            rawCount += result.Candidates.Count;
            MergeCandidates(merged, result.Candidates, plan.Label);

            // If this phase is still Poor after first page, try page 2 of same query
            var after = ScoreAndRank(title, artist, durationSeconds, album, merged.Values.ToList());
            var q = SearchResultQualityEvaluator.Evaluate(after, inputFlags);
            if (q == SearchResultQuality.Poor
                && result.HasMore
                && merged.Count < QQMusicSearchService.MaxAutoCandidates)
            {
                var more = await _search.SearchKeywordAsync(
                    plan.Keyword,
                    page: 2,
                    pageSize,
                    cancellationToken).ConfigureAwait(false);
                if (more.Success)
                {
                    rawCount += more.Candidates.Count;
                    MergeCandidates(merged, more.Candidates, plan.Label + ":p2");
                    _logger.Info($"SEARCH PHASE page2 Query: {plan.Keyword} Results: {more.Candidates.Count}");
                }
            }
        }

        var ranked = ScoreAndRank(title, artist, durationSeconds, album, merged.Values.ToList());
        _logger.Info($"MERGED CANDIDATES\nRaw: {rawCount}\nUnique: {merged.Count}");
        LogTop(ranked, inputFlags, 10);
        return BuildDecision(ranked, inputFlags, rawCount, merged.Count, phasesRun, early: false);
    }

    /// <summary>Manual keyword search (no planner rewrite). Scores for display only.</summary>
    public async Task<(IReadOnlyList<QQSongCandidate> Items, bool HasMore, string? Error)> ManualSearchAsync(
        string keyword,
        int page,
        int pageSize,
        string title,
        string? artist,
        int? durationSeconds,
        string? album = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _search.SearchKeywordAsync(keyword, page, pageSize, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return (Array.Empty<QQSongCandidate>(), false, result.Error);
        }

        foreach (var c in result.Candidates)
        {
            c.MatchedQueries = new List<string> { $"Manual:p{page}" };
        }

        _matcher.ScoreAll(title, artist, durationSeconds, result.Candidates, album);
        // Manual: do not hide low scores — already scored for info
        var ordered = result.Candidates
            .OrderByDescending(c => c.MatchScore)
            .ThenBy(c => c.Rank)
            .ToList();
        return (ordered, result.HasMore, null);
    }

    private List<QQSongCandidate> ScoreAndRank(
        string title,
        string? artist,
        int? duration,
        string? album,
        List<QQSongCandidate> list)
    {
        _matcher.ScoreAll(title, artist, duration, list, album);
        return list
            .OrderByDescending(c => c.MatchScore)
            .ThenByDescending(c => c.ArtistScore)
            .ThenBy(c => c.Rank)
            .ToList();
    }

    private static void MergeCandidates(
        Dictionary<string, QQSongCandidate> merged,
        IEnumerable<QQSongCandidate> incoming,
        string queryLabel)
    {
        foreach (var c in incoming)
        {
            var key = c.DedupKey;
            if (merged.TryGetValue(key, out var existing))
            {
                if (!existing.MatchedQueries.Contains(queryLabel, StringComparer.OrdinalIgnoreCase))
                {
                    existing.MatchedQueries.Add(queryLabel);
                }

                // Prefer better rank (lower number) as base rank
                if (c.Rank > 0 && (existing.Rank == 0 || c.Rank < existing.Rank))
                {
                    existing.Rank = c.Rank;
                }

                continue;
            }

            c.MatchedQueries = new List<string> { queryLabel };
            merged[key] = c;
        }
    }

    private AutoMatchDecision BuildDecision(
        IReadOnlyList<QQSongCandidate> ranked,
        SongVersionFlags inputFlags,
        int rawCount,
        int uniqueCount,
        IReadOnlyList<string> phasesRun,
        bool early)
    {
        var quality = SearchResultQualityEvaluator.Evaluate(ranked, inputFlags);
        if (ranked.Count == 0)
        {
            return new AutoMatchDecision
            {
                Best = null,
                Confidence = MatchConfidence.Rejected,
                ResultQuality = SearchResultQuality.Poor,
                RankedCandidates = ranked,
                Reason = "No candidates recalled.",
                RawCount = rawCount,
                UniqueCount = uniqueCount,
                PhasesRun = phasesRun
            };
        }

        var top = ranked[0];
        if (top.Confidence == MatchConfidence.High && quality == SearchResultQuality.Good)
        {
            _logger.Info(
                $"AUTO MATCH High mid={top.SongMid} score={top.MatchScore} title={top.Title}");
            return new AutoMatchDecision
            {
                Best = top,
                Confidence = MatchConfidence.High,
                ResultQuality = quality,
                RankedCandidates = ranked,
                Reason = early ? "High confidence (early stop)." : "High confidence after multi-phase search.",
                RawCount = rawCount,
                UniqueCount = uniqueCount,
                PhasesRun = phasesRun
            };
        }

        // Log rejection of top for auto
        _logger.Warn(
            "TOP CANDIDATE REJECTED FOR AUTO MATCH\n" +
            $"Title: {top.Title}\n" +
            $"Artists: {top.Artists}\n" +
            $"Album: {top.Album}\n" +
            $"Duration: {top.DurationSeconds}s\n" +
            $"SongMid: {top.SongMid}\n" +
            $"TitleScore: {top.TitleScore}\n" +
            $"ArtistScore: {top.ArtistScore}\n" +
            $"DurationScore: {top.DurationScore}\n" +
            $"AlbumScore: {top.AlbumScore}\n" +
            $"VersionFlags: {SongVersionDetector.Describe(top.VersionFlags)}\n" +
            $"VersionPenalty: -{top.VersionPenalty}\n" +
            $"Final: {top.MatchScore}\n" +
            $"Confidence: {top.Confidence}\n" +
            $"Reason: {top.RejectReason}\n" +
            $"ResultQuality: {quality}");

        return new AutoMatchDecision
        {
            Best = top,
            Confidence = top.Confidence,
            ResultQuality = quality,
            RankedCandidates = ranked,
            Reason = string.IsNullOrEmpty(top.RejectReason)
                ? $"Ambiguous/low confidence ({top.Confidence}, quality={quality}). Prefer no wrong lyrics."
                : top.RejectReason,
            RawCount = rawCount,
            UniqueCount = uniqueCount,
            PhasesRun = phasesRun
        };
    }

    private void LogTop(IReadOnlyList<QQSongCandidate> ranked, SongVersionFlags inputFlags, int n)
    {
        var take = Math.Min(n, ranked.Count);
        for (var i = 0; i < take; i++)
        {
            var c = ranked[i];
            _logger.Info(
                $"#{i + 1}\n" +
                $"Title: {c.Title}\n" +
                $"Artist: {c.Artists}\n" +
                $"Album: {c.Album}\n" +
                $"Duration: {c.DurationSeconds}\n" +
                $"SongMid: {c.SongMid}\n" +
                $"TitleScore: {c.TitleScore}\n" +
                $"ArtistScore: {c.ArtistScore}\n" +
                $"DurationScore: {c.DurationScore}\n" +
                $"AlbumScore: {c.AlbumScore}\n" +
                $"VersionFlags: {SongVersionDetector.Describe(c.VersionFlags)}\n" +
                $"VersionPenalty: {c.VersionPenalty}\n" +
                $"FinalScore: {c.MatchScore}\n" +
                $"Confidence: {c.Confidence}\n" +
                $"Queries: {string.Join(", ", c.MatchedQueries)}");
        }
    }
}
