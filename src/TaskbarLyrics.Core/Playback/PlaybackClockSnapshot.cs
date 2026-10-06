namespace TaskbarLyrics.Core.Playback;

public sealed record PlaybackClockSnapshot(
    TimeSpan RawPosition,
    TimeSpan EstimatedPosition,
    TimeSpan Drift,
    TimeSpan LastCorrection,
    bool IsPlaying,
    double PlaybackRate,
    bool IsInitialized,
    string ClockState,
    string LastAction = "");
