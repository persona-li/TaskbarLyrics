using System.Diagnostics;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Playback;

/// <summary>
/// High-frequency local media clock.
/// GSMTC is calibration only — classification (seek / scrub / resume gate) lives in TimelineSampleProcessor.
/// </summary>
public sealed class PlaybackClock
{
    private readonly object _lock = new();

    private TimeSpan _anchorMediaPosition;
    private long _anchorStopwatchTimestamp;
    private bool _isPlaying;
    private double _playbackRate = 1.0;
    private TimeSpan _lastRawPosition;
    private TimeSpan _lastCorrection;
    private bool _isInitialized;
    private TimeSpan? _duration;
    private PlaybackStatus _status = PlaybackStatus.Closed;
    private string _lastAction = "Uninitialized";
    private string _clockState = "UNINITIALIZED";

    public void Reset()
    {
        lock (_lock)
        {
            _anchorMediaPosition = TimeSpan.Zero;
            _anchorStopwatchTimestamp = 0;
            _isPlaying = false;
            _playbackRate = 1.0;
            _lastRawPosition = TimeSpan.Zero;
            _lastCorrection = TimeSpan.Zero;
            _isInitialized = false;
            _duration = null;
            _status = PlaybackStatus.Closed;
            _lastAction = "Reset";
            _clockState = "UNINITIALIZED";
        }
    }

    public void SetDuration(TimeSpan? duration)
    {
        lock (_lock)
        {
            _duration = duration;
        }
    }

    public TimeSpan GetEstimatedPosition()
    {
        lock (_lock)
        {
            return EstimateUnlocked();
        }
    }

    public PlaybackClockSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var estimated = EstimateUnlocked();
            return new PlaybackClockSnapshot(
                RawPosition: _lastRawPosition,
                EstimatedPosition: estimated,
                Drift: _lastRawPosition - estimated,
                LastCorrection: _lastCorrection,
                IsPlaying: _isPlaying,
                PlaybackRate: _playbackRate,
                IsInitialized: _isInitialized,
                ClockState: _clockState,
                LastAction: _lastAction);
        }
    }

    /// <summary>Hard re-anchor: next estimate starts at position (playing or frozen).</summary>
    public void HardReanchor(TimeSpan position, bool playing, double playbackRate = 1.0)
    {
        lock (_lock)
        {
            NormalizeRate(ref playbackRate);
            _playbackRate = playbackRate;
            _status = playing ? PlaybackStatus.Playing : PlaybackStatus.Paused;
            SetAnchorUnlocked(position, playing);
            _lastRawPosition = position;
            _lastCorrection = TimeSpan.Zero;
            _lastAction = "HardReanchor";
            _clockState = playing ? "PLAYING" : "PAUSED";
        }
    }

    /// <summary>
    /// Soft pull toward target while playing:
    /// corrected = estimated + (target − estimated) × factor, then re-anchor.
    /// </summary>
    public void SoftCorrect(TimeSpan targetPosition, double factor)
    {
        lock (_lock)
        {
            factor = Math.Clamp(factor, 0, 1);
            var estimated = EstimateUnlocked();
            var error = targetPosition - estimated;
            var corrected = estimated + TimeSpan.FromTicks((long)(error.Ticks * factor));
            SetAnchorUnlocked(corrected, playing: _isPlaying);
            _lastRawPosition = targetPosition;
            _lastCorrection = error;
            _lastAction = factor >= 0.35 ? "SoftBlend" : "LightBlend";
            _clockState = _isPlaying ? "PLAYING" : "PAUSED";
        }
    }

    public void PauseAt(TimeSpan position)
    {
        lock (_lock)
        {
            SetAnchorUnlocked(position, playing: false);
            _lastRawPosition = position;
            _status = PlaybackStatus.Paused;
            _lastAction = "PauseAt";
            _clockState = "PAUSED";
        }
    }

    public void ResumeFrom(TimeSpan position, double playbackRate = 1.0)
    {
        lock (_lock)
        {
            NormalizeRate(ref playbackRate);
            _playbackRate = playbackRate;
            SetAnchorUnlocked(position, playing: true);
            _lastRawPosition = position;
            _status = PlaybackStatus.Playing;
            _lastAction = "ResumeFrom";
            _clockState = "PLAYING";
        }
    }

    public void SetPlaybackRate(double playbackRate)
    {
        lock (_lock)
        {
            NormalizeRate(ref playbackRate);
            if (Math.Abs(_playbackRate - playbackRate) < 0.0001)
            {
                return;
            }

            // Keep continuous estimate when rate changes mid-play.
            var estimated = EstimateUnlocked();
            _playbackRate = playbackRate;
            if (_isPlaying)
            {
                SetAnchorUnlocked(estimated, playing: true);
            }

            _lastAction = "SetRate";
        }
    }

    public void NoteRaw(TimeSpan rawPosition)
    {
        lock (_lock)
        {
            _lastRawPosition = rawPosition;
        }
    }

    private static void NormalizeRate(ref double playbackRate)
    {
        if (playbackRate <= 0 || double.IsNaN(playbackRate) || double.IsInfinity(playbackRate))
        {
            playbackRate = 1.0;
        }
    }

    private void SetAnchorUnlocked(TimeSpan mediaPosition, bool playing)
    {
        _anchorMediaPosition = ClampToDuration(mediaPosition);
        _anchorStopwatchTimestamp = Stopwatch.GetTimestamp();
        _isPlaying = playing;
        _isInitialized = true;
    }

    private TimeSpan EstimateUnlocked()
    {
        if (!_isInitialized)
        {
            return TimeSpan.Zero;
        }

        if (!_isPlaying)
        {
            return ClampToDuration(_anchorMediaPosition);
        }

        var elapsed = Stopwatch.GetElapsedTime(_anchorStopwatchTimestamp);
        if (Math.Abs(_playbackRate - 1.0) > 0.0001)
        {
            elapsed = TimeSpan.FromTicks((long)(elapsed.Ticks * _playbackRate));
        }

        return ClampToDuration(_anchorMediaPosition + elapsed);
    }

    private TimeSpan ClampToDuration(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (_duration.HasValue && _duration.Value > TimeSpan.Zero && position > _duration.Value)
        {
            return _duration.Value;
        }

        return position;
    }
}
