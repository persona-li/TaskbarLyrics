namespace TaskbarLyrics.App.Services;

/// <summary>Runs media refreshes sequentially without depending on the UI dispatcher.</summary>
public static class MediaPollLoop
{
    public static async Task RunAsync(
        Func<CancellationToken, ValueTask<bool>> waitForNext,
        Func<CancellationToken, Task> poll,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(waitForNext);
        ArgumentNullException.ThrowIfNull(poll);

        try
        {
            while (!token.IsCancellationRequested && await waitForNext(token).ConfigureAwait(false))
            {
                // A timer callback can complete concurrently with cancellation.
                if (token.IsCancellationRequested) break;
                await poll(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disposal/session shutdown is a normal end of the polling loop.
        }
    }
}
