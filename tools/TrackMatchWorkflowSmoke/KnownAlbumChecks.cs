using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Services;

internal static class KnownAlbumChecks
{
    public static void Run(Action<bool,string> check)
    {
        var matcher = new SongMatcher(new ArtistAliasStore(loadFromDisk:false));
        QQSongCandidate Make(string mid,string album,int seconds=169,string title="2.0") => new()
        { SongMid=mid,Title=title,Artists="BTS (防弹少年团)",Album=album,DurationSeconds=seconds };
        foreach(var reverse in new[]{false,true})
        {
            var blank=Make("002ANwiz3TQKQx","",171);
            var exact=Make("002Ha9JP1mFRf1","ARIRANG (Explicit Ver.)");
            var candidates=reverse ? new[]{exact,blank} : new[]{blank,exact};
            for(int i=0;i<candidates.Length;i++) candidates[i].Rank=i+1;
            matcher.ScoreAll("2.0","BTS (防弹少年团)",169,candidates,"ARIRANG (Explicit Ver.)");
            var ranked=candidates.OrderByDescending(c=>c.MatchScore).ToArray();
            check(ranked[0]==exact && exact.Confidence==MatchConfidence.High,
                "Recorded 2.0 exact album wins independently of provider order");
            check(SearchResultQualityEvaluator.Evaluate(ranked,SongVersionFlags.None)==SearchResultQuality.Good,
                "Known album preference passes automatic matching");
            var score=blank.MatchScore;
            matcher.ScoreAll("2.0","BTS (防弹少年团)",169,candidates,"ARIRANG (Explicit Ver.)");
            check(blank.MatchScore==score,"Rescoring does not accumulate missing-album penalties");
        }
        foreach(var queryAlbum in new string?[]{null,"Unrelated release"})
        {
            var blank=Make("blank","",171); var exact=Make("known","ARIRANG (Explicit Ver.)");
            matcher.ScoreAll("2.0","BTS",169,new[]{blank,exact},queryAlbum);
            check(!blank.RejectReason.Contains("Exact album match preferred"),"No album evidence does not trigger preference");
        }
        var identified=Make("other","Other album",171); var known=Make("known","ARIRANG (Explicit Ver.)");
        matcher.ScoreAll("2.0","BTS",169,new[]{identified,known},known.Album);
        check(!identified.RejectReason.Contains("Exact album match preferred"),"Known different releases are not treated as missing albums");
        var language=Make("language","ARIRANG (Explicit Ver.)",169,"2.0 (Japanese ver.)"); var original=Make("original","",171);
        matcher.ScoreAll("2.0","BTS",169,new[]{original,language},language.Album);
        check(language.Confidence!=MatchConfidence.High && !original.RejectReason.Contains("Exact album match preferred"),
            "Album evidence cannot override an explicit language conflict");
        var noDuration=Make("missing","",171); var full=Make("full","ARIRANG (Explicit Ver.)");
        matcher.ScoreAll("2.0","BTS",null,new[]{noDuration,full},full.Album);
        check(!noDuration.RejectReason.Contains("Exact album match preferred"),"Missing playback duration retains conservative behavior");
    }
}
