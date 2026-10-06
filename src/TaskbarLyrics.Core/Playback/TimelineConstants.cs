namespace TaskbarLyrics.Core.Playback;

/// <summary>Central thresholds for GSMTC timeline classification (not magic numbers).</summary>
public static class TimelineConstants
{
    /// <summary>GSMTC timeline vs projected previous timeline → Seek / Scrub (Playing).</summary>
    public const double TimelineSeekDiscontinuityMs = 250;

    /// <summary>Reject position if LastUpdated is older than last accepted by more than this.</summary>
    public const double OutOfOrderToleranceMs = 30;

    /// <summary>Hold hard-reanchor mode after a seek (debounce scrub).</summary>
    public const int ScrubHoldMs = 600;

    /// <summary>Playing stable poll — first seek discovery before IsScrubbing.</summary>
    public const int PlayingStablePollMs = 75;

    /// <summary>While IsScrubbing after Playing seek detected.</summary>
    public const int ScrubbingPollMs = 40;

    /// <summary>Paused / idle / no active play — keep low frequency.</summary>
    public const int PausedPollMs = 200;

    // Legacy aliases (tests / any remaining references)
    public const int NormalPollMs = PausedPollMs;
    public const int ScrubPollMs = ScrubbingPollMs;

    /// <summary>Timeline LastUpdated must be near resume epoch to count as fresh.</summary>
    public const int ResumeFreshToleranceMs = 100;

    /// <summary>Max age (CapturedAt − LastUpdated) applied while playing and sample is fresh.</summary>
    public const int MaxFreshExtrapolateMs = 1000;

    // Stable playing drift (clock error vs adjusted raw — NOT seek detection)
    public const double SmallErrorMs = 80;
    public const double SoftBlendMs = 180;
    public const double ClockHardCorrectionMs = 600;

    /// <summary>
    /// After Playing→Paused, observe-only window for QQ Music pause settle.
    /// Does NOT auto-reanchor the freeze latch on ordinary samples.
    /// </summary>
    public const int PauseSettlingMs = 1500;

    // ── Paused seek candidate (never confirm on a single sample) ──────────

    /// <summary>|raw − frozen| must exceed this to start a candidate.</summary>
    public const double PausedSeekCandidateStartThresholdMs = 250;

    /// <summary>Samples within this of the candidate latest form one cluster.</summary>
    public const double PausedSeekCandidateToleranceMs = 300;

    /// <summary>Sample near frozen cancels an open candidate (settle rollback).</summary>
    public const double PausedSeekFrozenReturnToleranceMs = 300;

    /// <summary>Distinct fresh evidences required to confirm paused seek.</summary>
    public const int PausedSeekConfirmSamples = 2;

    /// <summary>Minimum age of candidate before confirmation is allowed.</summary>
    public const int PausedSeekCandidateMinConfirmAgeMs = 80;
}
