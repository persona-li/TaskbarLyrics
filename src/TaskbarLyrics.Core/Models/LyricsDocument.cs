namespace TaskbarLyrics.Core.Models;

public enum LyricsSourceKind
{
    None,
    Cache,
    Network
}

public enum LyricsMode
{
    None,
    Qrc,
    Lrc
}

/// <summary>Reserved for future dual-line translation UI (MVP 1.2: always Original on taskbar).</summary>
public enum LyricsTextKind
{
    Original,
    Translation,
    Romanization
}

/// <summary>
/// Applied lyrics for the current track generation.
/// </summary>
public sealed class LyricsDocument
{
    public string? SongId { get; init; }
    public string? SongMid { get; init; }
    public string? DisplayName { get; init; }
    public double? MatchScore { get; init; }
    public LyricsMode Mode { get; init; }
    public LyricsSourceKind Source { get; init; }
    public IReadOnlyList<QrcLine> Lines { get; init; } = Array.Empty<QrcLine>();
    public bool IsInstrumental { get; init; }
    public string? Error { get; init; }
    /// <summary>Reserved: translation text when available.</summary>
    public string? Translation { get; init; }
    /// <summary>Reserved: romanization when available.</summary>
    public string? Romanization { get; init; }
    public LyricsTextKind PreferredTextKind { get; init; } = LyricsTextKind.Original;

    public int LineCount => Lines.Count;
    public int WordCount => Lines.Sum(l => l.Words.Count);

    public static LyricsDocument Empty(string? error = null) => new()
    {
        Mode = LyricsMode.None,
        Source = LyricsSourceKind.None,
        Error = error
    };
}
