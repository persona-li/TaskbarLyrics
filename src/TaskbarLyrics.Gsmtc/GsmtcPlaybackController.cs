using Windows.Media.Control;

namespace TaskbarLyrics.Gsmtc;

public readonly record struct SessionPlaybackCapabilities(bool CanPlay, bool CanPause, bool CanSeek,
    bool CanSkipPrevious = false, bool CanSkipNext = false);

/// <summary>Explicit commands to one captured Windows session; never uses a global media key or toggle.</summary>
public static class GsmtcPlaybackController
{
    public static SessionPlaybackCapabilities ReadCapabilities(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var controls = session.GetPlaybackInfo()?.Controls;
            return controls is null ? default : new(
                controls.IsPlayEnabled, controls.IsPauseEnabled, controls.IsPlaybackPositionEnabled,
                controls.IsPreviousEnabled, controls.IsNextEnabled);
        }
        catch { return default; }
    }

    public static Task<bool> PlayAsync(GlobalSystemMediaTransportControlsSession session, CancellationToken token)
        => session.TryPlayAsync().AsTask(token);

    public static Task<bool> PauseAsync(GlobalSystemMediaTransportControlsSession session, CancellationToken token)
        => session.TryPauseAsync().AsTask(token);

    public static Task<bool> PreviousAsync(GlobalSystemMediaTransportControlsSession session, CancellationToken token)
        => session.TrySkipPreviousAsync().AsTask(token);

    public static Task<bool> NextAsync(GlobalSystemMediaTransportControlsSession session, CancellationToken token)
        => session.TrySkipNextAsync().AsTask(token);

    public static Task<bool>? SeekAsync(GlobalSystemMediaTransportControlsSession session, TimeSpan requestedPosition, CancellationToken token)
    {
        var timeline = session.GetTimelineProperties();
        if (timeline is null) return null;
        var start = timeline.MinSeekTime;
        var end = timeline.MaxSeekTime;
        // Some desktop players advertise seek support but leave the optional seek range empty.
        if (end <= start)
        {
            start = timeline.StartTime;
            end = timeline.EndTime;
        }
        if (!PlaybackControlGuard.TryClampSeekPosition(requestedPosition, start, end, out var position)) return null;
        return session.TryChangePlaybackPositionAsync(position.Ticks).AsTask(token);
    }
}

/// <summary>Pure request guards, shared by production commands and offline scenarios.</summary>
public static class PlaybackControlGuard
{
    public static bool IsCurrent(object? expectedSession, object? currentSession, long expectedGeneration, long currentGeneration)
        => expectedSession is not null && ReferenceEquals(expectedSession, currentSession) && expectedGeneration == currentGeneration;

    public static bool CanCompleteNavigation(object? expectedSession, object? currentSession, bool sessionSelected, bool disposed)
        => !disposed && sessionSelected && expectedSession is not null && ReferenceEquals(expectedSession, currentSession);

    public static bool TryClampSeekPosition(TimeSpan requestedPosition, TimeSpan start, TimeSpan end, out TimeSpan position)
    {
        position = TimeSpan.Zero;
        start = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        if (end <= start) return false;
        position = requestedPosition < start ? start : requestedPosition > end ? end : requestedPosition;
        return true;
    }

    /// <summary>Retains transport exclusivity until the initiated metadata read has settled.</summary>
    public static async Task<bool> AwaitRefreshAsync(bool accepted, Task refresh, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            await refresh.WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return accepted;
        }
        catch (OperationCanceledException) { return false; }
        catch { return false; }
    }

    public static async Task<bool> ExecuteAsync(
        Func<CancellationToken, Task<bool>?> startIfCurrent,
        Func<bool> isCurrent,
        Action refreshIfCurrent,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            var request = startIfCurrent(timeout.Token);
            if (request is null || !await request.WaitAsync(timeout.Token).ConfigureAwait(false) || !isCurrent()) return false;
            timeout.Token.ThrowIfCancellationRequested();
            refreshIfCurrent();
            return isCurrent();
        }
        catch (OperationCanceledException) { return false; }
        catch { return false; }
    }
}
