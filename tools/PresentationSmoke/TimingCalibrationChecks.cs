using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.Core.Models;

internal static class TimingCalibrationChecks
{
    public static void Run(Action<bool,string> check,string root)
    {
        using var config=new ConfigService(_=>{},_=>{},(_,_)=>{},System.IO.Path.Combine(root,"calibration"),watchFile:false,fontExists:_=>true);
        var backend=new FakeSettings { GlobalOffset=()=>config.Current.Lyrics.GlobalOffsetMs };
        using var vm=new SettingsViewModel(config,backend);
        vm.Refresh();
        check(vm.State.CanAdjustTrack && vm.State.TrackOffsetHint == "", "Bound song needs no calibration prerequisite hint");
        var paused = PlaybackPresentation.From(backend.State with { Status = PlaybackStatus.Paused });
        check(paused.CanAdjustTrack && paused.TrackOffsetHint == "", "Paused bound song remains adjustable without asking the user to start playback");
        var disconnected = PlaybackPresentation.From(backend.State with { SessionConnected = false });
        check(!disconnected.CanAdjustTrack && disconnected.TrackOffsetHint == "在播放器中播放歌曲",
            "Disconnected session shows the playback prerequisite instead of a lyric matching instruction");
        var waiting = PlaybackPresentation.From(backend.State with { Track = null, CacheKey = null, LyricsLoading = true });
        check(!waiting.CanAdjustTrack && waiting.TrackOffsetHint == "在播放器中播放歌曲",
            "Missing track takes precedence over stale lyric loading state");
        var loading = PlaybackPresentation.From(backend.State with { CacheKey = null, LyricsLoading = true });
        check(!loading.CanAdjustTrack && loading.TrackOffsetHint == "正在获取歌词…",
            "Pending lyric lookup explains unavailable song calibration without prompting a premature selection");
        var needsMatch = PlaybackPresentation.From(backend.State with { CacheKey = null, LyricsLoading = false, NeedsRematch = true });
        check(!needsMatch.CanAdjustTrack && needsMatch.TrackOffsetHint == "先选择歌词，再校准本曲",
            "Unmatched song gives the specific next step for enabling calibration");
        var noBinding = PlaybackPresentation.From(backend.State with { CacheKey = null, LyricsLoading = false, NeedsRematch = false });
        check(!noBinding.CanAdjustTrack && noBinding.TrackOffsetHint == "先选择歌词，再校准本曲",
            "Missing binding retains an actionable prerequisite even without a rematch flag");
        var initial=vm.TrackOffset;
        vm.TimingStep=50;
        check(vm.TimingSteps.SequenceEqual([50d, 100d, 500d]) && vm.TimingEarlierLabel=="提前 50 ms" && vm.TimingLaterLabel=="延后 50 ms",
            "Calibration action labels show the current supported step");
        vm.AdjustTimingCommand.Execute("TrackEarlier");
        check(vm.TrackOffset==initial+50,"Earlier adds positive single-track offset");
        vm.AdjustTimingCommand.Execute("TrackLater");
        check(vm.TrackOffset==initial,"Later subtracts single-track offset");
        vm.TimingStep=500;
        vm.TimingStep=75;
        check(vm.TimingStep==500 && vm.TimingStepLabel=="每次 500 ms", "Unsupported timing steps are ignored");
        vm.AdjustTimingCommand.Execute("GlobalEarlier");
        check(vm.GlobalOffset==500 && vm.TrackOffset==initial,"Global adjustment leaves per-track offset unchanged");
        vm.GlobalOffset=4900;
        vm.AdjustTimingCommand.Execute("GlobalEarlier");
        check(vm.GlobalOffset==5000,"Timing increment respects upper bound");
        vm.AdjustTimingCommand.Execute("TrackReset");
        check(vm.TrackOffset==0 && vm.GlobalOffset==5000,"Track reset changes only its own scope");
        vm.AdjustTimingCommand.Execute("GlobalReset");
        check(vm.GlobalOffset==0,"Global reset restores zero");
        backend.State=backend.State with { Track=null,SessionConnected=false,CacheKey=null };
        vm.Refresh();
        var before=backend.State.TrackOffsetMs;
        vm.AdjustTimingCommand.Execute("TrackEarlier");
        check(backend.State.TrackOffsetMs==before,"Disconnected track adjustment cannot write a binding");
        vm.AdjustTimingCommand.Execute("GlobalLater");
        check(vm.GlobalOffset==-500,"Global adjustment remains available without a track");
        check(vm.GlobalTimingSummary=="延后 500 ms", "Collapsed global summary keeps nonzero negative calibration visible");
        check(backend.PlaybackCalls.Count==0, "Calibration and reset never trigger player transport controls");
    }

    public static async Task RunPlaybackAsync(Action<bool, string> check, string root)
    {
        using var config = new ConfigService(_=>{}, _=>{}, (_,_)=>{}, System.IO.Path.Combine(root, "calibration-playback"), watchFile:false, fontExists:_=>true);
        config.Update(c => c.Lyrics.GlobalOffsetMs = 150, scheduleSave:false);
        var backend = new FakeSettings { GlobalOffset = () => config.Current.Lyrics.GlobalOffsetMs };
        backend.State = backend.State with { Status = PlaybackStatus.Paused, Clock = backend.State.Clock with { IsPlaying = false } };
        using var vm = new SettingsViewModel(config, backend);
        vm.Refresh();
        check(vm.ActualTimingEffect == "歌词提前 100 ms" && vm.GlobalTimingSummary == "提前 150 ms",
            "Effective lyric timing and global timing use separate plain-language summaries");
        check(vm.PlaybackButtonLabel == "播放" && vm.CanTogglePlayback && vm.CanSeekPlayback,
            "Paused connected playback enables explicit listen controls");
        check(vm.PlaybackPositionSeconds == 83 && vm.PlaybackDurationSeconds == 246,
            "Progress values expose the actual media clock and duration in seconds");
        vm.AdjustTimingCommand.Execute("TrackReset");
        check(vm.TrackOffset == 0 && vm.GlobalOffset == 150 && vm.ActualTimingEffect == "歌词提前 150 ms",
            "Reset current song preserves global calibration and its effective summary");
        check(backend.State.Status == PlaybackStatus.Paused && backend.PlaybackCalls.Count == 0,
            "Adjusting paused lyrics never starts audio playback");
        vm.GlobalOffset = 0;
        check(vm.ActualTimingEffect == "歌词无偏移" && vm.GlobalTimingSummary == "未调整",
            "Zero offsets display an unambiguous neutral summary");
        vm.TrackOffset = -100;
        check(vm.ActualTimingEffect == "歌词延后 100 ms", "Negative effective offset describes delayed lyrics");

        backend.State = backend.State with { Clock = backend.State.Clock with { EstimatedPosition = TimeSpan.FromSeconds(2) } };
        await vm.ReplayFiveSecondsAsync();
        check(backend.PlaybackCalls[^1].Action == PlaybackControlAction.Seek && backend.PlaybackCalls[^1].Position == TimeSpan.Zero,
            "Replay five seconds reads the current clock and clamps at song start");
        check(backend.PlaybackCalls[^1].Generation == backend.State.Generation, "Transport passes the displayed song generation to the backend");
        backend.State = backend.State with { Clock = backend.State.Clock with { EstimatedPosition = TimeSpan.FromSeconds(10.75) } };
        await vm.ReplayFiveSecondsAsync();
        check(backend.PlaybackCalls[^1].Position == TimeSpan.FromSeconds(5.75), "Replay uses a fresh clock without rounding away subsecond position");
        await vm.SeekPlaybackAsync(500);
        check(backend.PlaybackCalls[^1].Position == TimeSpan.FromSeconds(246), "Seek clamps at the current song duration");
        await vm.SeekPlaybackAsync(-50);
        check(backend.PlaybackCalls[^1].Position == TimeSpan.Zero, "Seek clamps at zero");
        var before = backend.PlaybackCalls.Count;
        await vm.SeekPlaybackAsync(double.NaN);
        await vm.SeekPlaybackAsync(double.PositiveInfinity);
        check(backend.PlaybackCalls.Count == before, "Nonfinite seek input cannot reach the player");
        check(backend.State.Status == PlaybackStatus.Paused && backend.PlaybackCalls.All(c => c.Action == PlaybackControlAction.Seek),
            "Replay and seek preserve pause and never chain an implicit play request");
        check(vm.TrackOffset == -100 && vm.GlobalOffset == 0, "Transport controls do not alter saved calibration");

        await vm.TogglePlaybackAsync();
        check(backend.PlaybackCalls[^1].Action == PlaybackControlAction.Play && vm.PlaybackButtonLabel == "暂停", "Only explicit toggle resumes playback");
        backend.PlaybackCapabilities = new(true, false, true);
        check(!vm.CanTogglePlayback, "Playing state requires pause capability rather than play capability");
        backend.PlaybackCapabilities = new(true, true, true);
        await vm.TogglePlaybackAsync();
        check(backend.PlaybackCalls[^1].Action == PlaybackControlAction.Pause && vm.PlaybackButtonLabel == "播放", "Toggle pauses playing audio and updates its label");
        backend.PlaybackCapabilities = new(false, true, true);
        check(!vm.CanTogglePlayback, "Paused state requires play capability rather than pause capability");

        backend.PlaybackCapabilities = new(false, false, false);
        vm.Refresh();
        before = backend.PlaybackCalls.Count;
        await vm.TogglePlaybackAsync(); await vm.ReplayFiveSecondsAsync(); await vm.SeekPlaybackAsync(25);
        check(!vm.CanTogglePlayback && !vm.CanSeekPlayback && backend.PlaybackCalls.Count == before,
            "Unavailable player capabilities disable controls and cannot dispatch requests");
        check(vm.SeekSupportHint == "播放器不支持跳转", "Unsupported seeking explains disabled replay and progress controls");
        backend.PlaybackCapabilities = new(true, true, true);
        var track = backend.State.Track!;
        backend.State = backend.State with { Track = track with { Duration = null } };
        vm.Refresh();
        check(vm.PlaybackDurationSeconds == 0 && !vm.CanSeekPlayback && vm.CanTogglePlayback,
            "Unknown duration disables seeking while preserving available play controls");
        check(vm.SeekSupportHint == "暂未获取歌曲时长", "Unknown duration has its own seek availability explanation");
        backend.State = backend.State with { Track = track, SessionConnected = false };
        vm.Refresh();
        check(!vm.CanSeekPlayback && !vm.CanTogglePlayback && vm.PlaybackPositionSeconds == 0, "Disconnected state disables all transport controls");
        check(vm.SeekSupportHint.Length == 0, "Disconnected state does not mislabel the player as unsupported");
        backend.State = backend.State with { SessionConnected = true };
        vm.Refresh();
        check(vm.SeekSupportHint.Length == 0, "Available seeking clears its explanation");

        var notices = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Notice)) notices++; };
        backend.PendingPlaybackControl = new TaskCompletionSource<bool>();
        var pending = vm.SeekPlaybackAsync(35);
        before = backend.PlaybackCalls.Count;
        check(vm.IsPlaybackControlBusy && !vm.CanSeekPlayback && !vm.CanTogglePlayback, "Pending transport request disables duplicate controls");
        await vm.TogglePlaybackAsync(); await vm.SeekPlaybackAsync(40);
        check(backend.PlaybackCalls.Count == before, "Repeated clicks cannot overlap an in-flight transport request");
        backend.PendingPlaybackControl.SetResult(false);
        await pending;
        check(!vm.IsPlaybackControlBusy && vm.NoticeError && notices == 1, "Rejected transport request reports once and re-enables controls");
        backend.PendingPlaybackControl = null;
        backend.PlaybackControlError = new InvalidOperationException("fixture failure");
        await vm.SeekPlaybackAsync(35);
        check(notices == 2 && !vm.IsPlaybackControlBusy && vm.Notice.Contains("fixture failure"), "Failed transport request reports once without retaining busy state");
        backend.PlaybackControlError = null;

        before = backend.PlaybackCalls.Count;
        backend.State = backend.State with { Generation = backend.State.Generation + 1 };
        await vm.SeekPlaybackAsync(35);
        check(backend.PlaybackCalls.Count == before && notices == 2, "Stale visible song cannot issue a seek against the replacement song");
        backend.PendingPlaybackControl = new TaskCompletionSource<bool>();
        pending = vm.SeekPlaybackAsync(40);
        var replacementPosition = TimeSpan.FromSeconds(12);
        backend.State = backend.State with
        {
            Generation = backend.State.Generation + 1,
            Clock = backend.State.Clock with { EstimatedPosition = replacementPosition }
        };
        vm.Refresh();
        backend.PendingPlaybackControl.SetResult(false);
        await pending;
        check(notices == 2 && vm.PlaybackPositionSeconds == 12 && !vm.IsPlaybackControlBusy,
            "Late old-song failure cannot change new progress or show a stale error");

        backend.PendingPlaybackControl = new TaskCompletionSource<bool>();
        vm.Activate(SettingsPage.Synchronization);
        pending = vm.SeekPlaybackAsync(60);
        vm.Deactivate();
        await pending;
        check(notices == 2 && !vm.IsPlaybackControlBusy && backend.State.Clock.EstimatedPosition == replacementPosition,
            "Leaving the page cancels pending transport without changing playback or displaying an error");
    }
}
