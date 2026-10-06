using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.App.Controls;

public sealed record KaraokeRenderState(
    string Text,
    IReadOnlyList<QrcWord> Words,
    int CurrentWordIndex,
    double CurrentWordProgress,
    bool KaraokeEnabled,
    /// <summary>True when showing title/loading/instrumental without word timing.</summary>
    bool StaticLineOnly);
