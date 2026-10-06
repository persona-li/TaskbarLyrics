using TaskbarLyrics.App.Services;
using TaskbarLyrics.Cache;
namespace TaskbarLyrics.App.Presentation;
public enum SettingsOperation { Reload, Redownload, ClearLyrics, ClearCurrentManual, ClearAllManual, ClearOffsets }
public enum PlaybackControlAction { Play, Pause, Seek, Previous, Next }
public sealed record PlaybackControlCapabilities(bool CanPlay, bool CanPause, bool CanSeek,
    bool CanSkipPrevious = false, bool CanSkipNext = false);
public interface ISettingsBackend
{
    event Action? StateChanged;
    OverlayUiState ReadState();
    PlaybackControlCapabilities PlaybackCapabilities { get; }
    Task<bool> ControlPlaybackAsync(PlaybackControlAction action, TimeSpan? position, long expectedGeneration, CancellationToken cancellationToken);
    bool SetTrackOffset(long value, long generation, string? cacheKey);
    Task ExecuteAsync(SettingsOperation operation, CancellationToken token, long? expectedGeneration = null);
    Task<CacheStatistics> StatisticsAsync(CancellationToken token);
    bool StartupEnabled { get; }
    void SetStartup(bool enabled);
    void OpenFolder(string kind);
    string DiagnosticDetails { get; }
    OverlayLayoutSnapshot Layout { get; }
}
