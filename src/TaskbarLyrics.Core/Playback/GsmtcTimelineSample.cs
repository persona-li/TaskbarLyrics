using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Playback;

public enum TimelineSampleSource
{
    TimelineEvent,
    PlaybackInfoEvent,
    Poll
}

/// <summary>
/// One GSMTC read. CapturedAt must be set at the moment of the WinRT read (not after await).
/// </summary>
public sealed record GsmtcTimelineSample(
    TimeSpan Position,
    DateTimeOffset LastUpdatedTime,
    DateTimeOffset CapturedAt,
    PlaybackStatus Status,
    double PlaybackRate,
    TimelineSampleSource Source);
