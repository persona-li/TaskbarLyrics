using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Services;

internal static class MixedReleaseTrackChecks
{
    private const string Album = "HOT (English ver.) (feat. JADE)";
    private const string OriginalArtist = "LE SSERAFIM (르세라핌)";
    private const string EnglishTitle = "HOT (English ver.)(feat. JADE)";
    private const string EnglishArtists = "LE SSERAFIM / JADE";
    private const string PreferenceReason = "Exact album track";

    public static void Run(Action<bool, string> check)
    {
        var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));

        // This release contains both tracks. Its English/featured-artist album name
        // does not imply that the bare HOT track is English or features JADE.
        foreach (var order in new[]
                 {
                     new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
                     new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
                 })
        {
            var recorded = RecordedCandidates();
            var candidates = order.Select(index => recorded[index]).ToArray();
            for (var index = 0; index < candidates.Length; index++)
                candidates[index].Rank = index + 1;

            matcher.ScoreAll("HOT", OriginalArtist, 143, candidates, Album);
            var ranked = Rank(candidates);
            check(ranked[0].SongMid == "000xuQkh0Df42Y"
                  && ranked[0].Confidence == MatchConfidence.High,
                "Mixed-release HOT selects the exact bare track regardless of candidate order");
            check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionDetector.Detect("HOT", Album))
                  == SearchResultQuality.Good,
                "The exact bare HOT track passes automatic binding despite the album's English label");
            check(recorded[1].RejectReason.Contains(PreferenceReason, StringComparison.Ordinal),
                "The separately titled English collaboration loses the same-album near tie");
            check(!SongMatcher.AreEquivalentRecordings(recorded[0], recorded[1]),
                "The Korean and English HOT tracks must remain different recordings");
            check(!recorded[2].RejectReason.Contains(PreferenceReason, StringComparison.Ordinal),
                "The separate English-only release is not changed by same-album preference");

            var before = Snapshot(candidates);
            matcher.ScoreAll("HOT", OriginalArtist, 143, candidates, Album);
            check(before.SequenceEqual(Snapshot(candidates)),
                "Repeated scoring must not accumulate mixed-release track preference");
        }

        // Reverse the request: the explicit English collaboration must beat both
        // its bare-title album sibling and the English-only track on another release.
        // Featured names come from the requested track title, never from its album.
        foreach (var reversed in new[] { false, true })
        foreach (var queryArtist in new[] { EnglishArtists, "LE SSERAFIM", OriginalArtist })
        {
            var candidates = RecordedCandidates();
            if (reversed)
                Array.Reverse(candidates);
            matcher.ScoreAll(EnglishTitle, queryArtist, 143, candidates, Album);
            var ranked = Rank(candidates);
            check(ranked[0].SongMid == "000WTa9W2U2VDH"
                  && ranked[0].Confidence == MatchConfidence.High,
                "An explicit English HOT request selects the English collaboration");
            check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionDetector.Detect(EnglishTitle, Album))
                  == SearchResultQuality.Good,
                "An explicit English collaboration passes the existing automatic quality gate");
            check(candidates.Single(candidate => candidate.SongMid == "000xuQkh0Df42Y")
                    .RejectReason.Contains(PreferenceReason, StringComparison.Ordinal),
                "The bare-title album sibling does not override the explicit English request");
            check(candidates.Single(candidate => candidate.SongMid == "002Rdedb1JJwZy")
                    .RejectReason.Contains(PreferenceReason, StringComparison.Ordinal),
                "The English-only track does not substitute for the explicitly requested featured collaboration");
            var before = Snapshot(candidates);
            matcher.ScoreAll(EnglishTitle, queryArtist, 143, candidates, Album);
            check(before.SequenceEqual(Snapshot(candidates)),
                "Repeated English-version scoring remains stable");
        }

        foreach (var scenario in new[]
                 {
                     "missing-query-album", "missing-anchor-album", "missing-competitor-album",
                     "missing-query-duration", "missing-anchor-duration", "missing-competitor-duration",
                     "anchor-duration-3s", "competitor-duration-3s", "candidate-duration-gap-4s",
                     "missing-query-artist", "missing-anchor-artist", "different-competitor-artist",
                     "unknown-annotation", "unknown-with-language", "same-title-flags",
                     "different-title-core", "different-release", "non-exact-anchor-title", "no-anchor"
                 })
        {
            var recorded = RecordedCandidates();
            var anchor = recorded[0];
            var competitor = recorded[1];
            string? queryAlbum = Album;
            string? queryArtist = OriginalArtist;
            int? queryDuration = 143;
            switch (scenario)
            {
                case "missing-query-album": queryAlbum = null; break;
                case "missing-anchor-album": anchor.Album = ""; break;
                case "missing-competitor-album": competitor.Album = ""; break;
                case "missing-query-duration": queryDuration = null; break;
                case "missing-anchor-duration": anchor.DurationSeconds = 0; break;
                case "missing-competitor-duration": competitor.DurationSeconds = 0; break;
                case "anchor-duration-3s": anchor.DurationSeconds = 146; break;
                case "competitor-duration-3s": competitor.DurationSeconds = 146; break;
                case "candidate-duration-gap-4s":
                    anchor.DurationSeconds = 145;
                    competitor.DurationSeconds = 141;
                    break;
                case "missing-query-artist": queryArtist = null; break;
                case "missing-anchor-artist": anchor.Artists = ""; break;
                case "different-competitor-artist": competitor.Artists = "Other Artist / JADE"; break;
                case "unknown-annotation": competitor.Title = "HOT (Festival Edition)"; break;
                case "unknown-with-language": competitor.Title = "HOT (English ver.)(Festival Edition)"; break;
                case "same-title-flags": competitor.Title = "HOT (feat. JADE)"; break;
                case "different-title-core": competitor.Title = "HOT AIR (English ver.)(feat. JADE)"; break;
                case "different-release": competitor.Album = "HOT (English ver.)"; break;
                case "non-exact-anchor-title": anchor.Title = "熱 (HOT)"; break;
            }

            var candidates = scenario == "no-anchor" ? new[] { competitor } : new[] { anchor, competitor };
            matcher.ScoreAll("HOT", queryArtist, queryDuration, candidates, queryAlbum);
            check(candidates.All(candidate => !candidate.RejectReason.Contains(PreferenceReason, StringComparison.Ordinal)),
                "Exact album track preference needs every corroborating signal: " + scenario);
        }

        foreach (var durationPair in new[] { (145, 143), (143, 145), (141, 143), (143, 141) })
        {
            var candidates = RecordedCandidates().Take(2).ToArray();
            candidates[0].DurationSeconds = durationPair.Item1;
            candidates[1].DurationSeconds = durationPair.Item2;
            matcher.ScoreAll("HOT", OriginalArtist, 143, candidates, Album);
            check(candidates[1].RejectReason.Contains(PreferenceReason, StringComparison.Ordinal)
                  && candidates[0].Confidence == MatchConfidence.High,
                "Two-second input and pairwise duration boundaries permit exact album track preference");
        }

        foreach (var scenario in new[]
                 {
                     "album-feature-only", "missing-query-album", "missing-query-duration",
                     "missing-anchor-feature", "unknown-artist", "title-declares-feature",
                     "artist-declares-feature", "unknown-album-annotation", "different-title-core",
                     "featured-name-substring", "duration-3s"
                 })
        {
            var recorded = RecordedCandidates();
            var anchor = recorded[1];
            var competitor = recorded[2];
            var queryTitle = EnglishTitle;
            string? queryAlbum = Album;
            int? queryDuration = 143;
            switch (scenario)
            {
                case "album-feature-only": queryTitle = "HOT (English ver.)"; break;
                case "missing-query-album": queryAlbum = null; break;
                case "missing-query-duration": queryDuration = null; break;
                case "missing-anchor-feature": anchor.Artists = "LE SSERAFIM"; break;
                case "unknown-artist": competitor.Artists = "LE SSERAFIM / Other Guest"; break;
                case "title-declares-feature": competitor.Title = EnglishTitle; break;
                case "artist-declares-feature": competitor.Artists = EnglishArtists; break;
                case "unknown-album-annotation": competitor.Album = "HOT (English ver.) (Deluxe)"; break;
                case "different-title-core": competitor.Title = "HOT AIR (English ver.)"; break;
                case "featured-name-substring": competitor.Artists = "LE SSERAFIM / JADEX"; break;
                case "duration-3s": competitor.DurationSeconds = 146; break;
            }

            matcher.ScoreAll(queryTitle, "LE SSERAFIM", queryDuration, new[] { anchor, competitor }, queryAlbum);
            check(!competitor.RejectReason.Contains(PreferenceReason, StringComparison.Ordinal),
                "Featured-track preference requires explicit missing credits and release corroboration: " + scenario);
        }

        // A high fuzzy album score does not establish the same release. A single
        // changed year in a long, otherwise identical edition name exceeds 98.
        const string edition2025 = " (Archive Collection With Original Studio Recordings From The Complete International Release Catalogue 2025)";
        const string edition2026 = " (Archive Collection With Original Studio Recordings From The Complete International Release Catalogue 2026)";
        var requestedAlbum = Album + edition2025;
        var changedAlbum = Album + edition2026;
        check(matcher.ScoreAlbum(requestedAlbum, changedAlbum) >= 98,
            "The edition-year fixture exercises a fuzzy album match above the old preference threshold");
        foreach (var featureBranch in new[] { false, true })
        foreach (var anchorMismatch in new[] { false, true })
        {
            var recorded = RecordedCandidates();
            var anchor = featureBranch ? recorded[1] : recorded[0];
            var competitor = featureBranch ? recorded[2] : recorded[1];
            anchor.Album = anchorMismatch ? changedAlbum : requestedAlbum;
            competitor.Album = (featureBranch ? "HOT (English ver.)" : Album)
                + (anchorMismatch ? edition2025 : edition2026);
            var queryTitle = featureBranch ? EnglishTitle : "HOT";
            var queryArtist = featureBranch ? EnglishArtists : OriginalArtist;

            if (featureBranch && !anchorMismatch)
            {
                check(matcher.ScoreAlbum(requestedAlbum, competitor.Album) < 98,
                    "The featured edition-year fixture reaches the comparison after removing featured annotations");
                check(matcher.ScoreAlbum("HOT (English ver.)" + edition2025, competitor.Album) >= 98,
                    "Removing featured annotations still leaves the different edition year above the fuzzy threshold");
            }

            matcher.ScoreAll(queryTitle, queryArtist, 143, new[] { anchor, competitor }, requestedAlbum);
            check(!competitor.RejectReason.Contains(PreferenceReason, StringComparison.Ordinal),
                "A different album edition cannot trigger exact track preference: "
                + (featureBranch ? "featured-credit" : "language")
                + (anchorMismatch ? " anchor mismatch" : " competitor mismatch"));
        }
    }

    private static QQSongCandidate[] RecordedCandidates() =>
    [
        new()
        {
            SongMid = "000xuQkh0Df42Y", Title = "HOT", Artists = OriginalArtist,
            Album = Album, DurationSeconds = 143, Rank = 1
        },
        new()
        {
            SongMid = "000WTa9W2U2VDH", Title = EnglishTitle, Artists = EnglishArtists,
            Album = Album, DurationSeconds = 143, Rank = 2
        },
        new()
        {
            SongMid = "002Rdedb1JJwZy", Title = "HOT (English ver.)", Artists = "LE SSERAFIM",
            Album = "HOT (English ver.)", DurationSeconds = 143, Rank = 3
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
