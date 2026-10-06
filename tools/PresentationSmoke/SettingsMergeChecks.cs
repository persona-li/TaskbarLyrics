using System.IO;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.Cache;

internal static class SettingsMergeChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var navigation = new SettingsNavigation();
        check(SettingsNavigation.Normalize(SettingsPage.Data) == SettingsPage.General,
            "Legacy data route resolves to settings");
        navigation.Navigate(SettingsPage.Appearance);
        check(navigation.Navigate(SettingsPage.Data) && navigation.Current == SettingsPage.General,
            "Legacy data navigation enters the merged settings page");
        check(!navigation.Navigate(SettingsPage.General) && !navigation.Navigate(SettingsPage.Data),
            "Data and settings aliases cannot create duplicate history entries");
        navigation.Navigate(SettingsPage.Synchronization);
        check(navigation.TryGoBack(out var merged) && merged == SettingsPage.General,
            "Back navigation restores the canonical merged page");
        check(navigation.TryGoBack(out var appearance) && appearance == SettingsPage.Appearance,
            "Merged page keeps the earlier navigation destination");
        check(navigation.TryGoBack(out var overview) && overview == SettingsPage.Overview && !navigation.CanGoBack,
            "Legacy data alias does not leave a phantom history entry");

        using var config = new ConfigService(_ => { }, _ => { }, (_, _) => { },
            Path.Combine(root, "settings-merge"), watchFile: false, fontExists: _ => true);
        var backend = new DeferredStatisticsBackend();
        using var vm = new SettingsViewModel(config, backend);
        SettingsPage? requested = null;
        vm.NavigateRequested += page => requested = page;
        vm.NavigateCommand.Execute("Data");
        check(requested == SettingsPage.General, "Legacy data commands request the merged route");

        vm.Activate(SettingsPage.General);
        check(vm.Page == SettingsPage.General && backend.Requests.Count == 1,
            "Entering settings automatically requests data statistics");
        backend.Requests[^1].Source.SetResult(Statistics(11));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(vm.CacheCount == "11" && vm.CacheSize == "11.0 KB" && vm.ManualCount == "12" && vm.OffsetCount == "13",
            "Merged settings receives cache size and all preserved counts");

        var older = vm.RefreshStatisticsAsync();
        var olderRequest = backend.Requests[^1];
        var newer = vm.RefreshStatisticsAsync();
        backend.Requests[^1].Source.SetResult(Statistics(20));
        await newer;
        olderRequest.Source.SetResult(Statistics(10));
        await older;
        check(vm.CacheCount == "20", "A slower earlier refresh cannot overwrite the latest statistics");

        older = vm.RefreshStatisticsAsync();
        olderRequest = backend.Requests[^1];
        newer = vm.RefreshStatisticsAsync();
        backend.Requests[^1].Source.SetResult(Statistics(30));
        await newer;
        olderRequest.Source.SetException(new IOException("superseded fixture error"));
        await older;
        check(vm.CacheCount == "30" && !vm.HasNotice,
            "A superseded refresh error cannot replace a successful newer result with a notice");

        var failed = vm.RefreshStatisticsAsync();
        backend.Requests[^1].Source.SetException(new IOException("current fixture error"));
        await failed;
        check(vm.NoticeError && vm.Notice.Contains("current fixture error", StringComparison.Ordinal) && vm.CacheCount == "30",
            "The latest refresh failure remains visible while retaining the previous statistics");
        var notice = vm.Notice;

        var abandoned = vm.RefreshStatisticsAsync();
        var abandonedRequest = backend.Requests[^1];
        var requestsBeforeLeaving = backend.Requests.Count;
        vm.Activate(SettingsPage.Appearance);
        check(abandonedRequest.Token.IsCancellationRequested && backend.Requests.Count == requestsBeforeLeaving,
            "Leaving settings cancels its statistics request without starting another page's data read");
        abandonedRequest.Source.SetResult(Statistics(999));
        await abandoned;
        check(vm.CacheCount == "30" && vm.Notice == notice,
            "An uncancellable late result cannot apply after leaving settings");

        vm.Activate(SettingsPage.Data);
        check(vm.Page == SettingsPage.General && backend.Requests.Count == requestsBeforeLeaving + 1,
            "Direct legacy activation normalizes and refreshes the merged settings page");
        backend.Requests[^1].Source.SetResult(Statistics(31));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        abandoned = vm.RefreshStatisticsAsync();
        abandonedRequest = backend.Requests[^1];
        vm.Deactivate();
        check(abandonedRequest.Token.IsCancellationRequested, "Hiding settings cancels a pending statistics read");
        abandonedRequest.Source.SetException(new IOException("hidden fixture error"));
        await abandoned;
        check(vm.CacheCount == "31" && vm.Notice == notice,
            "A hidden page's late failure cannot display a stale error");

        vm.Activate(SettingsPage.General);
        backend.Requests[^1].Source.SetResult(Statistics(32));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        abandoned = vm.RefreshStatisticsAsync();
        abandonedRequest = backend.Requests[^1];
        vm.Deactivate();
        abandonedRequest.Source.SetResult(Statistics(999));
        await abandoned;
        check(vm.CacheCount == "32", "Hiding settings rejects late successful statistics as well as errors");

        vm.Activate(SettingsPage.General);
        backend.Requests[^1].Source.SetResult(Statistics(33));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        abandoned = vm.RefreshStatisticsAsync();
        abandonedRequest = backend.Requests[^1];
        vm.Dispose();
        abandonedRequest.Source.SetException(new IOException("disposed fixture error"));
        await abandoned;
        var requestsBeforeDisposedRefresh = backend.Requests.Count;
        await vm.RefreshStatisticsAsync();
        check(vm.CacheCount == "33" && vm.Notice == notice && backend.Requests.Count == requestsBeforeDisposedRefresh,
            "Disposal rejects pending errors and prevents access to a disposed page cancellation source");
    }

    private static CacheStatistics Statistics(int count) => new()
    {
        LyricsCacheEntryCount = count,
        LyricsCacheTotalBytes = count * 1024L,
        ManualMatchCount = count + 1,
        TrackSettingsCount = count + 2,
        CacheRoot = "[isolated fixture]/Cache"
    };

    private sealed class DeferredStatisticsBackend : ISettingsBackend
    {
        private readonly FakeSettings _inner = new();
        public List<StatisticsRequest> Requests { get; } = [];
        public event Action? StateChanged { add => _inner.StateChanged += value; remove => _inner.StateChanged -= value; }
        public OverlayUiState ReadState() => _inner.ReadState();
        public PlaybackControlCapabilities PlaybackCapabilities => _inner.PlaybackCapabilities;
        public Task<bool> ControlPlaybackAsync(PlaybackControlAction action, TimeSpan? position, long expectedGeneration, CancellationToken cancellationToken)
            => _inner.ControlPlaybackAsync(action, position, expectedGeneration, cancellationToken);
        public bool SetTrackOffset(long value, long generation, string? cacheKey) => _inner.SetTrackOffset(value, generation, cacheKey);
        public Task ExecuteAsync(SettingsOperation operation, CancellationToken token, long? expectedGeneration = null)
            => _inner.ExecuteAsync(operation, token, expectedGeneration);
        public Task<CacheStatistics> StatisticsAsync(CancellationToken token)
        {
            var source = new TaskCompletionSource<CacheStatistics>();
            Requests.Add(new(token, source));
            // Deliberately ignore cancellation: completion order and late results are driven by the fixture.
            return source.Task;
        }
        public bool StartupEnabled => _inner.StartupEnabled;
        public void SetStartup(bool enabled) => _inner.SetStartup(enabled);
        public void OpenFolder(string kind) => _inner.OpenFolder(kind);
        public string DiagnosticDetails => _inner.DiagnosticDetails;
        public OverlayLayoutSnapshot Layout => _inner.Layout;
    }

    private sealed record StatisticsRequest(CancellationToken Token, TaskCompletionSource<CacheStatistics> Source);
}
