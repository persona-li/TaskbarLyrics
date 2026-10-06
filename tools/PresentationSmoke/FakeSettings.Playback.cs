using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.Core.Models;

internal sealed partial class FakeSettings
{
    public PlaybackControlCapabilities PlaybackCapabilities { get; set; } = new(true, true, true, true, true);
    public List<(PlaybackControlAction Action, TimeSpan? Position, long Generation)> PlaybackCalls { get; } = [];
    public TaskCompletionSource<bool>? PendingPlaybackControl { get; set; }
    public bool AcceptPlaybackControl { get; set; } = true;
    public Exception? PlaybackControlError { get; set; }

    public async Task<bool> ControlPlaybackAsync(PlaybackControlAction action, TimeSpan? position, long expectedGeneration, CancellationToken token)
    {
        PlaybackCalls.Add((action, position, expectedGeneration));
        if (PlaybackControlError is Exception error) throw error;
        var accepted = PendingPlaybackControl is { } pending ? await pending.Task.WaitAsync(token) : AcceptPlaybackControl;
        token.ThrowIfCancellationRequested();
        if (!accepted || State.Generation != expectedGeneration || !State.SessionConnected) return false;
        State = action switch
        {
            PlaybackControlAction.Play => State with { Status = PlaybackStatus.Playing, Clock = State.Clock with { IsPlaying = true } },
            PlaybackControlAction.Pause => State with { Status = PlaybackStatus.Paused, Clock = State.Clock with { IsPlaying = false } },
            PlaybackControlAction.Seek when position is TimeSpan target => State with
            {
                Clock = State.Clock with { RawPosition = target, EstimatedPosition = target }
            },
            _ => State
        };
        StateChanged?.Invoke();
        return true;
    }
}
