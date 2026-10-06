using TaskbarLyrics.App.Services;
using TaskbarLyrics.Cache;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Services;

internal static class CachedVersionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var track = new TrackIdentity("Some", "脸红的思春期 (볼빨간사춘기)", TimeSpan.FromSeconds(184), "私の思春期へ/Some");
        var mismatched = Resolve(track, "Red Diary Page.1");
        check(mismatched.Search.Calls == 1 && !mismatched.Result.FromCache && mismatched.Result.Song?.SongMid == "fresh",
            "Some in the Japanese album re-searches an automatic Korean-album cache entry");
        check(mismatched.Search.Album == track.Album && mismatched.Store.Saves == 1,
            "Album context reaches re-search and the verified replacement is saved");

        var manual = Resolve(track, "Red Diary Page.1", source: nameof(MatchSourceKind.Manual));
        check(manual.Search.Calls == 0 && manual.Store.Saves == 0 && manual.Result.FromCache && manual.Result.Song?.SongMid == "cached",
            "An explicit manual binding survives album disagreement without automatic replacement");
        var same = Resolve(track, track.Album!);
        check(same.Search.Calls == 0 && same.Store.Saves == 0 && same.Result.FromCache,
            "An automatic binding for the same album stays a fast cache hit");
        var duplicate = Resolve(track, "私の思春期へ/Some (私の思春期へ/Some)");
        check(duplicate.Search.Calls == 0 && duplicate.Result.FromCache,
            "Equivalent duplicated album annotations preserve the cache hit");
        var missingAlbum = Resolve(track, "");
        check(missingAlbum.Search.Calls == 1, "A cache entry without album cannot attest a requested release");
        var noInputAlbum = Resolve(track with { Album = null }, "Red Diary Page.1");
        check(noInputAlbum.Search.Calls == 0 && noInputAlbum.Result.FromCache,
            "Missing input album alone does not invalidate an otherwise unmarked cache entry");

        foreach (var language in new[] { "Japanese", "Chinese", "English", "Korean" })
        {
            var explicitTrack = track with { Title = $"Some ({language} Version)", Album = null };
            check(Resolve(explicitTrack, "", cachedTitle: "Some").Search.Calls == 1,
                $"A requested {language} version cannot reuse an unmarked automatic entry");
            check(Resolve(track with { Album = null }, "", cachedTitle: explicitTrack.Title).Search.Calls == 1,
                $"An unmarked request cannot reuse an explicit {language} automatic entry");
            check(Resolve(explicitTrack, "", cachedTitle: explicitTrack.Title).Result.FromCache,
                $"Matching {language} markers retain automatic cache reuse");
        }

        var uncertain = Resolve(track, "Red Diary Page.1", acceptSearch: false);
        check(uncertain.Result.Kind == TrackMatchResolutionKind.ManualRequired && uncertain.Store.Saves == 0,
            "Rejected replacement does not fall back to the conflicting cached version or persist uncertainty");
    }

    private static (TrackMatchResolution Result, Lookup Store, Search Search) Resolve(
        TrackIdentity track, string cachedAlbum, string cachedTitle = "Some",
        string source = nameof(MatchSourceKind.Automatic), bool acceptSearch = true)
    {
        var store = new Lookup(new TrackLookupEntry
        {
            SongMid = "cached", Title = cachedTitle, Artists = track.Artist, Album = cachedAlbum,
            MatchedDurationSeconds = 184, MatchSource = source, MatchScore = 87.09
        });
        var search = new Search(acceptSearch);
        var result = new TrackMatchResolver(store, search).ResolveAsync(track, CancellationToken.None).GetAwaiter().GetResult();
        return (result, store, search);
    }

    private sealed class Lookup(TrackLookupEntry entry) : ITrackLookupStore
    {
        public int Saves { get; private set; }
        public TrackLookupEntry? TryGet(string title, string artist, int? durationSeconds) => entry;
        public void SaveAutomatic(string title, string artist, int? durationSeconds, QQSongIdentity song, double? matchScore) => Saves++;
    }

    private sealed class Search(bool accept) : IAutomaticSongSearch
    {
        public int Calls { get; private set; }
        public string? Album { get; private set; }
        public Task<AutoMatchDecision> AutoMatchAsync(string title, string? artist, int? durationSeconds, string? album = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Album = album;
            return Task.FromResult(new AutoMatchDecision
            {
                Confidence = accept ? MatchConfidence.High : MatchConfidence.Low,
                Best = new QQSongCandidate { SongMid = "fresh", Title = title, Artists = artist ?? "", Album = album ?? "", DurationSeconds = durationSeconds ?? 0, MatchScore = 100 }
            });
        }
    }
}
