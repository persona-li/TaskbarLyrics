using System.Threading.Channels;
using TaskbarLyrics.App.Services;

internal static class MediaPollLoopChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // Queued ticks cannot start another read while the current read is blocked.
        using (var cancellation = new CancellationTokenSource())
        {
            var ticks = Channel.CreateUnbounded<bool>();
            var firstStarted = Signal();
            var secondStarted = Signal();
            var releaseFirst = Signal();
            var releaseSecond = Signal();
            int waits = 0, polls = 0, active = 0, maximumActive = 0;
            var loop = MediaPollLoop.RunAsync(
                token => { Interlocked.Increment(ref waits); return ticks.Reader.ReadAsync(token); },
                async token =>
                {
                    int number = Interlocked.Increment(ref polls);
                    maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
                    (number == 1 ? firstStarted : secondStarted).TrySetResult();
                    try { await (number == 1 ? releaseFirst : releaseSecond).Task.WaitAsync(token).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref active); }
                }, cancellation.Token);

            ticks.Writer.TryWrite(true);
            await firstStarted.Task.ConfigureAwait(false);
            ticks.Writer.TryWrite(true);
            check(polls == 1 && waits == 1 && active == 1, "Media polling does not consume another tick during an active read");
            releaseFirst.TrySetResult();
            await secondStarted.Task.ConfigureAwait(false);
            check(polls == 2 && maximumActive == 1, "Queued media refreshes execute sequentially without overlap");
            cancellation.Cancel();
            await loop.ConfigureAwait(false);
            check(polls == 2 && active == 0 && waits == 2, "Cancellation ends an in-flight cancellable read without scheduling another");
        }

        // An already issued wait may complete successfully after the session was disposed.
        using (var cancellation = new CancellationTokenSource())
        {
            var lateTick = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int polls = 0;
            var loop = MediaPollLoop.RunAsync(_ => new ValueTask<bool>(lateTick.Task),
                _ => { polls++; return Task.CompletedTask; }, cancellation.Token);
            cancellation.Cancel();
            lateTick.SetResult(true);
            await loop.ConfigureAwait(false);
            check(polls == 0, "A late media tick cannot initiate a read after cancellation");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var ticks = Channel.CreateUnbounded<bool>();
            int polls = 0;
            var loop = MediaPollLoop.RunAsync(token => ticks.Reader.ReadAsync(token),
                _ => { polls++; return Task.CompletedTask; }, cancellation.Token);
            cancellation.Cancel();
            await loop.ConfigureAwait(false);
            check(polls == 0, "Cancellation while waiting exits normally without a media read");

            int waits = 0;
            await MediaPollLoop.RunAsync(_ => { waits++; return ValueTask.FromResult(true); },
                _ => { polls++; return Task.CompletedTask; }, cancellation.Token).ConfigureAwait(false);
            check(waits == 0 && polls == 0, "An already cancelled polling loop never starts its timer wait");
        }

        int completedPolls = 0;
        await MediaPollLoop.RunAsync(_ => ValueTask.FromResult(false),
            _ => { completedPolls++; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
        check(completedPolls == 0, "A completed timer ends media polling normally");

        var expected = new InvalidOperationException("fake media read failure");
        Exception? observed = null;
        try
        {
            await MediaPollLoop.RunAsync(_ => ValueTask.FromResult(true),
                _ => Task.FromException(expected), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { observed = ex; }
        check(ReferenceEquals(expected, observed), "Unexpected media read failures remain visible to the caller");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
