using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.App.Services;

/// <summary>
/// Removes GSMTC fields that are known to be transient during a media-property transition.
/// </summary>
public static class TrackMatchInputSanitizer
{
    public static bool IsDifferentTrack(TrackIdentity? previous, TrackIdentity current) =>
        previous is null || !previous.SameAs(current)
        || (!string.IsNullOrWhiteSpace(current.Album)
            && TrackIdentity.Normalize(previous.Album) != TrackIdentity.Normalize(current.Album));

    public static TrackIdentity ForTrackChange(TrackIdentity? previous, TrackIdentity current)
    {
        if (previous is null
            || previous.DurationSeconds is not int previousDuration
            || current.DurationSeconds is not int currentDuration
            || previousDuration <= 0
            || previousDuration != currentDuration)
        {
            return current;
        }

        var titleChanged = !string.Equals(
            TrackIdentity.Normalize(previous.Title),
            TrackIdentity.Normalize(current.Title),
            StringComparison.Ordinal);
        var artistChanged = !string.Equals(
            TrackIdentity.Normalize(previous.Artist),
            TrackIdentity.Normalize(current.Artist),
            StringComparison.Ordinal);

        return titleChanged || artistChanged
            ? current with { Duration = null }
            : current;
    }
}
