using TaskbarLyrics.Core.Models;
namespace TaskbarLyrics.App.Presentation;

// Stabilize only a user-initiated skip. QQ reports Stopped -> Changing -> Playing,
// including when navigation starts from a paused song. Keep raw transport untouched.
public sealed class NavigationPlaybackLabel
{
    public const long GraceMilliseconds = 900;
    private object? _origin;
    private long _deadline;
    private long? _playingSince;
    private bool _heldPlaying;
    private string _heldLabel = "等待播放";
    public void Begin(PlaybackPresentation state, long now)
    {
        _origin = state.HasTrack ? state.TrackVisualKey : null;
        _heldPlaying = state.IsPlaying;
        _heldLabel = state.PlaybackLabel;
        _deadline = now + GraceMilliseconds;
        _playingSince = null;
    }
    public void Clear() { _origin = null; _playingSince = null; }
    private bool Hold(PlaybackPresentation state, long now)
    {
        if (_origin is null) return false;
        if (now >= _deadline || !state.HasTrack || state.Raw.Status is PlaybackStatus.Closed or PlaybackStatus.Opened)
        { Clear(); return false; }
        if (state.IsPlaying)
        {
            _heldPlaying = true;
            _heldLabel = state.PlaybackLabel;
            if (!Equals(_origin, state.TrackVisualKey)) _playingSince ??= now;
        }
        else _playingSince = null;
        if (_playingSince.HasValue && now - _playingSince.Value >= 200) { Clear(); return false; }
        return state.Raw.Status is PlaybackStatus.Stopped or PlaybackStatus.Changing or PlaybackStatus.Paused;
    }
    public bool IsPlaying(PlaybackPresentation state, long now) => Hold(state, now) ? _heldPlaying : state.IsPlaying;
    public string Read(PlaybackPresentation state, long now) => Hold(state, now) ? _heldLabel : state.PlaybackLabel;
}
