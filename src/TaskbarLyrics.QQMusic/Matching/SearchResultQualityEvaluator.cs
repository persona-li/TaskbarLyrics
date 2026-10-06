using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Services;

namespace TaskbarLyrics.QQMusic.Matching;

public static class SearchResultQualityEvaluator
{
    /// <summary>
    /// Judges whether current scored candidate set is good enough to stop multi-phase search.
    /// </summary>
    public static SearchResultQuality Evaluate(
        IReadOnlyList<QQSongCandidate> ranked,
        SongVersionFlags inputFlags)
    {
        if (ranked.Count == 0)
        {
            return SearchResultQuality.Poor;
        }

        var top = ranked[0];
        var highOk = top.Confidence == MatchConfidence.High
                     && (top.MatchScore >= 90 || SongMatcher.HasStrongIdentityConsensus(top))
                     && !HasHardVersionConflict(inputFlags, top.VersionFlags);

        if (highOk)
        {
            if (ranked.Count >= 2)
            {
                var margin = top.MatchScore - ranked[1].MatchScore;
                if (margin < 5 && !SongMatcher.AreEquivalentRecordings(top, ranked[1]))
                {
                    return SearchResultQuality.Ambiguous;
                }
            }

            return SearchResultQuality.Good;
        }

        // Top is version-conflicting while input is clean → poor recall set
        var extra = SongVersionDetector.ExtraFlags(inputFlags, top.VersionFlags);
        var badVersion = extra.HasFlag(SongVersionFlags.JapaneseVersion)
                         || extra.HasFlag(SongVersionFlags.Cover)
                         || extra.HasFlag(SongVersionFlags.Remix)
                         || extra.HasFlag(SongVersionFlags.Instrumental)
                         || extra.HasFlag(SongVersionFlags.SpedUp)
                         || extra.HasFlag(SongVersionFlags.Slowed);

        var artistWeak = top.ArtistScore < 55;
        var titleWeak = top.TitleScore < 60;

        if (badVersion || (artistWeak && titleWeak))
        {
            return SearchResultQuality.Poor;
        }

        if (top.Confidence is MatchConfidence.Medium or MatchConfidence.Low)
        {
            return SearchResultQuality.Ambiguous;
        }

        return SearchResultQuality.Ambiguous;
    }

    public static bool HasHardVersionConflict(SongVersionFlags input, SongVersionFlags candidate)
    {
        var extra = SongVersionDetector.ExtraFlags(input, candidate);
        return SongVersionDetector.LanguageDifference(input, candidate) != SongVersionFlags.None
               || extra.HasFlag(SongVersionFlags.JapaneseVersion)
               || extra.HasFlag(SongVersionFlags.ChineseVersion)
               || extra.HasFlag(SongVersionFlags.EnglishVersion)
               || extra.HasFlag(SongVersionFlags.Cover)
               || extra.HasFlag(SongVersionFlags.Remix)
               || extra.HasFlag(SongVersionFlags.Instrumental)
               || extra.HasFlag(SongVersionFlags.SpedUp)
               || extra.HasFlag(SongVersionFlags.Slowed)
               || extra.HasFlag(SongVersionFlags.Live);
    }

    /// <summary>Early-stop: very strong unique high-confidence hit.</summary>
    public static bool ShouldStopEarly(IReadOnlyList<QQSongCandidate> ranked, SongVersionFlags inputFlags)
    {
        if (ranked.Count == 0)
        {
            return false;
        }

        var top = ranked[0];
        if (top.MatchScore < 95 || top.Confidence != MatchConfidence.High)
        {
            return false;
        }

        if (HasHardVersionConflict(inputFlags, top.VersionFlags))
        {
            return false;
        }

        if (ranked.Count >= 2
            && top.MatchScore - ranked[1].MatchScore < 8
            && !SongMatcher.AreEquivalentRecordings(top, ranked[1]))
        {
            return false;
        }

        return Evaluate(ranked, inputFlags) == SearchResultQuality.Good;
    }
}
