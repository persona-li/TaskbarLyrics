namespace TaskbarLyrics.Core.Models;

/// <summary>
/// Platform-agnostic media snapshot (filled by GSMTC layer).
/// </summary>
public sealed record PlaybackSnapshot(
    TrackIdentity Track,
    PlaybackStatus Status,
    TimeSpan RawPosition,
    TimeSpan? Duration,
    DateTimeOffset TimelineLastUpdatedTime,
    DateTimeOffset CapturedAt,
    double PlaybackRate = 1.0,
    string? AlbumTitle = null,
    string SourceAppUserModelId = "");
