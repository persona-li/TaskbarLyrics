namespace TaskbarLyrics.Core.Models;

/// <summary>
/// Platform-agnostic playback status (not GSMTC/WinRT).
/// </summary>
public enum PlaybackStatus
{
    Closed = 0,
    Opened = 1,
    Changing = 2,
    Stopped = 3,
    Playing = 4,
    Paused = 5
}
