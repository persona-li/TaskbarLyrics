// Core GSMTC property reading patterns adapted from GsmtcProbe.

using TaskbarLyrics.Core.Models;
using Windows.Media.Control;

namespace TaskbarLyrics.Gsmtc;

public static class GsmtcSnapshotReader
{
    public static async Task<PlaybackSnapshot?> ReadAsync(
        GlobalSystemMediaTransportControlsSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var capturedAt = DateTimeOffset.Now;

        string aumid;
        try
        {
            aumid = session.SourceAppUserModelId ?? string.Empty;
        }
        catch
        {
            aumid = string.Empty;
        }

        string title = string.Empty;
        string artist = string.Empty;
        string? album = null;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (props is not null)
            {
                title = props.Title ?? string.Empty;
                artist = props.Artist ?? string.Empty;
                album = props.AlbumTitle;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // keep empty
        }

        // Timeline/status below are read now, not when the metadata request began.
        capturedAt = DateTimeOffset.Now;
        var status = PlaybackStatus.Closed;
        double rate = 1.0;
        try
        {
            var info = session.GetPlaybackInfo();
            if (info is not null)
            {
                status = PlaybackStatusMapper.Map(info.PlaybackStatus);
                try
                {
                    rate = info.PlaybackRate ?? 1.0;
                }
                catch
                {
                    rate = 1.0;
                }
            }
        }
        catch
        {
            // keep defaults
        }

        TimeSpan position = TimeSpan.Zero;
        TimeSpan start = TimeSpan.Zero;
        TimeSpan end = TimeSpan.Zero;
        DateTimeOffset lastUpdated = capturedAt;
        TimeSpan? duration = null;

        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline is not null)
            {
                position = timeline.Position;
                start = timeline.StartTime;
                end = timeline.EndTime;
                lastUpdated = timeline.LastUpdatedTime;
                if (end > TimeSpan.Zero)
                {
                    duration = end - start;
                    if (duration <= TimeSpan.Zero)
                    {
                        duration = end;
                    }
                }
            }
        }
        catch
        {
            // keep defaults
        }

        var track = new TrackIdentity(title, artist, duration, album);
        return new PlaybackSnapshot(
            Track: track,
            Status: status,
            RawPosition: position,
            Duration: duration,
            TimelineLastUpdatedTime: lastUpdated,
            CapturedAt: capturedAt,
            PlaybackRate: rate,
            AlbumTitle: album,
            SourceAppUserModelId: aumid);
    }

    public static bool TryReadTimeline(
        GlobalSystemMediaTransportControlsSession session,
        out TimeSpan position,
        out TimeSpan endTime,
        out DateTimeOffset lastUpdated,
        out TimeSpan? duration)
    {
        position = TimeSpan.Zero;
        endTime = TimeSpan.Zero;
        lastUpdated = DateTimeOffset.Now;
        duration = null;

        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline is null)
            {
                return false;
            }

            position = timeline.Position;
            endTime = timeline.EndTime;
            lastUpdated = timeline.LastUpdatedTime;
            if (endTime > TimeSpan.Zero)
            {
                var start = timeline.StartTime;
                duration = endTime - start;
                if (duration <= TimeSpan.Zero)
                {
                    duration = endTime;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadPlaybackInfo(
        GlobalSystemMediaTransportControlsSession session,
        out PlaybackStatus status,
        out double playbackRate)
    {
        status = PlaybackStatus.Closed;
        playbackRate = 1.0;
        try
        {
            var info = session.GetPlaybackInfo();
            if (info is null)
            {
                return false;
            }

            status = PlaybackStatusMapper.Map(info.PlaybackStatus);
            try
            {
                playbackRate = info.PlaybackRate ?? 1.0;
            }
            catch
            {
                playbackRate = 1.0;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
