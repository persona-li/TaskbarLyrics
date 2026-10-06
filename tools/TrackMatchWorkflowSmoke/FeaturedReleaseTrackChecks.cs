using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Services;

internal static class FeaturedReleaseTrackChecks
{
    private const string PreferenceReason = "Exact album track";

    public static void Run(Action<bool, string> check)
    {
        var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
        foreach (var synthetic in new[] { false, true })
        foreach (var collaboration in new[] { false, true })
        foreach (var reversed in new[] { false, true })
        {
            var candidates = Candidates(synthetic);
            var requested = candidates[collaboration ? 1 : 0];
            var other = candidates[collaboration ? 0 : 1];
            var artist = requested.Artists.Replace("BTS", "BTS (防弹少年团)");
            if (reversed) Array.Reverse(candidates);
            for (var i = 0; i < candidates.Length; i++) candidates[i].Rank = i + 1;
            matcher.ScoreAll(requested.Title, artist, 164, candidates, requested.Album);
            var ranked = Rank(candidates);
            check(ranked[0] == requested && requested.Confidence == MatchConfidence.High,
                "An exact release selects the original or collaboration in either candidate order");
            check(other.RejectReason.Contains(PreferenceReason, StringComparison.Ordinal)
                  && requested.MatchScore - other.MatchScore >= 5,
                "Corroborated release and complete artist credits resolve the near tie");
            check(SearchResultQualityEvaluator.Evaluate(ranked,
                      SongVersionDetector.Detect(requested.Title, requested.Album)) == SearchResultQuality.Good,
                "Featured-release preference passes the existing automatic binding quality gate");
            check(!SongMatcher.AreEquivalentRecordings(requested, other),
                "The original and featured edition remain distinct recordings");
            var before = Snapshot(candidates);
            matcher.ScoreAll(requested.Title, artist, 164, candidates, requested.Album);
            check(before.SequenceEqual(Snapshot(candidates)),
                "Featured-release preference remains stable across repeated scoring");
        }

        // Captured from the user's 2026-10-02 search. With no preference, QQ's
        // shared-artist scoring put the two recordings only 1.5 points apart.
        var recorded = Candidates();
        foreach (var candidate in recorded)
            matcher.ScoreInto("Butter", "BTS (防弹少年团)", 164, "Butter",
                SongVersionFlags.None, candidate);
        check(recorded[0].MatchScore == 99.4 && recorded[1].MatchScore == 97.9,
            "Recorded Butter fixture reproduces the original 99.4 versus 97.9 near tie");
        matcher.ScoreAll("Butter", "BTS (防弹少年团)", 164, recorded, "Butter");
        check(recorded[0].MatchScore == 99.4 && recorded[1].MatchScore == 93.4
              && recorded[0].Confidence == MatchConfidence.High,
            "Butter original remains at 99.4 and the corroborated alternative becomes 93.4");

        foreach (var scenario in new[]
                 {
                     "missing-query-album", "missing-anchor-album", "missing-competitor-album",
                     "missing-query-duration", "missing-anchor-duration", "missing-competitor-duration",
                     "anchor-duration-3s", "competitor-duration-3s", "duration-20s", "duration-gap-4s",
                     "missing-query-artist", "missing-anchor-artist", "missing-competitor-artist",
                     "different-artist", "same-artists", "no-anchor", "different-release",
                     "unknown-edition", "unknown-anchor-edition", "fuzzy-edition-year",
                     "wrong-feature-credit", "credit-name-substring", "uncredited-extra-artist",
                     "missing-feature-label", "unknown-title-annotation", "different-title",
                     "explicit-feature-request", "explicit-language-request", "album-feature-only",
                     "unknown-query-credit-annotation", "unknown-candidate-credit-annotation",
                     "unknown-anchor-credit-annotation"
                 })
        {
            var candidates = Candidates();
            var anchor = candidates[0];
            var competitor = candidates[1];
            string title = "Butter";
            string? artist = "BTS (防弹少年团)";
            string? album = "Butter";
            int? duration = 164;
            switch (scenario)
            {
                case "missing-query-album": album = null; break;
                case "missing-anchor-album": anchor.Album = ""; break;
                case "missing-competitor-album": competitor.Album = ""; break;
                case "missing-query-duration": duration = null; break;
                case "missing-anchor-duration": anchor.DurationSeconds = 0; break;
                case "missing-competitor-duration": competitor.DurationSeconds = 0; break;
                case "anchor-duration-3s": anchor.DurationSeconds = 167; break;
                case "competitor-duration-3s": competitor.DurationSeconds = 167; break;
                case "duration-20s": duration = 184; break;
                case "duration-gap-4s": anchor.DurationSeconds = 166; competitor.DurationSeconds = 162; break;
                case "missing-query-artist": artist = null; break;
                case "missing-anchor-artist": anchor.Artists = ""; break;
                case "missing-competitor-artist": competitor.Artists = ""; break;
                case "different-artist": competitor.Artists = "Another Artist / Megan Thee Stallion"; break;
                case "same-artists": competitor.Artists = "BTS"; break;
                case "no-anchor": candidates = [competitor]; break;
                case "different-release": competitor.Album = "Another Single (feat. Megan Thee Stallion)"; break;
                case "unknown-edition": competitor.Album += " (Festival Edition)"; break;
                case "unknown-anchor-edition": anchor.Album += " (Festival Edition)"; break;
                case "fuzzy-edition-year":
                    const string stem = " (Archive Collection With Original Studio Recordings From The Complete International Release Catalogue ";
                    album += stem + "2025)";
                    anchor.Album = album;
                    competitor.Album += stem + "2026)";
                    break;
                case "wrong-feature-credit": competitor.Album = "Butter (feat. Someone Else)"; break;
                case "credit-name-substring": competitor.Album = "Butter (feat. Megan Thee StallionX)"; break;
                case "uncredited-extra-artist": competitor.Artists += " / Another Guest"; break;
                case "missing-feature-label": competitor.Album = "Butter (Megan Thee Stallion)"; break;
                case "unknown-title-annotation": competitor.Title += " (Radio Edit)"; break;
                case "different-title": competitor.Title = "Butterflies"; break;
                case "explicit-feature-request": title += " (feat. Megan Thee Stallion)"; break;
                case "explicit-language-request": title += " (Japanese ver.)"; break;
                case "album-feature-only": album = competitor.Album; anchor.Album = album; break;
                case "unknown-query-credit-annotation": artist = "BTS (with Another Guest)"; break;
                case "unknown-candidate-credit-annotation": competitor.Artists += " (with Another Guest)"; break;
                case "unknown-anchor-credit-annotation": anchor.Artists += " (with Another Guest)"; break;
            }
            matcher.ScoreAll(title, artist, duration, candidates, album);
            check(!competitor.RejectReason.Contains(PreferenceReason, StringComparison.Ordinal),
                "Featured-release preference requires all corroborating signals: " + scenario);
            if (scenario is "missing-query-album" or "missing-query-duration")
                check(SearchResultQualityEvaluator.Evaluate(Rank(candidates),
                          SongVersionDetector.Detect(title, album)) != SearchResultQuality.Good,
                    "Missing album or trustworthy duration preserves automatic-match ambiguity");
        }

        foreach (var durations in new[] { (166, 164), (164, 166), (162, 164), (164, 162) })
        {
            var candidates = Candidates();
            candidates[0].DurationSeconds = durations.Item1;
            candidates[1].DurationSeconds = durations.Item2;
            matcher.ScoreAll("Butter", "BTS (防弹少年团)", 164, candidates, "Butter");
            check(candidates[0].Confidence == MatchConfidence.High
                  && candidates[1].RejectReason.Contains(PreferenceReason, StringComparison.Ordinal),
                "The inclusive two-second input and candidate-duration boundary remains supported");
        }
    }

    private static QQSongCandidate[] Candidates(bool synthetic = false) =>
    [
        new()
        {
            SongMid = synthetic ? "original" : "001wepyO0z7U9l", Title = synthetic ? "Evening Signal" : "Butter",
            Artists = synthetic ? "North / South" : "BTS", Album = synthetic ? "Evening Signal" : "Butter",
            DurationSeconds = 164, Rank = 1
        },
        new()
        {
            SongMid = synthetic ? "featured" : "004f2tCx16CCOy", Title = synthetic ? "Evening Signal" : "Butter",
            Artists = synthetic ? "South / North / East / West" : "BTS / Megan Thee Stallion",
            Album = synthetic ? "Evening Signal (ft. East & West)" : "Butter (feat. Megan Thee Stallion)",
            DurationSeconds = 164, Rank = 2
        }
    ];

    private static QQSongCandidate[] Rank(IEnumerable<QQSongCandidate> candidates) =>
        candidates.OrderByDescending(candidate => candidate.MatchScore).ThenBy(candidate => candidate.Rank).ToArray();

    private static (string Mid, double Score, MatchConfidence Confidence, string Reason)[] Snapshot(
        IEnumerable<QQSongCandidate> candidates) =>
        candidates.OrderBy(candidate => candidate.SongMid, StringComparer.Ordinal)
            .Select(candidate => (candidate.SongMid, candidate.MatchScore, candidate.Confidence, candidate.RejectReason))
            .ToArray();
}
