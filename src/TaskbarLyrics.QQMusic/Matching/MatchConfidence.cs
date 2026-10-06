namespace TaskbarLyrics.QQMusic.Matching;

public enum MatchConfidence
{
    High,
    Medium,
    Low,
    Rejected
}

public enum SearchResultQuality
{
    Good,
    Ambiguous,
    Poor
}

public enum SearchQueryPhase
{
    Strict,
    Relaxed,
    Alias,
    Manual
}
