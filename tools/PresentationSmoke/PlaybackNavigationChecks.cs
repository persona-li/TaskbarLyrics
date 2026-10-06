using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Gsmtc;

internal static class PlaybackNavigationChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        await CheckRefreshCompletionAsync(check);
        using var config = new ConfigService(_ => { }, _ => { }, (_, _) => { },
            System.IO.Path.Combine(root, "playback-navigation"), watchFile: false, fontExists: _ => true);
        var backend = new FakeSettings();
        backend.State = backend.State with { Status = PlaybackStatus.Paused, Clock = backend.State.Clock with { IsPlaying = false } };
        using var vm = new SettingsViewModel(config, backend);
        var legacy = new PlaybackControlCapabilities(true, true, true);
        var legacySession = new SessionPlaybackCapabilities(true, true, true);
        check(!legacy.CanSkipPrevious && !legacy.CanSkipNext && !legacySession.CanSkipPrevious && !legacySession.CanSkipNext,
            "Unspecified navigation capabilities remain unavailable rather than guessing player support");

        backend.PlaybackCapabilities = new(false, false, false, true, false);
        vm.Refresh();
        check(vm.CanSkipPrevious && !vm.CanSkipNext && !vm.CanTogglePlayback && !vm.CanSeekPlayback,
            "Previous support is independent of next, play and seek support");
        check(vm.PreviousTrackCommand.CanExecute(null) && !vm.NextTrackCommand.CanExecute(null),
            "Navigation commands expose the current per-direction capability");
        await vm.NextTrackAsync();
        check(backend.PlaybackCalls.Count == 0, "Unsupported next-track requests never reach the backend");
        var track = backend.State.Track;
        var beforeClock = backend.State.Clock;
        var beforeTrackOffset = vm.TrackOffset;
        var beforeGlobalOffset = vm.GlobalOffset;
        await vm.PreviousTrackAsync();
        check(backend.PlaybackCalls.Count == 1 && backend.PlaybackCalls[0] == (PlaybackControlAction.Previous, null, backend.State.Generation),
            "Previous dispatches one explicit request with the displayed generation and no invented seek position");
        check(backend.State.Track == track && backend.State.Clock == beforeClock && backend.State.Status == PlaybackStatus.Paused,
            "Navigation acknowledgment does not fabricate a new song, clock or resumed playback");
        check(vm.TrackOffset == beforeTrackOffset && vm.GlobalOffset == beforeGlobalOffset,
            "Track navigation leaves calibration values unchanged");

        backend.PlaybackCapabilities = new(false, false, false, false, true);
        backend.State = backend.State with { Track = track! with { Duration = null } };
        vm.Refresh();
        check(!vm.CanSkipPrevious && vm.CanSkipNext && vm.PlaybackDurationSeconds == 0,
            "Next is available without a duration when the player advertises it");
        await vm.PreviousTrackAsync();
        await vm.NextTrackAsync();
        check(backend.PlaybackCalls.Count == 2 && backend.PlaybackCalls[^1].Action == PlaybackControlAction.Next,
            "Only the supported navigation direction is dispatched");
        backend.State = backend.State with { SessionConnected = false };
        vm.Refresh();
        await vm.NextTrackAsync();
        check(!vm.CanSkipPrevious && !vm.CanSkipNext && backend.PlaybackCalls.Count == 2,
            "A disconnected player disables track navigation even with stale capabilities");

        backend.State = backend.State with { SessionConnected = true, Track = track };
        backend.PlaybackCapabilities = new(true, true, true, true, true);
        vm.Refresh();
        var notices = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Notice)) notices++; };
        backend.PendingPlaybackControl = new TaskCompletionSource<bool>();
        var pending = vm.NextTrackAsync();
        var calls = backend.PlaybackCalls.Count;
        check(vm.IsPlaybackControlBusy && !vm.CanSkipPrevious && !vm.CanSkipNext && !vm.CanTogglePlayback && !vm.CanSeekPlayback,
            "An in-flight next request prevents overlapping navigation, toggle and seek requests");
        await vm.PreviousTrackAsync(); await vm.NextTrackAsync(); await vm.TogglePlaybackAsync(); await vm.SeekPlaybackAsync(20);
        check(backend.PlaybackCalls.Count == calls, "Repeated transport clicks cannot overlap navigation");
        backend.PendingPlaybackControl.SetResult(false);
        await pending;
        check(!vm.IsPlaybackControlBusy && vm.CanSkipPrevious && vm.CanSkipNext && vm.NoticeError && notices == 1,
            "A rejected navigation request reports once and restores available controls");

        backend.PendingPlaybackControl = null;
        backend.PlaybackControlError = new InvalidOperationException("navigation fixture failure");
        await vm.PreviousTrackAsync();
        check(notices == 2 && !vm.IsPlaybackControlBusy && vm.Notice.Contains("navigation fixture failure"),
            "A navigation exception reports once without leaving the controls busy");
        backend.PlaybackControlError = null;
        calls = backend.PlaybackCalls.Count;
        backend.State = backend.State with { Generation = backend.State.Generation + 1 };
        await vm.NextTrackAsync();
        check(backend.PlaybackCalls.Count == calls && notices == 2,
            "A click from the previously displayed song refreshes state instead of skipping the replacement song");

        backend.PendingPlaybackControl = new TaskCompletionSource<bool>();
        pending = vm.NextTrackAsync();
        var replacement = backend.State with
        {
            Generation = backend.State.Generation + 1,
            Track = track! with { Title = "replacement fixture" },
            Clock = backend.State.Clock with { EstimatedPosition = TimeSpan.FromSeconds(7) }
        };
        backend.State = replacement;
        vm.Refresh();
        backend.PendingPlaybackControl.SetResult(true);
        await pending;
        check(backend.State == replacement && vm.State.Raw.Generation == replacement.Generation && notices == 2,
            "A successful skip followed by a real track change retains new state without a stale failure notice");
        check(!vm.IsPlaybackControlBusy && vm.CanSkipPrevious && vm.CanSkipNext,
            "Navigation controls recover after the expected generation transition");

        vm.Activate(SettingsPage.Overview);
        backend.PendingPlaybackControl = new TaskCompletionSource<bool>();
        pending = vm.PreviousTrackAsync();
        vm.Deactivate();
        await pending;
        backend.PendingPlaybackControl.SetResult(true);
        check(backend.State == replacement && notices == 2 && !vm.IsPlaybackControlBusy,
            "Leaving either playback page cancels pending navigation and ignores late completion");
        vm.Activate(SettingsPage.Synchronization);
        backend.PendingPlaybackControl = null;
        await vm.PreviousTrackAsync();
        check(backend.PlaybackCalls[^1].Action == PlaybackControlAction.Previous && notices == 2,
            "Navigation works again on the calibration page after cancellation on overview");
    }

    private static async Task CheckRefreshCompletionAsync(Action<bool, string> check)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = 14;
        // A real skip normally replaces the generation during this read. Completion
        // must not reclassify that expected state update as a rejected media command.
        async Task RefreshTrackAsync() { await source.Task; generation++; }
        var completion = PlaybackControlGuard.AwaitRefreshAsync(true, RefreshTrackAsync(), CancellationToken.None);
        check(!completion.IsCompleted && generation == 14,
            "Navigation completion waits while its metadata refresh is still pending");
        source.SetResult();
        check(await completion && generation == 15,
            "Accepted navigation remains successful after the metadata refresh changes the track generation");
        check(!await PlaybackControlGuard.AwaitRefreshAsync(false, Task.CompletedTask, CancellationToken.None),
            "Finishing metadata does not turn a rejected navigation request into success");
        check(!await PlaybackControlGuard.AwaitRefreshAsync(true, Task.FromException(new InvalidOperationException("read fixture")), CancellationToken.None),
            "An unsuccessful metadata refresh releases navigation without an unobserved exception");

        using var cancellation = new CancellationTokenSource();
        source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = PlaybackControlGuard.AwaitRefreshAsync(true, source.Task, cancellation.Token);
        cancellation.Cancel();
        check(!await completion && !source.Task.IsCompleted,
            "Leaving a page ends metadata waiting without requiring the player to respond");
        source.SetResult();
        check(!await completion, "A late metadata response cannot revive a canceled navigation completion");
        check(!await PlaybackControlGuard.AwaitRefreshAsync(true, Task.CompletedTask, cancellation.Token),
            "Already canceled navigation cannot succeed even with an immediately available refresh");
    }
}
