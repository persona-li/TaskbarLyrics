using System.Diagnostics;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.Cache;
namespace TaskbarLyrics.App.Presentation;
public sealed class SettingsBackend(TrackCoordinator coordinator, CacheStatisticsService statistics) : ISettingsBackend
{
    private readonly StartupService _startup = new();
    public event Action? StateChanged { add => coordinator.StateChanged += value; remove => coordinator.StateChanged -= value; }
    public OverlayUiState ReadState() => coordinator.GetUiState();
    public PlaybackControlCapabilities PlaybackCapabilities => coordinator.PlaybackCapabilities;
    public Task<bool> ControlPlaybackAsync(PlaybackControlAction action, TimeSpan? position, long expectedGeneration, CancellationToken cancellationToken)
        => coordinator.ControlPlaybackAsync(action, position, expectedGeneration, cancellationToken);
    public bool SetTrackOffset(long value, long generation, string? cacheKey) => coordinator.TrySetTrackOffsetMs(value, generation, cacheKey);
    public bool StartupEnabled => _startup.IsEnabled();
    public void SetStartup(bool enabled) => _startup.SetEnabled(enabled);
    public Task<CacheStatistics> StatisticsAsync(CancellationToken token) => statistics.ComputeAsync(token);
    public string DiagnosticDetails => OverlayLayoutDiagnostics.Get().ToDisplayText();
    public OverlayLayoutSnapshot Layout => OverlayLayoutDiagnostics.Get();
    public void OpenFolder(string kind) => Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "\"" + (kind == "logs" ? CachePaths.LogsRoot : CachePaths.CacheRoot) + "\"", UseShellExecute = true });
    public async Task ExecuteAsync(SettingsOperation operation, CancellationToken token, long? expectedGeneration = null)
    {
        token.ThrowIfCancellationRequested();
        switch (operation)
        {
            case SettingsOperation.Reload: await coordinator.ReloadCurrentLyricsAsync(expectedGeneration); break;
            case SettingsOperation.Redownload: await coordinator.RedownloadCurrentLyricsAsync(expectedGeneration); break;
            case SettingsOperation.ClearLyrics:
                await Task.Run(() => CacheStatisticsService.ClearAllLyricsCacheStrict(cancellationToken: token), token); break;
            case SettingsOperation.ClearCurrentManual:
                if (!coordinator.ClearCurrentManualMatch(expectedGeneration)) throw new InvalidOperationException("当前歌曲没有手动匹配。"); break;
            case SettingsOperation.ClearAllManual: coordinator.ClearAllManualMatches(); break;
            case SettingsOperation.ClearOffsets: coordinator.TrackSettings.ClearAllOffsets(requirePersistence: true); coordinator.NotifyOffsetsChanged(); break;
        }
    }
}
