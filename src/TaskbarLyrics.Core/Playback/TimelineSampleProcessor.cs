using System.Diagnostics;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Playback;

/// <summary>
/// Classifies GSMTC samples and drives <see cref="PlaybackClock"/>.
/// Paused seek uses multi-sample confirmation — never HardReanchor from a single anomalous raw.
/// </summary>
public sealed class TimelineSampleProcessor
{
    private readonly PlaybackClock _clock;
    private readonly Action<string>? _logInfo;
    private readonly Action<string>? _logWarn;
    private readonly object _lock = new();

    // Dedup of last *incoming* sample identity
    private bool _hasSampleIdentity;
    private TimeSpan _lastSamplePosition;
    private DateTimeOffset _lastSampleLastUpdated;
    private PlaybackStatus _lastSampleStatus = PlaybackStatus.Closed;
    private double _lastSampleRate = 1.0;

    // Last accepted for Playing discontinuity prediction
    private bool _hasAcceptedTimeline;
    private TimeSpan _lastAcceptedRawPosition;
    private DateTimeOffset _lastAcceptedLastUpdatedTime;
    private double _lastAcceptedPlaybackRate = 1.0;
    private PlaybackStatus _lastAcceptedStatus = PlaybackStatus.Closed;

    private PlaybackStatus _lastPlaybackStatus = PlaybackStatus.Closed;
    private DateTimeOffset _playingEpochStartedAt;
    private bool _awaitingFreshTimelineAfterResume;
    private long _scrubUntilStopwatchTimestamp;

    // Pause settling
    private bool _isPauseSettling;
    private long _pauseSettlingUntilTimestamp;
    private TimeSpan _frozenPausePosition;
    private TimeSpan? _lastObservedPausedRawPosition;
    private DateTimeOffset? _lastObservedPausedLastUpdated;

    // Playing observed baseline (CapturedAt seek fallback — NOT the accepted anchor)
    private bool _hasObservedPlayingTimeline;
    private TimeSpan _lastObservedPlayingRawPosition;
    private DateTimeOffset _lastObservedPlayingCapturedAt;
    private double _lastObservedPlayingRate = 1.0;

    // Paused seek candidate (observe only until confirmed)
    private bool _hasPausedSeekCandidate;
    private TimeSpan _pausedSeekCandidatePosition;
    private DateTimeOffset _pausedSeekCandidateLastUpdated;
    private long _pausedSeekCandidateStartedTimestamp;
    private DateTimeOffset _pausedSeekCandidateCapturedAt;
    private int _pausedSeekCandidateConfirmationCount;
    private TimeSpan _pausedSeekCandidateLatestPosition;

    public TimelineSampleProcessor(
        PlaybackClock clock,
        Action<string>? logInfo = null,
        Action<string>? logWarn = null)
    {
        _clock = clock;
        _logInfo = logInfo;
        _logWarn = logWarn;
    }

    public bool IsScrubbing
    {
        get
        {
            lock (_lock)
            {
                return IsScrubbingUnlocked();
            }
        }
    }

    public int PreferredPollIntervalMs
    {
        get
        {
            lock (_lock)
            {
                // Paused / stopped / closed: low frequency even if scrub timer still open.
                if (_lastPlaybackStatus != PlaybackStatus.Playing)
                {
                    return TimelineConstants.PausedPollMs;
                }

                if (IsScrubbingUnlocked())
                {
                    return TimelineConstants.ScrubbingPollMs;
                }

                return TimelineConstants.PlayingStablePollMs;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _hasSampleIdentity = false;
            _hasAcceptedTimeline = false;
            _lastPlaybackStatus = PlaybackStatus.Closed;
            _awaitingFreshTimelineAfterResume = false;
            _scrubUntilStopwatchTimestamp = 0;
            _playingEpochStartedAt = default;
            ClearPauseSettlingUnlocked();
            CancelPausedSeekCandidateUnlocked();
            ClearObservedPlayingUnlocked();
            _lastObservedPausedRawPosition = null;
            _lastObservedPausedLastUpdated = null;
        }
    }

    public void Process(GsmtcTimelineSample sample)
    {
        lock (_lock)
        {
            if (sample.PlaybackRate <= 0 || double.IsNaN(sample.PlaybackRate) || double.IsInfinity(sample.PlaybackRate))
            {
                sample = sample with { PlaybackRate = 1.0 };
            }

            var samePos = _hasSampleIdentity
                          && sample.Position == _lastSamplePosition
                          && sample.LastUpdatedTime == _lastSampleLastUpdated;
            var sameStatus = _hasSampleIdentity
                             && sample.Status == _lastSampleStatus
                             && Math.Abs(sample.PlaybackRate - _lastSampleRate) < 0.0001;

            if (samePos && sameStatus)
            {
                return;
            }

            _lastSamplePosition = sample.Position;
            _lastSampleLastUpdated = sample.LastUpdatedTime;
            _lastSampleStatus = sample.Status;
            _lastSampleRate = sample.PlaybackRate;
            _hasSampleIdentity = true;

            var wasPlaying = _lastPlaybackStatus == PlaybackStatus.Playing;
            var isPlaying = sample.Status == PlaybackStatus.Playing;
            var isPausedLike = !isPlaying;

            if (_isPauseSettling && !IsPauseSettlingActiveUnlocked())
            {
                EndPauseSettlingUnlocked(keepObservedAsBaseline: true);
            }

            // ── Status transitions ────────────────────────────────────────
            if (wasPlaying && isPausedLike)
            {
                var freezeAt = _clock.GetEstimatedPosition();
                _clock.PauseAt(freezeAt);
                _lastPlaybackStatus = sample.Status;
                _awaitingFreshTimelineAfterResume = false;
                _frozenPausePosition = freezeAt;
                CancelPausedSeekCandidateUnlocked();
                EnterPauseSettlingUnlocked();
                ResetTimelinePredictionBaselineUnlocked();
                LogInfo(
                    "PLAYBACK PAUSE\n" +
                    $"FrozenAt: {freezeAt.TotalMilliseconds:0}ms\n" +
                    $"Raw: {sample.Position.TotalMilliseconds:0}ms\n" +
                    "EnterPauseSettling");
            }
            else if (!wasPlaying && isPlaying)
            {
                ClearPauseSettlingUnlocked();
                CancelPausedSeekCandidateUnlocked();
                ClearObservedPlayingUnlocked();
                _lastObservedPausedRawPosition = null;
                _lastObservedPausedLastUpdated = null;
                var frozen = _clock.GetEstimatedPosition();
                _clock.ResumeFrom(frozen, sample.PlaybackRate);
                _playingEpochStartedAt = sample.CapturedAt;
                _awaitingFreshTimelineAfterResume = true;
                _lastPlaybackStatus = sample.Status;
                ResetTimelinePredictionBaselineUnlocked();
                LogInfo(
                    "PLAYBACK RESUME\n" +
                    $"FrozenPosition: {frozen.TotalMilliseconds:0}ms\n" +
                    "AwaitFreshTimeline: true");
            }
            else if (_lastPlaybackStatus != sample.Status)
            {
                _lastPlaybackStatus = sample.Status;
                if (sample.Status is PlaybackStatus.Stopped or PlaybackStatus.Closed)
                {
                    ClearPauseSettlingUnlocked();
                    CancelPausedSeekCandidateUnlocked();
                    ClearObservedPlayingUnlocked();
                    _lastObservedPausedRawPosition = null;
                    _lastObservedPausedLastUpdated = null;
                    ResetTimelinePredictionBaselineUnlocked();
                }
                else if (isPlaying)
                {
                    _clock.SetPlaybackRate(sample.PlaybackRate);
                }
            }
            else if (isPlaying)
            {
                _clock.SetPlaybackRate(sample.PlaybackRate);
            }

            // Out-of-order vs accepted playing baseline — do NOT pollute observed baseline
            if (_hasAcceptedTimeline
                && sample.LastUpdatedTime < _lastAcceptedLastUpdatedTime
                    - TimeSpan.FromMilliseconds(TimelineConstants.OutOfOrderToleranceMs))
            {
                if (isPausedLike)
                {
                    ObservePausedRawUnlocked(sample);
                }

                return;
            }

            if (isPlaying)
            {
                HandlePlayingPositionUnlocked(sample);
                return;
            }

            // Leave playing observed baseline when paused (resume will clear on transition)
            // All paused-like: unified candidate model (settling + stable)
            HandlePausedPositionUnlocked(sample);
        }
    }

    /// <summary>
    /// PauseSettling and PausedStable share one candidate model.
    /// Single anomalous sample never HardReanchors the freeze latch.
    /// </summary>
    private void HandlePausedPositionUnlocked(GsmtcTimelineSample sample)
    {
        if (!_clock.GetSnapshot().IsInitialized)
        {
            _clock.HardReanchor(sample.Position, playing: false, sample.PlaybackRate);
            _frozenPausePosition = sample.Position;
            AcceptTimeline(sample);
            ObservePausedRawUnlocked(sample);
            return;
        }

        ObservePausedRawUnlocked(sample);

        var distFromFrozenMs = Math.Abs((sample.Position - _frozenPausePosition).TotalMilliseconds);

        // Near frozen: natural settle / rollback — cancel any candidate, leave clock alone
        if (distFromFrozenMs < TimelineConstants.PausedSeekFrozenReturnToleranceMs)
        {
            if (_hasPausedSeekCandidate)
            {
                CancelPausedSeekCandidateUnlocked();
                LogInfo(
                    "PAUSED SEEK CANDIDATE CANCELLED (near frozen)\n" +
                    $"Frozen: {_frozenPausePosition.TotalMilliseconds:0}ms\n" +
                    $"Raw: {sample.Position.TotalMilliseconds:0}ms");
            }

            _clock.NoteRaw(sample.Position);
            return;
        }

        // Far from frozen: start / update candidate (never instant HardReanchor)
        if (distFromFrozenMs < TimelineConstants.PausedSeekCandidateStartThresholdMs)
        {
            // Between frozen-return and start threshold: observe only
            _clock.NoteRaw(sample.Position);
            return;
        }

        if (!_hasPausedSeekCandidate)
        {
            StartPausedSeekCandidateUnlocked(sample);
            _clock.NoteRaw(sample.Position);
            return;
        }

        // Existing candidate
        if (!IsNewTimelineEvidenceUnlocked(sample))
        {
            // Same stale GSMTC state (e.g. repeated Poll) — do not double-count
            _clock.NoteRaw(sample.Position);
            return;
        }

        var distFromClusterMs = Math.Abs(
            (sample.Position - _pausedSeekCandidateLatestPosition).TotalMilliseconds);

        if (distFromClusterMs <= TimelineConstants.PausedSeekCandidateToleranceMs)
        {
            // Same cluster: confirm
            _pausedSeekCandidateConfirmationCount++;
            _pausedSeekCandidateLatestPosition = sample.Position;
            _pausedSeekCandidateLastUpdated = sample.LastUpdatedTime;
            _pausedSeekCandidateCapturedAt = sample.CapturedAt;

            if (TryConfirmPausedSeekUnlocked())
            {
                return;
            }

            _clock.NoteRaw(sample.Position);
            return;
        }

        // Far from cluster: user still dragging — replace candidate
        StartPausedSeekCandidateUnlocked(sample);
        _clock.NoteRaw(sample.Position);
    }

    private void StartPausedSeekCandidateUnlocked(GsmtcTimelineSample sample)
    {
        _hasPausedSeekCandidate = true;
        _pausedSeekCandidatePosition = sample.Position;
        _pausedSeekCandidateLatestPosition = sample.Position;
        _pausedSeekCandidateLastUpdated = sample.LastUpdatedTime;
        _pausedSeekCandidateCapturedAt = sample.CapturedAt;
        _pausedSeekCandidateStartedTimestamp = Stopwatch.GetTimestamp();
        _pausedSeekCandidateConfirmationCount = 1;
        LogInfo(
            "PAUSED SEEK CANDIDATE\n" +
            $"Frozen: {_frozenPausePosition.TotalMilliseconds:0}ms\n" +
            $"Candidate: {sample.Position.TotalMilliseconds:0}ms\n" +
            "Clock unchanged");
    }

    private void CancelPausedSeekCandidateUnlocked()
    {
        _hasPausedSeekCandidate = false;
        _pausedSeekCandidateConfirmationCount = 0;
        _pausedSeekCandidatePosition = TimeSpan.Zero;
        _pausedSeekCandidateLatestPosition = TimeSpan.Zero;
        _pausedSeekCandidateLastUpdated = default;
        _pausedSeekCandidateCapturedAt = default;
        _pausedSeekCandidateStartedTimestamp = 0;
    }

    private bool IsNewTimelineEvidenceUnlocked(GsmtcTimelineSample sample)
    {
        // Prefer LastUpdated advancement
        if (sample.LastUpdatedTime > _pausedSeekCandidateLastUpdated)
        {
            return true;
        }

        // Same LastUpdated + same position = identical GSMTC state (Poll replay)
        if (sample.Position == _pausedSeekCandidateLatestPosition
            && sample.LastUpdatedTime == _pausedSeekCandidateLastUpdated)
        {
            return false;
        }

        // Secondary: LastUpdated stuck but Raw moved, and enough wall time elapsed
        if (sample.Position != _pausedSeekCandidateLatestPosition)
        {
            var ageMs = (sample.CapturedAt - _pausedSeekCandidateCapturedAt).TotalMilliseconds;
            if (ageMs >= TimelineConstants.PausedSeekCandidateMinConfirmAgeMs)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryConfirmPausedSeekUnlocked()
    {
        if (!_hasPausedSeekCandidate)
        {
            return false;
        }

        if (_pausedSeekCandidateConfirmationCount < TimelineConstants.PausedSeekConfirmSamples)
        {
            return false;
        }

        var ageMs = Stopwatch.GetElapsedTime(_pausedSeekCandidateStartedTimestamp).TotalMilliseconds;
        if (ageMs < TimelineConstants.PausedSeekCandidateMinConfirmAgeMs)
        {
            return false;
        }

        var latest = _pausedSeekCandidateLatestPosition;
        CancelPausedSeekCandidateUnlocked();

        _clock.HardReanchor(latest, playing: false, playbackRate: 1.0);
        _frozenPausePosition = latest;
        AcceptTimeline(new GsmtcTimelineSample(
            latest,
            _lastObservedPausedLastUpdated ?? DateTimeOffset.Now,
            DateTimeOffset.Now,
            PlaybackStatus.Paused,
            1.0,
            TimelineSampleSource.Poll));
        EnterPauseSettlingUnlocked();

        LogInfo(
            "PAUSED SEEK CONFIRMED\n" +
            $"HardReanchor: {latest.TotalMilliseconds:0}ms\n" +
            "Remain Paused + re-enter PauseSettling");
        return true;
    }

    private void HandlePlayingPositionUnlocked(GsmtcTimelineSample sample)
    {
        double discontinuityMs = 0;
        var isTimelineDiscontinuity = false;
        var lastUpdatedAdvanced = false;
        var lastUpdatedSameStampJump = false;
        var captureFallbackSeek = false;

        // ── 1) LastUpdated-based discontinuity (primary) ─────────────────
        if (_hasAcceptedTimeline)
        {
            var lastUpdatedDelta = sample.LastUpdatedTime - _lastAcceptedLastUpdatedTime;
            if (lastUpdatedDelta.TotalMilliseconds > 0)
            {
                lastUpdatedAdvanced = true;
                TimeSpan expectedRaw;
                if (_lastAcceptedStatus == PlaybackStatus.Playing)
                {
                    expectedRaw = _lastAcceptedRawPosition
                                  + TimeSpan.FromTicks((long)(lastUpdatedDelta.Ticks * _lastAcceptedPlaybackRate));
                }
                else
                {
                    expectedRaw = _lastAcceptedRawPosition;
                }

                discontinuityMs = (sample.Position - expectedRaw).TotalMilliseconds;
                isTimelineDiscontinuity =
                    Math.Abs(discontinuityMs) >= TimelineConstants.TimelineSeekDiscontinuityMs;
            }
            else if (lastUpdatedDelta.TotalMilliseconds == 0
                     && sample.Position != _lastAcceptedRawPosition)
            {
                // Same LastUpdated stamp, Position jumped (existing capability)
                lastUpdatedSameStampJump = true;
                discontinuityMs = (sample.Position - _lastAcceptedRawPosition).TotalMilliseconds;
                isTimelineDiscontinuity =
                    Math.Abs(discontinuityMs) >= TimelineConstants.TimelineSeekDiscontinuityMs;
            }
        }

        // ── 2) CapturedAt fallback when LastUpdated cannot advance-predict ─
        // Use when LU didn't advance (no reliable rate projection). Same-stamp jump
        // already handled above; CapturedAt still helps when LU regresses slightly
        // (not OOO) or observed baseline exists without accepted LU advance.
        if (!isTimelineDiscontinuity
            && !lastUpdatedAdvanced
            && !lastUpdatedSameStampJump
            && _hasObservedPlayingTimeline
            && !_awaitingFreshTimelineAfterResume)
        {
            var captureDelta = sample.CapturedAt - _lastObservedPlayingCapturedAt;
            if (captureDelta.TotalMilliseconds > 0
                && captureDelta.TotalMilliseconds <= TimelineConstants.MaxFreshExtrapolateMs * 2)
            {
                var expectedByCapture = _lastObservedPlayingRawPosition
                    + TimeSpan.FromTicks((long)(captureDelta.Ticks * _lastObservedPlayingRate));
                var captureDiscMs = (sample.Position - expectedByCapture).TotalMilliseconds;
                if (Math.Abs(captureDiscMs) >= TimelineConstants.TimelineSeekDiscontinuityMs)
                {
                    isTimelineDiscontinuity = true;
                    captureFallbackSeek = true;
                    discontinuityMs = captureDiscMs;
                }
            }
        }

        if (isTimelineDiscontinuity)
        {
            EnterScrubWindowUnlocked();
        }

        var ageMs = (sample.CapturedAt - sample.LastUpdatedTime).TotalMilliseconds;
        var isFreshForEpoch = IsFreshForPlayingEpoch(sample);
        // Only extrapolate when LastUpdated advanced (trustworthy stamp for "now").
        // Same-stamp jump / CapturedAt fallback: anchor to raw to avoid overshoot.
        var canExtrapolate = isFreshForEpoch
                             && lastUpdatedAdvanced
                             && !captureFallbackSeek
                             && ageMs >= 0
                             && ageMs <= TimelineConstants.MaxFreshExtrapolateMs;

        var adjusted = canExtrapolate
            ? sample.Position + TimeSpan.FromMilliseconds(ageMs * sample.PlaybackRate)
            : sample.Position;

        var estimatedBefore = _clock.GetEstimatedPosition();
        var clockErrorMs = (adjusted - estimatedBefore).TotalMilliseconds;
        var clockSnap = _clock.GetSnapshot();

        if (!clockSnap.IsInitialized)
        {
            _clock.HardReanchor(adjusted, playing: true, sample.PlaybackRate);
            AcceptTimeline(sample);
            ObservePlayingUnlocked(sample);
            _playingEpochStartedAt = sample.CapturedAt;
            _awaitingFreshTimelineAfterResume = false;
            return;
        }

        if (_awaitingFreshTimelineAfterResume && !isFreshForEpoch)
        {
            _clock.NoteRaw(sample.Position);
            // Do not seed observed baseline with pre-resume timeline
            return;
        }

        if (isTimelineDiscontinuity || IsScrubbingUnlocked())
        {
            if (isFreshForEpoch || isTimelineDiscontinuity)
            {
                // Capture fallback / same-stamp jump: adjusted == raw (no stale LU age).
                var target = adjusted;
                _clock.HardReanchor(target, playing: true, sample.PlaybackRate);
                AcceptTimeline(sample);
                ObservePlayingUnlocked(sample);
                EnterScrubWindowUnlocked();
                if (_awaitingFreshTimelineAfterResume && isFreshForEpoch)
                {
                    _awaitingFreshTimelineAfterResume = false;
                }

                if (isTimelineDiscontinuity)
                {
                    LogInfo(
                        "TIMELINE SEEK\n" +
                        $"Raw: {sample.Position.TotalMilliseconds:0}ms\n" +
                        $"Discontinuity: {discontinuityMs:0}ms\n" +
                        $"CaptureFallback: {captureFallbackSeek}");
                }
            }

            return;
        }

        if (_awaitingFreshTimelineAfterResume && isFreshForEpoch)
        {
            _awaitingFreshTimelineAfterResume = false;
        }

        var absErr = Math.Abs(clockErrorMs);
        if (absErr <= TimelineConstants.SmallErrorMs)
        {
            _clock.NoteRaw(adjusted);
            AcceptTimeline(sample);
            ObservePlayingUnlocked(sample);
        }
        else if (absErr <= TimelineConstants.SoftBlendMs)
        {
            _clock.SoftCorrect(adjusted, 0.20);
            AcceptTimeline(sample);
            ObservePlayingUnlocked(sample);
        }
        else if (absErr <= TimelineConstants.ClockHardCorrectionMs)
        {
            _clock.SoftCorrect(adjusted, 0.40);
            AcceptTimeline(sample);
            ObservePlayingUnlocked(sample);
        }
        else
        {
            _clock.HardReanchor(adjusted, playing: true, sample.PlaybackRate);
            AcceptTimeline(sample);
            ObservePlayingUnlocked(sample);
        }
    }

    private void ObservePlayingUnlocked(GsmtcTimelineSample sample)
    {
        _lastObservedPlayingRawPosition = sample.Position;
        _lastObservedPlayingCapturedAt = sample.CapturedAt;
        _lastObservedPlayingRate = sample.PlaybackRate > 0 ? sample.PlaybackRate : 1.0;
        _hasObservedPlayingTimeline = true;
    }

    private void ClearObservedPlayingUnlocked()
    {
        _hasObservedPlayingTimeline = false;
        _lastObservedPlayingRawPosition = TimeSpan.Zero;
        _lastObservedPlayingCapturedAt = default;
        _lastObservedPlayingRate = 1.0;
    }

    private void ObservePausedRawUnlocked(GsmtcTimelineSample sample)
    {
        _lastObservedPausedRawPosition = sample.Position;
        _lastObservedPausedLastUpdated = sample.LastUpdatedTime;
    }

    private void EnterPauseSettlingUnlocked()
    {
        _isPauseSettling = true;
        var hold = Stopwatch.Frequency * TimelineConstants.PauseSettlingMs / 1000L;
        _pauseSettlingUntilTimestamp = Stopwatch.GetTimestamp() + hold;
    }

    private bool IsPauseSettlingActiveUnlocked()
    {
        if (!_isPauseSettling)
        {
            return false;
        }

        return Stopwatch.GetTimestamp() < _pauseSettlingUntilTimestamp;
    }

    private void EndPauseSettlingUnlocked(bool keepObservedAsBaseline)
    {
        _isPauseSettling = false;
        _pauseSettlingUntilTimestamp = 0;
        if (keepObservedAsBaseline && _lastObservedPausedRawPosition is { } raw
            && _lastObservedPausedLastUpdated is { } lu)
        {
            _lastAcceptedRawPosition = raw;
            _lastAcceptedLastUpdatedTime = lu;
            _lastAcceptedPlaybackRate = 1.0;
            _lastAcceptedStatus = PlaybackStatus.Paused;
            _hasAcceptedTimeline = true;
        }
    }

    private void ClearPauseSettlingUnlocked()
    {
        _isPauseSettling = false;
        _pauseSettlingUntilTimestamp = 0;
    }

    private void ResetTimelinePredictionBaselineUnlocked()
    {
        _hasAcceptedTimeline = false;
        _lastAcceptedRawPosition = TimeSpan.Zero;
        _lastAcceptedLastUpdatedTime = default;
        _lastAcceptedPlaybackRate = 1.0;
        _lastAcceptedStatus = PlaybackStatus.Closed;
    }

    private void AcceptTimeline(GsmtcTimelineSample sample)
    {
        _lastAcceptedRawPosition = sample.Position;
        _lastAcceptedLastUpdatedTime = sample.LastUpdatedTime;
        _lastAcceptedPlaybackRate = sample.PlaybackRate;
        _lastAcceptedStatus = sample.Status;
        _hasAcceptedTimeline = true;
    }

    private bool IsFreshForPlayingEpoch(GsmtcTimelineSample sample)
    {
        if (!_awaitingFreshTimelineAfterResume)
        {
            return sample.Status == PlaybackStatus.Playing;
        }

        var threshold = _playingEpochStartedAt
                        - TimeSpan.FromMilliseconds(TimelineConstants.ResumeFreshToleranceMs);
        return sample.LastUpdatedTime >= threshold;
    }

    private void EnterScrubWindowUnlocked()
    {
        var hold = Stopwatch.Frequency * TimelineConstants.ScrubHoldMs / 1000L;
        _scrubUntilStopwatchTimestamp = Stopwatch.GetTimestamp() + hold;
    }

    private bool IsScrubbingUnlocked()
    {
        if (_scrubUntilStopwatchTimestamp == 0)
        {
            return false;
        }

        return Stopwatch.GetTimestamp() < _scrubUntilStopwatchTimestamp;
    }

    private void LogInfo(string msg) => _logInfo?.Invoke(msg);
}
