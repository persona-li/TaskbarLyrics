using TaskbarLyrics.App.Services;
using TaskbarLyrics.Cache;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Services;

var failures = new List<string>();

await AutoSearchesAndPersistsNewTrackAsync();
await ExistingManualBindingWinsAsync();
await AmbiguousSearchRequiresManualChoiceAsync();
await ConfirmedManualBindingIsImmediatelyResolvableAsync();
await ConfirmedSelectionLoadsImmediatelyAsync(false);
await ConfirmedSelectionLoadsImmediatelyAsync(true);
LateRequestsCannotCancelManualLoad();
RequestCompletionAndCancellationDoNotCrossOwners();
CancellationCallbacksCannotBreakReplacement();
await AutomaticBindingWithoutLyricsIsRetriedAsync();
InheritedPreviousDurationIsIgnoredForNewTrack();
KickItMatchesAfterInheritedDurationIsIgnored();
EquivalentMultilingualLoveShotEntriesDoNotCauseAmbiguity();
MultilingualParentheticalOrderMatchesAutomatically();
StrongIdentityConsensusAutoMatchesCheerUp();
ExactMetadataConsensusIgnoresBrokenGsmtcDurationForRidin();
AnnotatedArtistAndDuplicateAlbumAutoMatchNiceBody();
CompleteArtistPreferenceScenarios();
NestedDuplicateAlbumMatchesGoldDust();
AlbumAndLanguageVersionsMustAgree();
CorroboratedLimitlessRecordingWins();
KnownAlbumChecks.Run(Check);
MixedReleaseTrackChecks.Run(Check);
FeaturedReleaseTrackChecks.Run(Check);

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Track match workflow smoke failed ({failures.Count}):");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine("- " + failure);
    }

    return 1;
}

Console.WriteLine("Track match workflow smoke passed (17 existing scenarios + complete-artist and nested-album regression matrices).");
return 0;

async Task AutoSearchesAndPersistsNewTrackAsync()
{
    var cache = CreateCache("automatic");
    var candidate = Candidate("auto-mid", "新歌", "歌手", MatchConfidence.High, 97);
    var search = new FakeSearch(new AutoMatchDecision
    {
        Best = candidate,
        Confidence = MatchConfidence.High,
        ResultQuality = SearchResultQuality.Good,
        RankedCandidates = new[] { candidate },
        UniqueCount = 1,
        RawCount = 1,
        Reason = "High confidence."
    });
    var resolver = new TrackMatchResolver(cache, search);
    var track = Track("新歌", "歌手");

    var first = await resolver.ResolveAsync(track, CancellationToken.None);
    Check(first.Kind == TrackMatchResolutionKind.Bound, "new track should bind automatically");
    Check(first.MatchSource == nameof(MatchSourceKind.Automatic), "new track source should be Automatic");
    Check(first.Song?.SongMid == "auto-mid", "new track should use searched SongMid");
    Check(search.CallCount == 1, "cache miss should invoke automatic search exactly once");

    var second = await resolver.ResolveAsync(track, CancellationToken.None);
    Check(second.Song?.SongMid == "auto-mid", "automatic binding should be reusable immediately");
    Check(search.CallCount == 1, "persisted automatic binding should prevent a second search");
}

async Task ExistingManualBindingWinsAsync()
{
    var cache = CreateCache("manual-wins");
    var track = Track("同名歌曲", "原歌手");
    var manual = Identity("manual-mid", "手选版本", "原歌手");
    cache.SaveManual(track.Title, track.Artist, track.DurationSeconds, manual);
    cache.SaveAutomatic(
        track.Title,
        track.Artist,
        track.DurationSeconds,
        Identity("wrong-auto-mid", "自动版本", "其他歌手"),
        99);

    var search = new FakeSearch(new AutoMatchDecision());
    var result = await new TrackMatchResolver(cache, search).ResolveAsync(track, CancellationToken.None);

    Check(result.Song?.SongMid == "manual-mid", "automatic save must not overwrite Manual binding");
    Check(result.MatchSource == nameof(MatchSourceKind.Manual), "manual binding source should be preserved");
    Check(!result.CanRetryAfterMissingLyrics, "Manual binding without lyrics must not be auto-replaced");
    Check(search.CallCount == 0, "manual binding should bypass automatic search");
}

async Task AmbiguousSearchRequiresManualChoiceAsync()
{
    var cache = CreateCache("ambiguous");
    var candidate = Candidate("maybe-mid", "相似歌曲", "歌手", MatchConfidence.Medium, 82);
    var search = new FakeSearch(new AutoMatchDecision
    {
        Best = candidate,
        Confidence = MatchConfidence.Medium,
        ResultQuality = SearchResultQuality.Ambiguous,
        RankedCandidates = new[] { candidate },
        UniqueCount = 2,
        RawCount = 2,
        Reason = "Ambiguous candidates."
    });
    var track = Track("歌曲", "歌手");

    var result = await new TrackMatchResolver(cache, search).ResolveAsync(track, CancellationToken.None);

    Check(result.Kind == TrackMatchResolutionKind.ManualRequired, "ambiguous result must require manual choice");
    Check(cache.TryGet(track.Title, track.Artist, track.DurationSeconds) is null,
        "ambiguous result must not persist an automatic binding");
}

async Task ConfirmedManualBindingIsImmediatelyResolvableAsync()
{
    var cache = CreateCache("manual-confirm");
    var track = Track("待确认歌曲", "歌手");
    cache.SaveManual(
        track.Title,
        track.Artist,
        track.DurationSeconds,
        Identity("confirmed-mid", "确认版本", "歌手"));
    var search = new FakeSearch(new AutoMatchDecision());

    var result = await new TrackMatchResolver(cache, search).ResolveAsync(track, CancellationToken.None);

    Check(result.Kind == TrackMatchResolutionKind.Bound, "confirmed manual binding should resolve for immediate load");
    Check(result.Song?.SongMid == "confirmed-mid", "immediate load should use the confirmed version");
    Check(result.MatchSource == nameof(MatchSourceKind.Manual), "confirmed binding should remain Manual");
    Check(search.CallCount == 0, "confirmed manual binding must not trigger automatic search");
}

async Task ConfirmedSelectionLoadsImmediatelyAsync(bool cachedLyrics)
{
    var cache = CreateCache("confirmed-direct");
    var track = Track("当前歌曲", "歌手");
    // An older automatic result must not divert the explicit user selection.
    cache.SaveAutomatic(track.Title, track.Artist, track.DurationSeconds, Identity("old-mid", "旧版本", "歌手"), 99);
    var search = new FakeSearch(new AutoMatchDecision());
    var resolver = new TrackMatchResolver(cache, search);
    using var requests = new LyricsLoadRequests();
    using var old = requests.Begin(1, 1, CancellationToken.None)!;
    using var manual = requests.Begin(2, 2, CancellationToken.None)!;
    var selected = Identity("selected-mid", "手选版本", "歌手");
    var result = await resolver.ResolveAsync(track, manual.Token, confirmedSelection: selected);
    var network = new TaskCompletionSource<string>();
    string? visibleLyrics = null;
    async Task LoadAndDisplayAsync()
    {
        var lyric = cachedLyrics ? "所选版本的缓存歌词" : await network.Task;
        manual.Token.ThrowIfCancellationRequested();
        visibleLyrics = result.Song!.SongMid + ":" + lyric;
    }
    var apply = LoadAndDisplayAsync();
    if (!cachedLyrics) network.SetResult("所选版本的在线歌词");
    await apply;
    Check(old.Token.IsCancellationRequested, "manual confirmation retires old automatic load");
    Check(!manual.Token.IsCancellationRequested, "manual load owns a fresh token");
    Check(result.MatchSource == nameof(MatchSourceKind.Manual) && !result.CanRetryAfterMissingLyrics, "confirmed identity stays Manual");
    Check(search.CallCount == 0, "manual confirmation never auto-searches the old song");
    Check(visibleLyrics?.StartsWith("selected-mid:") == true, "one confirmation produces visible selected lyrics without Reload (cache/network)");
}

void LateRequestsCannotCancelManualLoad()
{
    using var requests = new LyricsLoadRequests();
    using var manual = requests.Begin(5, 5, CancellationToken.None)!;
    using var stale = requests.Begin(4, 5, CancellationToken.None);
    Check(stale is null, "stale queued load is rejected before replacing active token");
    Check(!manual.Token.IsCancellationRequested, "late automatic load cannot cancel the confirmed manual load");
}

void RequestCompletionAndCancellationDoNotCrossOwners()
{
    using var requests = new LyricsLoadRequests();
    var old = requests.Begin(6, 6, CancellationToken.None)!;
    using var current = requests.Begin(7, 7, CancellationToken.None)!;
    old.Dispose();
    Check(!current.Token.IsCancellationRequested, "retired owner's completion does not dispose active token");
    requests.Cancel();
    Check(current.Token.IsCancellationRequested, "active request still cancels after old owner finishes");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Check(requests.Begin(8,8,cancelled.Token) is null, "application shutdown cannot start a new load");
}

void CancellationCallbacksCannotBreakReplacement()
{
    using var requests = new LyricsLoadRequests();
    var previous = requests.Begin(8,8,CancellationToken.None)!;
    using var completion = previous.Token.Register(previous.Dispose);
    using var throwing = previous.Token.Register(() => throw new InvalidOperationException("retired callback"));
    using var current = requests.Begin(9,9,CancellationToken.None)!;
    Check(!current.Token.IsCancellationRequested, "replacement survives completion/throwing callbacks during cancellation");
}

async Task AutomaticBindingWithoutLyricsIsRetriedAsync()
{
    var cache = CreateCache("automatic-without-lyrics");
    var track = Track("曾经匹配错误的歌曲", "歌手");
    cache.SaveAutomatic(
        track.Title,
        track.Artist,
        track.DurationSeconds,
        Identity("old-no-lyrics-mid", "无歌词版本", "歌手"),
        96);
    var replacement = Candidate("new-lyrics-mid", "正确版本", "歌手", MatchConfidence.High, 98);
    var search = new FakeSearch(new AutoMatchDecision
    {
        Best = replacement,
        Confidence = MatchConfidence.High,
        ResultQuality = SearchResultQuality.Good,
        RankedCandidates = new[] { replacement },
        UniqueCount = 1,
        RawCount = 1,
        Reason = "High confidence."
    });

    var resolver = new TrackMatchResolver(cache, search);
    var initial = await resolver.ResolveAsync(track, CancellationToken.None);
    Check(initial.CanRetryAfterMissingLyrics, "cached Automatic binding should be retryable after missing lyrics");

    var result = await resolver.ResolveAsync(
        track,
        CancellationToken.None,
        retryAutomaticBinding: true);

    Check(search.CallCount == 1, "Automatic binding without lyrics should trigger a fresh search");
    Check(result.Song?.SongMid == "new-lyrics-mid", "fresh search should replace the stale Automatic binding");
    Check(result.MatchSource == nameof(MatchSourceKind.Automatic), "retried binding should remain Automatic");
}

void MultilingualParentheticalOrderMatchesAutomatically()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    var candidates = new List<QQSongCandidate>
    {
        new()
        {
            SongMid = "short-wrong-mid",
            Title = "너에게 닿기를 (I Wish)",
            Artists = "宇宙少女",
            Album = string.Empty,
            DurationSeconds = 37,
            Rank = 1
        },
        new()
        {
            SongMid = "correct-mid",
            Title = "너에게 닿기를 (靠近你的心) (I Wish)",
            Artists = "WJSN (宇宙少女) (우주소녀)",
            Album = "From. 우주소녀",
            DurationSeconds = 218,
            Rank = 2
        }
    };

    matcher.ScoreAll(
        "너에게 닿기를 (I Wish) (靠近你的心)",
        "WJSN (宇宙少女) (우주소녀)",
        239,
        candidates,
        "From. 우주소녀");

    var best = candidates.OrderByDescending(c => c.MatchScore).First();
    Check(best.SongMid == "correct-mid", "reordered multilingual title aliases should rank the correct song first");
    Check(best.Confidence == MatchConfidence.High,
        "exact title parts, artist and album should overcome a <=30s GSMTC duration discrepancy");
}

void StrongIdentityConsensusAutoMatchesCheerUp()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    var candidates = new List<QQSongCandidate>
    {
        new()
        {
            SongMid = "004H8CP93KDaHi",
            Title = "CHEER UP (Korean Ver.)",
            Artists = "TWICE (트와이스)",
            Album = "PAGE TWO (page two)",
            DurationSeconds = 208,
            Rank = 1
        },
        new()
        {
            SongMid = "004CFtDx258bS9",
            Title = "SIGNAL (Korean Ver.)",
            Artists = "TWICE (트와이스)",
            Album = "SIGNAL (signal)",
            DurationSeconds = 196,
            Rank = 2
        }
    };

    matcher.ScoreAll(
        "CHEER UP (Korean Ver.)",
        "TWICE (트와이스)",
        225,
        candidates,
        "PAGE TWO");

    var ranked = candidates.OrderByDescending(c => c.MatchScore).ToList();
    Check(ranked[0].SongMid == "004H8CP93KDaHi", "CHEER UP should remain the top-ranked candidate");
    Check(ranked[0].Confidence == MatchConfidence.High,
        "exact title/artist and strong album consensus should be High despite a tolerable duration discrepancy");
    Check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionDetector.Detect("CHEER UP (Korean Ver.)")) == SearchResultQuality.Good,
        "strong identity consensus with the requested Korean version should be eligible for automatic binding");
}

void ExactMetadataConsensusIgnoresBrokenGsmtcDurationForRidin()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    var candidates = new List<QQSongCandidate>
    {
        new()
        {
            SongMid = "004VXk3R1RDw4F",
            Title = "Ridin'",
            Artists = "NCT DREAM (엔시티 드림)",
            Album = "Reload",
            DurationSeconds = 201,
            Rank = 1
        },
        new()
        {
            SongMid = "004Ltju10IFZIF",
            Title = "Ridin' (Live)",
            Artists = "NCT DREAM (엔시티 드림)",
            Album = "SMTOWN LIVE Culture Humanity",
            DurationSeconds = 208,
            Rank = 2
        },
        new()
        {
            SongMid = "001JFcpK34g5qJ",
            Title = "Ridin'",
            Artists = "NLSN",
            Album = "Ridin'",
            DurationSeconds = 124,
            Rank = 3
        }
    };

    matcher.ScoreAll(
        "Ridin'",
        "NCT DREAM (엔시티 드림)",
        274,
        candidates,
        "Reload");

    var ranked = candidates.OrderByDescending(c => c.MatchScore).ToList();
    Check(ranked[0].SongMid == "004VXk3R1RDw4F", "Ridin' standard version should rank first");
    Check(ranked[0].Confidence == MatchConfidence.High,
        "exact title/artist/album should override a broken GSMTC duration");
    Check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionFlags.None) == SearchResultQuality.Good,
        "exact metadata consensus should be eligible for automatic binding");
    Check(candidates.Single(c => c.SongMid == "004Ltju10IFZIF").Confidence != MatchConfidence.High,
        "Ridin' Live must remain blocked from automatic binding");
}

void AnnotatedArtistAndDuplicateAlbumAutoMatchNiceBody()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    var candidates = new List<QQSongCandidate>
    {
        new()
        {
            SongMid = "003H7Wiw23J0iv",
            Title = "Nice Body (With 로꼬)",
            Artists = "孝敏 / Loco",
            Album = "Make Up (make up)",
            DurationSeconds = 208,
            Rank = 1
        },
        new()
        {
            SongMid = "0012N0fg4b8FwG",
            Title = "Nice Body (伴奏)",
            Artists = "효민;로꼬",
            Album = string.Empty,
            DurationSeconds = 208,
            Rank = 2
        },
        new()
        {
            SongMid = "001cEBKe1wJQlY",
            Title = "Nice Body",
            Artists = "A酱",
            Album = string.Empty,
            DurationSeconds = 214,
            Rank = 3
        }
    };

    matcher.ScoreAll(
        "Nice Body (With 로꼬)",
        "孝敏 (효민)/Loco (로꼬)",
        243,
        candidates,
        "Make Up");

    var ranked = candidates.OrderByDescending(c => c.MatchScore).ToList();
    Check(ranked[0].SongMid == "003H7Wiw23J0iv", "Nice Body standard version should rank first");
    Check(ranked[0].Confidence == MatchConfidence.High,
        "annotated artist names and duplicate album annotations should form exact metadata consensus");
    Check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionFlags.None) == SearchResultQuality.Good,
        "Nice Body exact metadata consensus should be eligible for automatic binding");
    Check(candidates.Single(c => c.SongMid == "0012N0fg4b8FwG").Confidence != MatchConfidence.High,
        "Nice Body instrumental must remain blocked from automatic binding");
}

static FakeLookupStore CreateCache(string name) => new();

void InheritedPreviousDurationIsIgnoredForNewTrack()
{
    var previous = new TrackIdentity("SMILEY (Feat. BIBI)", "YENA/BIBI", TimeSpan.FromSeconds(173));
    var kickIt = new TrackIdentity(
        "영웅 (英雄; Kick It)",
        "NCT 127 (엔시티 127)",
        TimeSpan.FromSeconds(173),
        "NCT #127 Neo Zone – The 2nd Album");

    var sanitized = TrackMatchInputSanitizer.ForTrackChange(previous, kickIt);

    Check(sanitized.Duration is null,
        "new-track matching must ignore a duration inherited unchanged from the previous song");
    Check(sanitized.Title == kickIt.Title && sanitized.Artist == kickIt.Artist,
        "duration sanitizing must preserve the new track metadata");

    var legitimate = kickIt with { Duration = TimeSpan.FromSeconds(233) };
    Check(TrackMatchInputSanitizer.ForTrackChange(previous, legitimate).Duration == legitimate.Duration,
        "a changed duration must remain available to matching");
}

void KickItMatchesAfterInheritedDurationIsIgnored()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    var candidates = new List<QQSongCandidate>
    {
        new()
        {
            SongMid = "003Hg7vv2cHyIx",
            Title = "英雄;Kick It",
            Artists = "NCT 127 (엔시티 127)",
            Album = "LOVEHOLIC (loveholic)",
            DurationSeconds = 233,
            Rank = 1
        },
        new()
        {
            SongMid = "000qjaGA3C171u",
            Title = "서곡 (序曲; Prelude) +영웅 (英雄; Kick It) (网友改编)",
            Artists = "NCT 127",
            Album = string.Empty,
            DurationSeconds = 300,
            Rank = 2
        }
    };

    matcher.ScoreAll(
        "영웅 (英雄; Kick It)",
        "NCT 127 (엔시티 127)",
        durationSeconds: null,
        candidates,
        "NCT #127 Neo Zone – The 2nd Album");

    var ranked = candidates.OrderByDescending(c => c.MatchScore).ToList();
    Check(ranked[0].SongMid == "003Hg7vv2cHyIx", "Kick It standard candidate should rank first");
    Check(ranked[0].Confidence != MatchConfidence.High,
        "Ignoring inherited duration must not bypass a conflicting Kick It album");
    Check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionFlags.None) != SearchResultQuality.Good,
        "Kick It from LOVEHOLIC cannot stop search for the requested Neo Zone release");
}

void EquivalentMultilingualLoveShotEntriesDoNotCauseAmbiguity()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    var candidates = new List<QQSongCandidate>
    {
        new()
        {
            SongMid = "0023HEeH3xzJC3",
            Title = "Love Shot",
            Artists = "EXO (엑소)",
            Album = "LOVE SHOT – The 5th Album Repackage",
            DurationSeconds = 200,
            Rank = 1
        },
        new()
        {
            SongMid = "000020wX1JLzzV",
            Title = "宣告 (Love Shot)",
            Artists = "EXO",
            Album = "LOVE SHOT – The 5th Album Repackage",
            DurationSeconds = 200,
            Rank = 2
        },
        new()
        {
            SongMid = "japanese-version-mid",
            Title = "Love Shot (Japanese Ver.)",
            Artists = "EXO (엑소)",
            Album = "LOVE SHOT – The 5th Album Repackage",
            DurationSeconds = 200,
            Rank = 3
        }
    };

    matcher.ScoreAll(
        "Love Shot",
        "EXO (엑소)",
        200,
        candidates,
        "LOVE SHOT – The 5th Album Repackage");

    var ranked = candidates.OrderByDescending(c => c.MatchScore).ToList();
    Check(ranked[0].Confidence == MatchConfidence.High,
        "equivalent multilingual entries must not downgrade the exact Love Shot candidate");
    Check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionFlags.None) == SearchResultQuality.Good,
        "equivalent multilingual entries must not make Love Shot ambiguous");
    Check(!SongMatcher.AreEquivalentRecordings(ranked[0], candidates[2]),
        "an explicitly different language recording must remain distinct from the standard version");
}

void CompleteArtistPreferenceScenarios()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    QQSongCandidate Song(string id, string artists, int duration = 203, string album = "Future Nostalgia (Explicit)", string title = "Levitating") => new()
    {
        SongMid = id, Title = title, Artists = artists, Album = album,
        DurationSeconds = duration, Rank = id == "partial" ? 1 : 2
    };
    foreach (var reversed in new[] { false, true })
    foreach (var collaboration in new[] { false, true })
    {
        var query = collaboration ? "Dua Lipa / DaBaby" : "Dua Lipa";
        var exact = Song("exact", collaboration ? "dababy; DUA LIPA" : "DUA LIPA");
        var partial = Song("partial", collaboration ? "Dua Lipa" : "Dua Lipa / DaBaby");
        var songs = reversed ? new[] { exact, partial } : new[] { partial, exact };
        matcher.ScoreAll("Levitating", query, 210, songs, "Future Nostalgia (Explicit)");
        var ranked = songs.OrderByDescending(c => c.MatchScore).ThenBy(c => c.Rank).ToArray();
        Check(ranked[0] == exact && exact.Confidence == MatchConfidence.High,
            "complete artists must win independently of search order, case and artist order");
        Check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionFlags.None) == SearchResultQuality.Good,
            "Levitating exact artist preference must pass the automatic-binding quality gate");
        var scores = songs.Select(c => c.MatchScore).ToArray();
        matcher.ScoreAll("Levitating", query, 210, songs, "Future Nostalgia (Explicit)");
        Check(scores.SequenceEqual(songs.Select(c => c.MatchScore)), "rescoring must not accumulate artist penalties");
    }

    foreach (var scenario in new[] { "missing-album", "missing-duration", "missing-artist", "wrong-album",
        "different-duration", "unknown-candidate-duration", "version-conflict", "title-conflict", "stale-duration", "no-exact", "boundary-3s" })
    {
        var exact = Song("exact", scenario == "no-exact" ? "Dua Lipa / Guest" : "Dua Lipa",
            scenario == "different-duration" ? 220 : scenario == "boundary-3s" ? 206 : 203,
            scenario == "wrong-album" ? "Another album" : "Future Nostalgia (Explicit)",
            scenario == "version-conflict" ? "Levitating (Live)" : scenario == "title-conflict" ? "Another song" : "Levitating");
        var partial = Song("partial", "Dua Lipa / DaBaby", scenario == "unknown-candidate-duration" ? 0 : 203);
        var songs = new[] { partial, exact };
        matcher.ScoreAll("Levitating", scenario == "missing-artist" ? null : "Dua Lipa",
            scenario == "missing-duration" ? null : scenario == "stale-duration" ? 300 : 210,
            songs, scenario == "missing-album" ? null : "Future Nostalgia (Explicit)");
        Check(!partial.RejectReason.Contains("Complete artist list preferred"),
            "artist preference must not apply without corroboration: " + scenario);
    }

    var boundaryExact = Song("exact", "Dua Lipa", 205);
    var boundaryPartial = Song("partial", "Dua Lipa / DaBaby");
    matcher.ScoreAll("Levitating", "Dua Lipa", 212, new[] { boundaryPartial, boundaryExact }, "Future Nostalgia (Explicit)");
    Check(boundaryPartial.RejectReason.Contains("Complete artist list preferred"), "two-second candidate duration boundary is comparable");

    var onlyPartial = Song("partial", "Dua Lipa / DaBaby");
    matcher.ScoreAll("Levitating", "Dua Lipa", 210, new[] { onlyPartial }, "Future Nostalgia (Explicit)");
    Check(onlyPartial.Confidence == MatchConfidence.High && onlyPartial.MatchScore == 94,
        "a missing collaborator in player metadata alone must not penalize the only candidate");
}

static TrackIdentity Track(string title, string artist) =>
    new(title, artist, TimeSpan.FromSeconds(210), "专辑");

static QQSongIdentity Identity(string mid, string title, string artists) =>
    new("id-" + mid, mid, title, artists, "专辑", 210);

static QQSongCandidate Candidate(
    string mid,
    string title,
    string artists,
    MatchConfidence confidence,
    double score) => new()
    {
        SongId = "id-" + mid,
        SongMid = mid,
        Title = title,
        Artists = artists,
        Album = "专辑",
        DurationSeconds = 210,
        MatchScore = score,
        Confidence = confidence
    };

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

void NestedDuplicateAlbumMatchesGoldDust()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    const string album = "疾驰 (2 Baddies) - The 4th Album";
    var correct = new QQSongCandidate { SongMid="000B7cM22vEhtq", Title="波光 (Gold Dust)", Artists="NCT 127 (엔시티 127)", Album=album+" (疾驰 (2 baddies) - the 4th album)", DurationSeconds=249, Rank=1 };
    var clip = new QQSongCandidate { SongMid="000VTyg74YxvYW", Title=correct.Title, Artists="NCT 127", Album="", DurationSeconds=60, Rank=2 };
    matcher.ScoreAll(correct.Title, correct.Artists, 289, new[]{correct,clip}, album);
    Check(correct.AlbumScore>=98 && correct.Confidence==MatchConfidence.High, "Gold Dust nested duplicate album corroborates the correct recording despite GSMTC duration drift");
    Check(clip.Confidence!=MatchConfidence.High, "Gold Dust sixty-second clip without album consensus remains rejected for automatic binding");
    Check(SearchResultQualityEvaluator.Evaluate(new[]{correct,clip}.OrderByDescending(c=>c.MatchScore).ToList(), SongVersionFlags.None)==SearchResultQuality.Good, "Gold Dust recorded candidates produce an automatic match");
    foreach(var suffix in new[]{"Live", "Deluxe", "Remix", "Other Album", "疾驰 (2 Baddies) - The 3rd Album"})
        Check(matcher.ScoreAlbum(album,album+" ("+suffix+")")<95, "Different album annotation is not discarded: "+suffix);
    Check(matcher.ScoreAlbum("A (B)","A (B) (a (b))")>=98, "Nested duplicate annotation is case-insensitive after normalization");
    Check(matcher.ScoreAlbum("A [B]","A [B] [a [b]]")>=98, "Balanced square-bracket duplicate annotation matches");
    Check(matcher.ScoreAlbum("A (B)","A (B) (a (b])")<95, "Malformed mixed brackets are not collapsed");
    var live = new QQSongCandidate { SongMid="live", Title="波光 (Gold Dust) (Live)", Artists=correct.Artists, Album=correct.Album, DurationSeconds=249 };
    matcher.ScoreAll(correct.Title,correct.Artists,289,new[]{live},album);
    Check(live.VersionPenalty>0 && live.Confidence!=MatchConfidence.High, "Exact album normalization does not bypass version conflicts");
}
void AlbumAndLanguageVersionsMustAgree()
{
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk:false));
    const string artist = "脸红的思春期 (볼빨간사춘기)";
    var originalTrack = new TrackIdentity("Some", artist, TimeSpan.FromSeconds(184), "Red Diary Page.1");
    Check(TrackMatchInputSanitizer.IsDifferentTrack(originalTrack, originalTrack with { Album="私の思春期へ/Some" }),
        "Switching albums with identical title, artist and duration reloads the version");
    Check(!TrackMatchInputSanitizer.IsDifferentTrack(originalTrack, originalTrack with { Album=" RED DIARY PAGE.1 " }),
        "Cosmetic album differences do not reload the track");
    Check(!TrackMatchInputSanitizer.IsDifferentTrack(originalTrack, originalTrack with { Album=null }),
        "Transient missing album does not discard an established version");
    var korean = new QQSongCandidate { SongMid="000E9f1f303Wq8", Title="썸 탈꺼야 (Some)", Artists=artist, Album="Red Diary Page.1 (red diary page.1)", DurationSeconds=181 };
    var japanese = new QQSongCandidate { SongMid="002YEbUM4BB9mj", Title="Some", Artists=artist, Album="私の思春期へ/Some", DurationSeconds=184 };
    matcher.ScoreAll("Some",artist,184,new[]{korean},japanese.Album);
    Check(korean.Confidence!=MatchConfidence.High && SearchResultQualityEvaluator.Evaluate(new[]{korean},SongVersionFlags.None)!=SearchResultQuality.Good,
        "Some from a different album cannot stop search early despite matching title and artist");
    matcher.ScoreAll("Some",artist,184,new[]{korean,japanese},japanese.Album);
    Check(japanese.Confidence==MatchConfidence.High && japanese.MatchScore>korean.MatchScore,"Some prefers the requested Japanese release");
    foreach(var marker in new[]{"Japanese Ver.","Chinese Ver.","English Ver.","Korean Ver."})
    {
        var flags=SongVersionDetector.Detect("Some ("+marker+")");
        Check(flags!=SongVersionFlags.None,"Recognize explicit language: "+marker);
        Check(SearchResultQualityEvaluator.HasHardVersionConflict(flags,SongVersionFlags.None)
            && SearchResultQualityEvaluator.HasHardVersionConflict(SongVersionFlags.None,flags),"Language conflicts are bidirectional: "+marker);
        var plain=new QQSongCandidate { Title="Some",Artists=artist,Album="Album",DurationSeconds=184 };
        matcher.ScoreAll("Some",artist,184,new[]{plain},"Album ("+marker+")");
        Check(plain.Confidence!=MatchConfidence.High,"A requested language cannot bind an unmarked original: "+marker);
    }
}
void CorroboratedLimitlessRecordingWins()
{
    const string title = "無限的我 (无限的我) (무한적아; LIMITLESS)";
    const string album = "NCT #127 Limitless - The 2nd Mini Album";
    var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk: false));
    QQSongCandidate Song(string mid, string name, string release, int seconds) => new()
    {
        SongMid = mid, Title = name, Artists = "NCT 127 (엔시티 127)",
        Album = release, DurationSeconds = seconds
    };
    foreach (var reverse in new[] { false, true })
    {
        var correct = Song("004Ki2rr4ISl1g", title, album, 247);
        var incomplete = Song("002xhscJ0Euos8", "无限的我", "", 218);
        var candidates = new[] { correct, incomplete, Song("chain", "Limitless", "Chain", 247),
            Song("live", title + " (Live)", "NEO CITY : SEOUL – The Origin – The 1st Live Album", 278) };
        if (reverse) Array.Reverse(candidates);
        for (var pass = 0; pass < 2; pass++)
        {
            matcher.ScoreAll(title, "NCT 127", 247, candidates, album);
            var ranked = candidates.OrderByDescending(c => c.MatchScore).ToArray();
            Check(ranked[0] == correct && correct.Confidence == MatchConfidence.High,
                "Limitless complete recording wins independent of ordering/repeated scoring");
            Check(correct.MatchScore - incomplete.MatchScore >= 8,
                "Missing album and conflicting duration cannot create a false near tie");
            Check(SearchResultQualityEvaluator.Evaluate(ranked, SongVersionFlags.None) == SearchResultQuality.Good,
                "Limitless can auto-bind without relaxing version safeguards");
        }
    }
    foreach (var scenario in new[] { "noAlbum", "noDuration", "staleDuration", "fullCompetitor", "unknownDuration", "boundary20", "boundary21", "languageConflict" })
    {
        var correct = Song("correct", title, album, 247);
        var other = Song("other", "无限的我", scenario == "fullCompetitor" ? album : "",
            scenario == "unknownDuration" ? 0 : scenario == "boundary20" ? 227 : scenario == "boundary21" ? 226 : 218);
        int? duration = scenario == "noDuration" ? null : scenario == "staleDuration" ? 270 : 247;
        var queryAlbum = scenario == "noAlbum" ? null : album;
        var queryTitle = scenario == "languageConflict" ? title + " (Japanese Ver.)" : title;
        matcher.ScoreInto(queryTitle, "NCT 127", duration, queryAlbum,
            SongVersionDetector.Detect(queryTitle, queryAlbum), other);
        var before = other.MatchScore;
        matcher.ScoreAll(queryTitle, "NCT 127", duration, new[] { correct, other }, queryAlbum);
        Check(scenario == "boundary21" ? other.MatchScore < before : other.MatchScore == before,
            "Corroborating evidence boundaries: " + scenario);
        if (scenario == "fullCompetitor")
            Check(correct.Confidence != MatchConfidence.High, "Two complete competing recordings remain ambiguous");
    }
}

sealed class FakeSearch : IAutomaticSongSearch
{
    private readonly AutoMatchDecision _decision;

    public FakeSearch(AutoMatchDecision decision) => _decision = decision;

    public int CallCount { get; private set; }

    public Task<AutoMatchDecision> AutoMatchAsync(
        string title,
        string? artist,
        int? durationSeconds,
        string? album = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(_decision);
    }
}

sealed class FakeLookupStore : ITrackLookupStore
{
    private readonly Dictionary<string, TrackLookupEntry> _entries = new(StringComparer.Ordinal);

    public TrackLookupEntry? TryGet(string title, string artist, int? durationSeconds)
    {
        _entries.TryGetValue(Key(title, artist, durationSeconds), out var entry);
        return entry;
    }

    public void SaveAutomatic(
        string title,
        string artist,
        int? durationSeconds,
        QQSongIdentity song,
        double? matchScore)
    {
        var key = Key(title, artist, durationSeconds);
        if (_entries.TryGetValue(key, out var existing)
            && existing.MatchSource == nameof(MatchSourceKind.Manual))
        {
            return;
        }

        _entries[key] = Entry(song, MatchSourceKind.Automatic, matchScore);
    }

    public void SaveManual(
        string title,
        string artist,
        int? durationSeconds,
        QQSongIdentity song) =>
        _entries[Key(title, artist, durationSeconds)] = Entry(song, MatchSourceKind.Manual, null);

    private static TrackLookupEntry Entry(
        QQSongIdentity song,
        MatchSourceKind source,
        double? matchScore) => new()
    {
        SongId = song.SongId,
        SongMid = song.SongMid,
        Title = song.Title,
        Artists = song.Artists,
        Album = song.Album,
        MatchedDurationSeconds = song.DurationSeconds,
        MatchSource = source.ToString(),
        MatchScore = matchScore
    };

    private static string Key(string title, string artist, int? durationSeconds) =>
        $"{TrackIdentity.Normalize(title)}|{TrackIdentity.Normalize(artist)}|{durationSeconds?.ToString() ?? "na"}";
}
