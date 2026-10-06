using TaskbarLyrics.Gsmtc;

internal static class PlaybackControlChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var oldSession = new object();
        var currentSession = oldSession;
        var generation = 5L;
        check(PlaybackControlGuard.IsCurrent(oldSession, currentSession, 5, generation), "Playback controls accept only the captured live session and generation");
        check(!PlaybackControlGuard.IsCurrent(oldSession, new object(), 5, generation), "A player restart cannot redirect a command to its new session");
        check(!PlaybackControlGuard.IsCurrent(oldSession, oldSession, 4, generation), "A command from the previous song is rejected");
        check(!PlaybackControlGuard.IsCurrent(null, null, 5, 5), "Disconnected playback never has a valid command target");
        check(PlaybackControlGuard.CanCompleteNavigation(oldSession, currentSession, true, false), "Accepted skip can finish on its captured session after a track generation changes");
        check(!PlaybackControlGuard.CanCompleteNavigation(oldSession, new object(), true, false)
            && !PlaybackControlGuard.CanCompleteNavigation(oldSession, oldSession, false, false), "Skip completion still rejects replacement or disconnected sessions");
        var duration = TimeSpan.FromSeconds(180);
        check(PlaybackControlGuard.TryClampSeekPosition(TimeSpan.FromSeconds(-4), TimeSpan.Zero, duration, out var position)
            && position == TimeSpan.Zero, "Rewind at the start clamps to zero");
        check(PlaybackControlGuard.TryClampSeekPosition(TimeSpan.FromSeconds(185), TimeSpan.Zero, duration, out position)
            && position == duration, "Seek beyond duration clamps to the last valid position");
        check(PlaybackControlGuard.TryClampSeekPosition(TimeSpan.FromSeconds(12.345), TimeSpan.Zero, duration, out position)
            && position == TimeSpan.FromSeconds(12.345), "Seek preserves subsecond precision");
        check(PlaybackControlGuard.TryClampSeekPosition(TimeSpan.Zero, TimeSpan.FromSeconds(20), duration, out position)
            && position == TimeSpan.FromSeconds(20), "Seek respects a player-supplied minimum seek position");
        check(!PlaybackControlGuard.TryClampSeekPosition(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, out _), "An unknown or empty seek range is unavailable");
        check(!PlaybackControlGuard.TryClampSeekPosition(TimeSpan.Zero, duration, TimeSpan.Zero, out _), "A reversed seek range is unavailable");

        var sends = 0;
        var refreshes = 0;
        bool Current() => PlaybackControlGuard.IsCurrent(oldSession, currentSession, 5, generation);
        Task<bool>? Start(CancellationToken _) { if (!Current()) return null; sends++; return Task.FromResult(true); }
        void Refresh() { if (Current()) refreshes++; }
        check(await PlaybackControlGuard.ExecuteAsync(Start, Current, Refresh, CancellationToken.None)
            && sends == 1 && refreshes == 1, "Successful playback control refreshes the actual source once");

        generation++;
        check(!await PlaybackControlGuard.ExecuteAsync(Start, Current, Refresh, CancellationToken.None)
            && sends == 1 && refreshes == 1, "A stale queued command never contacts a player");
        generation = 5;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = PlaybackControlGuard.ExecuteAsync(_ => pending.Task, Current, Refresh, CancellationToken.None);
        currentSession = new object();
        pending.SetResult(true);
        check(!await running && refreshes == 1, "Late command completion cannot refresh a replacement session");
        currentSession = oldSession;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        running = PlaybackControlGuard.ExecuteAsync(_ => pending.Task, Current, Refresh, CancellationToken.None);
        generation++;
        pending.SetResult(true);
        check(!await running && refreshes == 1, "Late command completion cannot refresh another song");
        generation = 5;
        check(!await PlaybackControlGuard.ExecuteAsync(_ => Task.FromResult(false), Current, Refresh, CancellationToken.None)
            && refreshes == 1, "A player-declined control does not invent a state refresh");
        check(!await PlaybackControlGuard.ExecuteAsync(_ => throw new InvalidOperationException("offline failure"), Current, Refresh, CancellationToken.None)
            && refreshes == 1, "A disconnected player failure returns false");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        check(!await PlaybackControlGuard.ExecuteAsync(Start, Current, Refresh, canceled.Token) && sends == 1,
            "Canceled controls do not contact a player");
        using var cancelPending = new CancellationTokenSource();
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        running = PlaybackControlGuard.ExecuteAsync(_ => pending.Task, Current, Refresh, cancelPending.Token);
        cancelPending.Cancel();
        check(!await running && refreshes == 1, "Cancellation ends an unresponsive player request without waiting for its result");
        pending.SetResult(true);
        check(refreshes == 1, "A canceled operation's late success never refreshes state");
    }
}
