using System.Text.Json.Serialization;
using TaskbarLyrics.QQMusic.Matching;

namespace TaskbarLyrics.QQMusic.Models;

public sealed class QQSongCandidate
{
    public string SongId { get; set; } = string.Empty;
    public string SongMid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artists { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumMid { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public double MatchScore { get; set; }
    public double TitleScore { get; set; }
    public double ArtistScore { get; set; }
    public double DurationScore { get; set; }
    public double AlbumScore { get; set; }
    public double VersionPenalty { get; set; }
    public int Rank { get; set; }

    public SongVersionFlags VersionFlags { get; set; }
    public MatchConfidence Confidence { get; set; } = MatchConfidence.Rejected;
    public List<string> MatchedQueries { get; set; } = new();
    public string RejectReason { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Artists)
        ? Title
        : $"{Title} - {Artists}";

    [JsonIgnore]
    public string DedupKey =>
        !string.IsNullOrWhiteSpace(SongMid) ? "mid:" + SongMid
        : !string.IsNullOrWhiteSpace(SongId) ? "id:" + SongId
        : $"t:{Title}|a:{Artists}|d:{DurationSeconds}";
}
