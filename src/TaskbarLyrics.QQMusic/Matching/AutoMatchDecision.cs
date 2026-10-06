using TaskbarLyrics.QQMusic.Models;

namespace TaskbarLyrics.QQMusic.Matching;

/// <summary>
/// Result of multi-phase auto search + confidence evaluation.
/// Only High confidence should auto-bind.
/// </summary>
public sealed class AutoMatchDecision
{
    public QQSongCandidate? Best { get; init; }
    public MatchConfidence Confidence { get; init; } = MatchConfidence.Rejected;
    public SearchResultQuality ResultQuality { get; init; } = SearchResultQuality.Poor;
    public bool ShouldAutoBind => Confidence == MatchConfidence.High && Best is not null;
    public IReadOnlyList<QQSongCandidate> RankedCandidates { get; init; } = Array.Empty<QQSongCandidate>();
    public string Reason { get; init; } = string.Empty;
    public int RawCount { get; init; }
    public int UniqueCount { get; init; }
    public IReadOnlyList<string> PhasesRun { get; init; } = Array.Empty<string>();
}
