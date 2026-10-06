using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadioButton = System.Windows.Controls.RadioButton;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.App.Windows;
using TaskbarLyrics.Cache;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Playback;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Matching;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> BindingErrors = new();
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var p in new[] { "Themes/Colors.Dark.xaml", "Themes/Typography.xaml", "Themes/Cards.xaml", "Themes/Buttons.xaml", "Themes/Inputs.xaml", "Themes/FontPicker.xaml", "Themes/Navigation.xaml", "Assets/Icons/Logo.xaml", "Assets/Icons/ActionIcons.xaml", "Themes/ScrollBars.xaml", "Themes/Presentation.xaml", "Themes/Playback.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TaskbarLyrics.App;component/" + p, UriKind.Relative) });
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var dispatcher = Dispatcher.CurrentDispatcher;
        var task = Run(args);
        task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
        Dispatcher.Run();
        try { task.GetAwaiter().GetResult(); Console.WriteLine($"Presentation smoke passed: {_checks} checks; no production startup, QQ, GSMTC, startup registration or user-data access."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; }
    private static ConfigService Config(string root) => new(_ => { }, _ => { }, (_, _) => { }, root, watchFile: false, fontExists: _ => true);
    private static async Task Run(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "TaskbarLyrics-ux-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (args.Contains("--shell-only"))
            {
                var index = Array.IndexOf(args, "--screenshots");
                var output = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
                if (output is not null) Directory.CreateDirectory(output);
                await TestShell(output);
                return;
            }
            if (args.Contains("--tray-only"))
            {
                var index = Array.IndexOf(args, "--screenshots");
                var output = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
                if (output is not null) Directory.CreateDirectory(output);
                using var trayConfig = Config(Path.Combine(root, "tray"));
                TestTray(trayConfig, output);
                return;
            }
            LogRetentionChecks.Run(Check, root);
            DistributionChecks.Run(Check, root);
            TestProjection(); TestNavigation(); TestOffsetsAndMotion(); TestConfig(root); TestPersistence(root); TestContrast();
            AlignmentChecks.Run(Check);
            FontPickerChecks.Run(Check);
            await FontPickerPositionChecks.RunAsync(Check);
            ColorEditorChecks.Run(Check);
            KeyboardEditingOnlyChecks.Run(Check);
            TrayKeyboardChecks.Run(Check);
            RecentColorChecks.Run(Check, root);
            LayoutSettingsChecks.Run(Check, root);
            TaskbarRegionChecks.Run(Check);
            ScrollGutterChecks.Run(Check);
            SessionReconnectChecks.Run(Check);
            ColorSpectrumChecks.Run(Check);
            AttentionPulseChecks.Run(Check);
            CachedVersionChecks.Run(Check);
            await TestMediaReadOrdering();
            await MediaPollLoopChecks.RunAsync(Check);
            PreviewChecks.Run(Check);
            OklchPaletteChecks.Run(Check);
            PaletteSettingsChecks.Run(Check, root);
            TimingCalibrationChecks.Run(Check, root);
            await TimingCalibrationChecks.RunPlaybackAsync(Check, root);
            await PlaybackNavigationChecks.RunAsync(Check, root);
            await PlaybackPageChecks.RunAsync(Check, root);
            await SettingsMergeChecks.RunAsync(Check, root);
            await SettingsScrollChecks.RunAsync(Check);
            await SidebarPressChecks.RunAsync(Check);
            await DropdownMotionChecks.RunAsync(Check);
            await DropdownTemplateChecks.RunAsync(Check);
            await ChoiceLayoutChecks.RunAsync(Check, root);
            await UiConsistencyChecks.RunAsync(Check);
            await InteractionMotionChecks.RunAsync(Check);
            await PlaybackControlChecks.RunAsync(Check);
            PlaybackSeekSliderChecks.Run(Check);
            HoverPlaybackProgressChecks.Run(Check);
            using var config = Config(Path.Combine(root, "ui"));
            config.Update(c => { c.General.ReduceMotion = true; c.Lyrics.GlobalOffsetMs = 100; }, scheduleSave: false);
            await TestAsyncFlows(config);
            TestColorRecovery(config);
            config.SaveNow();
            TestControls();
            TestSelectionIndicators();
            await TestFontBinding();
            await TestSmoothProgress();
            var screenshotIndex = Array.IndexOf(args, "--screenshots");
            var outDir = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length ? Path.GetFullPath(args[screenshotIndex + 1]) : null;
            await TestWindows(config, outDir);
            TestTray(config, outDir);
            if (args.Contains("--shell-check")) await TestShell(outDir);
            Check(BindingErrors.Count == 0, "WPF binding errors:\n" + string.Join("\n", BindingErrors.Distinct()));
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("TaskbarLyrics-ux-smoke-")) throw new InvalidOperationException("Unsafe test cleanup path");
            Directory.Delete(absolute, true);
        }
    }
    private static async Task TestMediaReadOrdering()
    {
        var reads = new MediaReadState();
        var first = new TaskCompletionSource<string>();
        var second = new TaskCompletionSource<string>();
        string visible = "old lyrics";
        async Task Read(Task<string> result)
        {
            var revision = reads.Begin();
            visible = "正在读取歌曲…";
            var title = await result;
            if (reads.Complete(revision)) visible = title;
        }
        var a = Read(first.Task);
        Check(reads.IsPending && visible != "old lyrics", "Media event clears previous lyrics before metadata completes");
        var b = Read(second.Task);
        second.SetResult("new song"); await b;
        first.SetResult("old song"); await a;
        Check(!reads.IsPending && visible == "new song", "Late metadata cannot replace the newer song");
        var old = reads.Begin(); var newest = reads.Begin();
        Check(!reads.Complete(old) && reads.IsPending, "Old completion cannot end a newer metadata loading state");
        Check(reads.Complete(newest) && !reads.IsPending, "Newest completion ends loading even when metadata is unchanged");
        var poll = reads.Begin(announceChange:false);
        Check(!reads.IsPending, "Background metadata polls do not blank unchanged lyrics");
        var announced = reads.Begin();
        Check(!reads.Complete(poll) && reads.IsPending, "An old poll cannot clear a newer event's loading cue");
        var latestPoll = reads.Begin(announceChange:false);
        Check(reads.IsPending && !reads.Complete(announced), "New polling reads preserve pending change and reject old event reads");
        Check(reads.Complete(latestPoll) && !reads.IsPending, "Newest poll can complete a pending change");
    }
    public static OverlayUiState Fixture() => new(
        "雨停以后，风还在吹", new TrackIdentity("日光旅行", "陈小满 / 远山乐队", TimeSpan.FromSeconds(246), "沿途的风景"), PlaybackStatus.Playing,
        new(TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(83), TimeSpan.Zero, TimeSpan.Zero, true, 1, true, "fixture"),
        new(83000, 3, "雨停以后，风还在吹", 4, "，", .5, "那些经过的风景", "都留在记忆里面"), 7, false,
        LyricsSourceKind.Cache, LyricsMode.Qrc,
        "雨停以后，风还在吹".Select((c,i) => new QrcWord { Text = c.ToString(), StartMs = 81000+i*400, DurationMs = 400 }).ToArray(),
        false, 4, .5, 100, -50, 50, "fixture-song-mid", "123", "Manual", "qq-fixture", true, true);
    private static void TestContrast()
    {
        static double Luminance(Color c)
        {
            static double Linear(byte b) { var x=b/255d; return x<=.04045 ? x/12.92 : Math.Pow((x+.055)/1.055,2.4); }
            return .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
        }
        static double Ratio(Color a,Color b) { var x=Luminance(a); var y=Luminance(b); return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05); }
        static Color Resource(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;
        foreach(var theme in new[]{"Dark","Light"})
        {
            ThemeManager.Apply(theme,false);
            foreach(var foreground in new[]{"TextPrimaryBrush","TextSecondaryBrush","TextMutedBrush","AccentBrush","DangerBrush","SuccessBrush","WarningBrush"})
                foreach(var background in new[]{"BgBrush","SurfaceBrush","SurfaceAltBrush"})
                    Check(Ratio(Resource(foreground),Resource(background))>=4.5,$"Text contrast below 4.5:1: {theme} {foreground}/{background}");
            Check(Ratio(Resource("AccentTextBrush"),Resource("AccentBrush"))>=4.5,"Primary button text contrast");
            Check(Ratio(Resource("PlaybackLyricBrush"),Resource("SurfaceBrush"))>=3,$"Large playback lyric contrast below 3:1: {theme}");
            Check(Resource("PlaybackLyricBrush")==Resource("PlaybackIndicatorBrush"),$"Playback lyric and progress share an accent: {theme}");
        }
        ThemeManager.Apply("Dark",false);
    }
    private static void TestNavigation()
    {
        var history = new SettingsNavigation();
        Check(!history.CanGoBack && !history.TryGoBack(out _), "No phantom back entry on first page");
        Check(history.Navigate(SettingsPage.Appearance), "Quick action enters appearance");
        Check(!history.Navigate(SettingsPage.Appearance), "Same-page navigation is not duplicated");
        history.Navigate(SettingsPage.General);
        Check(history.TryGoBack(out var appearance) && appearance == SettingsPage.Appearance, "Back restores previous page");
        Check(history.TryGoBack(out var overview) && overview == SettingsPage.Overview && !history.CanGoBack, "Back returns to playback");
        Check(Motion.ShouldAnimate(false,true,false), "Animation enabled when preferences permit");
        Check(!Motion.ShouldAnimate(true,true,false) && !Motion.ShouldAnimate(false,false,false) && !Motion.ShouldAnimate(false,true,true), "Reduced/system/high-contrast animation preferences respected");
    }
    private static void TestProjection()
    {
        var s = Fixture();
        foreach (var (state, expected) in new[] {
            (s, LyricsHealth.Ready), (s with { SessionConnected = false }, LyricsHealth.Disconnected),
            (s with { Track = null }, LyricsHealth.Waiting), (s with { Track = new TrackIdentity(" ","",null) }, LyricsHealth.Waiting), (s with { LyricsLoading = true }, LyricsHealth.Loading),
            (s with { IsInstrumental = true, HasLyrics = false }, LyricsHealth.Instrumental),
            (s with { NeedsRematch = true, HasLyrics = false }, LyricsHealth.NeedsRematch),
            (s with { Error = "offline", HasLyrics = false }, LyricsHealth.Error),
            (s with { HasLyrics = false }, LyricsHealth.NoLyrics),
            (s with { HasLyrics = false, Error = "provider payload missing", NoTimedLyrics = true }, LyricsHealth.NoLyrics) }) Check(PlaybackPresentation.From(state).Health == expected, "Health projection: " + expected);
        var missing = new QQMusicLyricsResult { LrcDetail = new LrcFetchDetail { RequestSuccess=true, ApiCode=-1901 }, QrcDetail = new QrcFetchDetail { RequestSuccess=true, XmlParsed=true } };
        Check(missing.HasConfirmedNoLyrics,"Successful empty QQ responses are not network failures");
        missing.QrcDetail.RequestSuccess=false; Check(!missing.HasConfirmedNoLyrics,"Network failure is not classified as missing lyrics");
        Check(!PlaybackPresentation.From(s with { SessionConnected=false }).HasTrack,"Disconnected session cannot advertise active playback");
        Check(PlaybackPresentation.FormatTime(TimeSpan.FromSeconds(246)) == "04:06", "Duration format");
        Check(PlaybackPresentation.From(s with { Status = PlaybackStatus.Paused }).PlaybackLabel == "已暂停", "Pause label");
        Check(PlaybackPresentation.From(s).MatchLabel == "手动匹配", "Manual label");
        Check(PlaybackPresentation.From(s with { CacheKey = null }).CanAdjustTrack == false, "Unbound offset disabled");
    }
    private static void TestOffsetsAndMotion()
    {
        var bounded = WindowPlacement.Constrain(new Rect(-2000,-100,2500,1400),new Rect(-1920,0,1920,1040));
        Check(bounded == new Rect(-1920,0,1920,1040), "Oversized window fits negative-origin monitor work area");
        Check(WindowPlacement.Constrain(new Rect(400,400,600,500),new Rect(0,0,800,600)) == new Rect(200,100,600,500), "Window position clamps without resizing");
        Check(OffsetAdjustment.Add(200,100) == 300, "Offsets must accumulate");
        Check(OffsetAdjustment.Add(200,-100) == 100, "Delay decreases offset");
        Check(OffsetAdjustment.Add(4999,100) == 5000 && OffsetAdjustment.Add(-4999,-100) == -5000, "Offset limits");
        Check(OffsetAdjustment.Add(long.MaxValue, long.MaxValue) == 5000, "Offset overflow safety");
        Check(!OffsetAdjustment.Matches(7,"a",8,"b"), "Old context rejected");
        Check(LineTransitionPolicy.ShouldAnimate(7,7,2,3,1000,1033,false,false,false), "Natural next line may animate");
        Check(!LineTransitionPolicy.ShouldAnimate(7,8,2,3,1000,1033,false,false,false), "Track change does not animate");
        Check(!LineTransitionPolicy.ShouldAnimate(7,7,2,3,1000,1250,false,false,false), "Seek boundary does not animate");
        Check(!LineTransitionPolicy.ShouldAnimate(7,7,2,2,1000,1033,false,false,false), "Word ticks do not animate");
        Check(!LineTransitionPolicy.ShouldAnimate(7,7,2,3,1000,1033,true,false,false), "Scrub does not animate");
        Check(!LineTransitionPolicy.ShouldAnimate(7,7,2,3,1000,1033,false,false,true), "Reduced motion respected");
    }
    private static async Task TestSmoothProgress()
    {
        Check(SmoothProgressBar.ShouldSmooth(10,10.1,true,true,true),"Normal playback interpolates");
        Check(!SmoothProgressBar.ShouldSmooth(10,9,true,true,true) && !SmoothProgressBar.ShouldSmooth(10,12,true,true,true),"Backwards and large seeks snap");
        Check(!SmoothProgressBar.ShouldSmooth(10,10.1,false,true,true) && !SmoothProgressBar.ShouldSmooth(10,10.1,true,false,true) && !SmoothProgressBar.ShouldSmooth(10,10.1,true,true,false),"Paused, hidden and reduced-motion states do not interpolate");
        var bar=new SmoothProgressBar { Height=5, Width=300, TrackKey="first", IsPlaying=true, TargetValue=10 };
        var host=new Window { Content=bar, Width=340, Height=80, ShowInTaskbar=false, ShowActivated=false, Left=-20000, Top=-20000, WindowStartupLocation=WindowStartupLocation.Manual };
        host.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        try
        {
            bar.TargetValue=10.2;
            Check(bar.HasAnimatedProperties==Motion.Allowed(bar),"Actual progress animation follows animation preference");
            Check((double)bar.GetAnimationBaseValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty)==10.2,"Progress commits exact target under interpolation");
            bar.IsPlaying=false; Check(!bar.HasAnimatedProperties && bar.Value==10.2,"Pause cancels interpolation");
            bar.IsPlaying=true; bar.TargetValue=10.4; bar.TrackKey="next";
            Check(!bar.HasAnimatedProperties && bar.Value==10.4,"Track identity change cancels stale interpolation");
            bar.TargetValue=80; Check(!bar.HasAnimatedProperties && bar.Value==80,"Large seek updates immediately");
            bar.TargetValue=0; Check(!bar.HasAnimatedProperties && bar.Value==0,"Backward seek updates immediately");
            Motion.SetReduce(bar,true); bar.TargetValue=.2; Check(!bar.HasAnimatedProperties && bar.Value==.2,"Reduced motion uses exact progress");
            Motion.SetReduce(bar,false); bar.TargetValue=.4; host.Hide();
            Check(!bar.HasAnimatedProperties && bar.Value==.4,"Hiding window removes progress animation");
        }
        finally { host.Close(); }
    }

    private static void TestConfig(string root)
    {
        var defaults=AppConfig.CreateDefault();
        Check(typeof(FontConfig).GetProperties().All(p => Equals(p.GetValue(defaults.Display.Fonts), "Microsoft YaHei UI")),
            "New installations use system UI fonts for every lyric language");
        var fontDefaultsDir = Path.Combine(root, "font-defaults-only");
        using (var fontConfig = Config(fontDefaultsDir))
        {
            fontConfig.Update(c => { c.Display.Fonts.Chinese = "Resource Han Rounded CN VF"; c.Display.Fonts.Korean = "Resource Han Rounded KR VF"; }, scheduleSave: false);
            fontConfig.SaveNow();
        }
        using (var fontConfig = Config(fontDefaultsDir))
        {
            Check(fontConfig.Current.Display.Fonts.Chinese == "Resource Han Rounded CN VF" && fontConfig.Current.Display.Fonts.Korean == "Resource Han Rounded KR VF",
                "Changing defaults preserves saved custom font selections");
            fontConfig.ResetAppearanceToDefaults();
            Check(typeof(FontConfig).GetProperties().All(p => Equals(p.GetValue(fontConfig.Current.Display.Fonts), FontConfig.DefaultFamily)),
                "Explicit appearance reset uses system UI font defaults");
        }
        using (var fallbackConfig = new ConfigService(_ => { }, _ => { }, (_, _) => { }, Path.Combine(root, "font-system-fallback"), watchFile: false, fontExists: f => f == "Segoe UI"))
            Check(fallbackConfig.Current.Display.Fonts.Chinese == "Segoe UI" && fallbackConfig.Current.Display.Fonts.Korean == "Segoe UI",
                "Missing preferred system font falls back to Segoe UI without bundled fonts");

        Check(defaults.Display.NormalColor=="#FF496DBF" && defaults.Display.HighlightColor=="#FFA0CCEE", "New installations use requested blue lyric defaults");
        Check(LyricColorScheme.Default.Normal=="#FF496DBF" && LyricColorScheme.Default.Highlight=="#FFA0CCEE", "Reset colors uses new defaults");
        TestRetiredThemeMigration(root);
        var startupDir = Path.Combine(root,"startup-save-failure"); Directory.CreateDirectory(startupDir);
        File.WriteAllText(Path.Combine(startupDir,"config.json"),"{}");
        Directory.CreateDirectory(Path.Combine(startupDir,"config.json.tmp"));
        using (var startup = Config(startupDir))
        {
            Check(startup.SaveState.Phase == SavePhase.Failed,"Startup normalization write failure is visible");
            Directory.Delete(Path.Combine(startupDir,"config.json.tmp"));
        }
        var dir = Path.Combine(root, "legacy"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"config.json"), "{\"version\":1,\"display\":{\"fontSize\":22,\"highlightColor\":\"#FF112233\"},\"general\":{\"theme\":\"Light\"},\"lyrics\":{\"globalOffsetMs\":321}}");
        using (var c = Config(dir))
        {
            Check(c.Current.Display.FontSize == 22 && c.Current.Display.HighlightColor == "#FF112233", "Legacy appearance preserved");
            Check(c.Current.General.Theme == "Light" && !c.Current.General.ReduceMotion && c.Current.Lyrics.GlobalOffsetMs == 321, "Optional preference backwards compatible");
            var old = c.Current;
            c.Update(x => x.Display.FontSize = 24, scheduleSave:false);
            Check(old.Display.FontSize == 22, "Published config snapshots are not mutated");
            c.ScheduleSave(); Check(c.SaveState.Phase == SavePhase.Pending, "Pending save feedback");
            c.SaveNow(); var rev = c.SaveState.Revision; Check(c.SaveState.Phase == SavePhase.Saved, "Successful save feedback");
            c.Update(x => x.Lyrics.GlobalOffsetMs = 999, scheduleSave:false); c.SaveNow();
            Check(c.SaveState.Revision > rev && File.ReadAllText(c.ConfigPath).Contains("999"), "Newest revision persisted");
            Directory.CreateDirectory(c.ConfigPath + ".tmp");
            try { c.SaveNow(); throw new Exception("Expected write failure"); } catch (UnauthorizedAccessException) { }
            Check(c.SaveState.Phase == SavePhase.Failed && c.SaveState.Error is not null, "Write failure is not reported as saved");
            Directory.Delete(c.ConfigPath + ".tmp"); c.SaveNow(); Check(c.SaveState.Phase == SavePhase.Saved, "Retry save succeeds");
            c.Update(x => x.General.ReduceMotion = true);
        }
        using (var c = Config(dir)) Check(c.Current.General.ReduceMotion, "Dispose flushes pending changes");
        var discardDir = Path.Combine(root,"discard");
        using (var c = Config(discardDir)) { c.Update(x => x.Lyrics.GlobalOffsetMs = 777); c.DisposeWithoutSaving(); }
        using (var c = Config(discardDir)) Check(c.Current.Lyrics.GlobalOffsetMs == 0, "Explicit discard cancels pending persistence");
        var bad = Path.Combine(root,"invalid"); Directory.CreateDirectory(bad); File.WriteAllText(Path.Combine(bad,"config.json"),"broken");
        using (var c = Config(bad)) Check(Directory.GetFiles(bad,"config.invalid-*.json").Length == 1, "Corrupt config backed up inside isolated root");
    }
    private static void TestRetiredThemeMigration(string root)
    {
        var dir = Path.Combine(root, "retired-theme");
        string expected;
        string path;
        using (var config = Config(dir))
        {
            config.Update(c =>
            {
                c.General.Theme = "FollowSystem";
                c.General.ReduceMotion = true;
                c.General.RecentNormalColors = ["#FF13579B", "#FF2468AC"];
                c.General.RecentHighlightColors = ["#FFAABBCC"];
                c.General.RecentShadowColors = ["#80554433"];
                c.Display.Fonts.Chinese = "Resource Han Rounded CN VF";
                c.Display.Fonts.Korean = "Resource Han Rounded KR VF";
                c.Display.FontSize = 23;
                c.Display.NormalColor = "#FF13579B";
                c.Display.HighlightColor = "#FFAABBCC";
                c.Display.ShadowColor = "#80554433";
                c.Overlay.LeftMarginPx = 17;
                c.Overlay.RightSafetyMarginPx = 38;
                c.Lyrics.GlobalOffsetMs = 275;
            }, scheduleSave: false);
            config.SaveNow();
            expected = JsonSerializer.Serialize(config.Current);
            path = config.ConfigPath;
        }
        var retired = JsonSerializer.Deserialize<AppConfig>(expected)!;
        retired.General.Theme = "Glass";
        File.WriteAllText(path, JsonSerializer.Serialize(retired));
        using (var migrated = Config(dir))
        {
            Check(migrated.Current.General.Theme == "FollowSystem", "Retired Glass preference migrates to FollowSystem");
            Check(JsonSerializer.Serialize(migrated.Current) == expected,
                "Retiring glass preserves fonts, colors, recent colors, layout, offsets and all other preferences");
        }
        using (var reloaded = Config(dir))
            Check(JsonSerializer.Serialize(reloaded.Current) == expected, "Retired theme migration persists without resetting unrelated preferences");
    }

    private static void TestPersistence(string root)
    {
        var settingsPath = Path.Combine(root,"store-offsets.json");
        var offsets = new TrackSettingsStore(new SilentLogger(), settingsPath);
        offsets.SetTrackOffsetMs("song",100,true);
        Check(new TrackSettingsStore(new SilentLogger(),settingsPath).GetTrackOffsetMs("song") == 100,"Per-track offset round trip");
        Directory.CreateDirectory(settingsPath + ".tmp");
        try { offsets.SetTrackOffsetMs("song",200,true); throw new Exception("Expected strict offset failure"); } catch(UnauthorizedAccessException) { }
        Check(offsets.GetTrackOffsetMs("song") == 100,"Failed offset persistence restores prior value");
        try { offsets.ClearAllOffsets(true); throw new Exception("Expected strict reset failure"); } catch(UnauthorizedAccessException) { }
        Check(offsets.Count == 1,"Failed offset reset preserves entries");
        Directory.Delete(settingsPath + ".tmp");
        offsets.ClearAllOffsets(true); Check(new TrackSettingsStore(new SilentLogger(),settingsPath).Count == 0,"Offset reset persists");
        var lookupPath = Path.Combine(root,"store-matches.json");
        var lookup = new TrackLookupCache(new SilentLogger(),lookupPath);
        var original = new QQSongIdentity("1","original","song","artist","album",200);
        lookup.SaveManual("song","artist",200,original,true);
        lookup.SaveAutomatic("song","artist",200,original with { SongMid="automatic" },100);
        Check(lookup.TryGet("song","artist",200)?.SongMid == "original","Automatic match cannot overwrite Manual");
        Directory.CreateDirectory(lookupPath + ".tmp");
        try { lookup.SaveManual("song","artist",200,original with { SongMid="replacement" },true); throw new Exception("Expected manual save failure"); } catch(UnauthorizedAccessException) { }
        Check(lookup.TryGet("song","artist",200)?.SongMid == "original","Failed manual rebind preserves old binding");
        try { lookup.ClearAllManual(true); throw new Exception("Expected manual reset failure"); } catch(UnauthorizedAccessException) { }
        Check(lookup.ManualCount == 1,"Failed manual reset preserves binding");
        Directory.Delete(lookupPath + ".tmp");
        lookup.ClearAllManual(true); Check(new TrackLookupCache(new SilentLogger(),lookupPath).ManualCount == 0,"Manual reset persists");
        var lyricsRoot=Path.Combine(root,"lyrics-only"); Directory.CreateDirectory(Path.Combine(lyricsRoot,"qq-a")); File.WriteAllText(Path.Combine(lyricsRoot,"qq-a","data.json"),"fixture");
        CacheStatisticsService.ClearAllLyricsCacheStrict(lyricsRoot);
        Check(Directory.GetDirectories(lyricsRoot).Length == 0 && File.Exists(lookupPath) && File.Exists(settingsPath),"Lyrics cleanup retains independent matches and offsets");
    }

    private static async Task TestAsyncFlows(ConfigService config)
    {
        var backend = new FakeSettings(); using var vm = new SettingsViewModel(config, backend);
        vm.Activate(SettingsPage.Overview);
        vm.TrackOffset = 100; Check(backend.State.TrackOffsetMs == 100, "Per-track writes preserve context");
        backend.FailOffset = true; vm.TrackOffset = 150; Check(vm.NoticeError, "Rejected offset produces inline feedback"); backend.FailOffset = false;
        backend.PendingOperation = new(); var first = vm.RunOperationAsync(SettingsOperation.Reload);
        await vm.RunOperationAsync(SettingsOperation.Reload); Check(backend.Operations == 1, "Duplicate operation blocked");
        vm.Deactivate(); backend.PendingOperation.SetResult(); await first; Check(!vm.IsBusy, "Cancelled view releases busy state");
        var rematch = new FakeRematch(); using var match = new RematchViewModel(rematch);
        var a = match.SearchAsync(true); match.Query = "new query"; var b = match.SearchAsync(true);
        rematch.Requests[1].Source.SetResult(([Song("new")],true,null)); await b;
        rematch.Requests[0].Source.SetResult(([Song("old")],false,null)); await a;
        Check(match.Candidates.Count == 1 && match.Candidates[0].Title == "new", "Late search cannot overwrite new query");
        var moreFail = match.SearchAsync(false); rematch.Requests[^1].Source.SetResult(([],false,"offline")); await moreFail;
        var retry = match.SearchAsync(false); Check(rematch.Requests[^1].Page == 2, "Failed pagination retries same page"); rematch.Requests[^1].Source.SetResult(([Song("new"),Song("second")],false,null)); await retry;
        Check(match.Candidates.Count == 2, "Candidate deduplication");
        match.Selected = match.Candidates[0]; var pending = match.SearchAsync(true); rematch.SwitchTrack();
        rematch.Requests[^1].Source.SetResult(([Song("stale")],true,null)); await pending;
        Check(match.IsStale && !match.CanApply, "Track switch cancels search and apply");
        var closingBackend = new FakeRematch(); var closing = new RematchViewModel(closingBackend); var search = closing.SearchAsync(true); closing.Dispose(); closingBackend.Requests[0].Source.SetResult(([Song("closed")],false,null)); await search;
        Check(closing.Candidates.Count == 0, "Closed search cannot populate UI");
        var successfulBackend = new FakeRematch(); using var successful = new RematchViewModel(successfulBackend);
        var successfulLoad = successful.SearchAsync(true); successfulBackend.Requests[0].Source.SetResult(([Song("selected")],false,null)); await successfulLoad;
        successful.Selected = successful.Candidates[0]; var appliedEvents = 0; successful.Applied += () => appliedEvents++;
        var successTask = successful.ApplyAsync(); successfulBackend.ApplyCompletion.SetResult(true); await successTask;
        Check(appliedEvents == 1 && successfulBackend.ApplyCount == 1,"One explicit selection completes the apply flow without a Reload action");
        var applyBackend = new FakeRematch(); using var apply = new RematchViewModel(applyBackend);
        var load = apply.SearchAsync(true); applyBackend.Requests[0].Source.SetResult(([Song("selection")],false,null)); await load;
        apply.Selected = apply.Candidates[0]; var applyTask = apply.ApplyAsync(); await apply.ApplyAsync(); Check(applyBackend.ApplyCount == 1, "Duplicate manual apply blocked");
        applyBackend.SwitchTrack(); applyBackend.ApplyCompletion.SetResult(true); await applyTask; Check(apply.IsStale, "Changed track cannot report manual apply as current success");
    }
    private static QQSongCandidate Song(string title) => new() { Title = title, SongMid = title, Artists = "陈小满", Album = "沿途的风景", DurationSeconds = 246, MatchScore = 93.4, Confidence = MatchConfidence.High };
    private static void TestControls()
    {
        var n = new NumberEditor { Value = 200, Step = 100, OffsetMode = true, ContextKey = "old" };
        ((Button)n.FindName("PlusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(n.Value == 300, "Actual plus button accumulates");
        ((Button)n.FindName("MinusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(n.Value == 200, "Actual minus button accumulates");
        var box = (TextBox)n.FindName("Input");
        box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,null,box) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
        n.ContextKey = "new"; box.Text = "400"; Check(!n.Commit() && n.Value == 200, "Editing old song cannot commit to new song");
        Check(ColorEditor.TryParse("#80245BC1",out var c) && c.A == 128, "ARGB color accepted");
        Check(!ColorEditor.TryParse("garbage",out _) && !ColorEditor.TryParse("#12",out _), "Invalid color rejected");
    }
    private static void TestSelectionIndicators()
    {
        foreach (var theme in new[] { "Dark", "Light", "HighContrast" })
        {
            ThemeManager.Apply(theme == "HighContrast" ? "Dark" : theme, highContrastOverride: theme == "HighContrast");
            foreach (var kind in new[] { "Navigation", "Candidate", "ComboBox" })
            {
                System.Windows.Controls.Primitives.Selector selector = kind == "ComboBox" ? new ComboBox() : new ListBox();
                System.Windows.Controls.Control control = kind == "ComboBox"
                    ? new ComboBoxItem { Content = "Selection fixture" }
                    : new ListBoxItem { Content = "Selection fixture", DataContext = new { Title = "Selection fixture" } };
                if (kind == "Candidate") control.Style = (Style)Application.Current.FindResource("CandidateListBoxItem");
                else control.Style = (Style)Application.Current.FindResource(control.GetType());
                selector.Items.Add(control);
                control.Width = 220; control.Height = 48;
                control.ApplyTemplate();
                control.Measure(new Size(220, 48)); control.Arrange(new Rect(0, 0, 220, 48));
                var outline = (Border)control.Template.FindName("SelectionOutline", control);
                Check(outline.Visibility == Visibility.Collapsed, $"{theme}/{kind}: unselected row has no outline");
                selector.SelectedIndex = 0;
                control.UpdateLayout();
                Check(outline.Visibility == Visibility.Visible && !outline.IsHitTestVisible && outline.BorderThickness.Left >= 1,
                    $"{theme}/{kind}: selected row retains a noninteractive outline without keyboard focus");
                var brush = (SolidColorBrush)outline.BorderBrush;
                Check(theme == "HighContrast" ? brush.Color == System.Windows.SystemColors.WindowTextColor : brush.Color.A == 0,
                    $"{theme}/{kind}: selected outline contrasts in high contrast and preserves ordinary themes");
                Check(outline.ActualWidth > 0 && outline.ActualHeight > 0 && control.ActualWidth == 220 && control.ActualHeight == 48,
                    $"{theme}/{kind}: selection does not change row geometry");
                selector.SelectedIndex = -1;
                Check(outline.Visibility == Visibility.Collapsed, $"{theme}/{kind}: clearing selection removes outline");
            }
        }
        ThemeManager.Apply("Dark", highContrastOverride: false);
    }
    private sealed class FontChoice { public string Name { get; set; } = "Segoe UI"; }
    private static async Task TestFontBinding()
    {
        var choice = new FontChoice();
        var picker = new FontPicker { CatalogProvider = () => Task.FromResult(new[]{"Segoe UI","Microsoft YaHei UI"}) };
        picker.SetBinding(ComboBox.TextProperty,new System.Windows.Data.Binding(nameof(FontChoice.Name)) { Source=choice, Mode=System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger=System.Windows.Data.UpdateSourceTrigger.LostFocus });
        await picker.EnsureCatalogAsync();
        Check(System.Windows.Data.BindingOperations.IsDataBound(picker,ComboBox.TextProperty),"Loading font catalog preserves setting binding");
        picker.SelectedItem="Microsoft YaHei UI";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(choice.Name=="Microsoft YaHei UI","Explicit font choice commits immediately");
        var editable = new FontPicker { IsEditable=true, CatalogProvider=()=>Task.FromResult(new[]{"Segoe UI"}) };
        editable.ApplyTemplate();
        var text = (TextBox)editable.Template.FindName("PART_EditableTextBox",editable);
        Check(ComboBoxInteraction.GetOpenOnClick(editable),"Font picker inherits whole-field click behavior");
        Check(ComboBoxInteraction.OpenEditableField(editable,text,1),"Clicking editable field opens the list");
        Check(!text.IsReadOnly,"Editable font field retains text editing");
        editable.SetCurrentValue(ComboBox.IsDropDownOpenProperty,false);
        Check(!ComboBoxInteraction.OpenEditableField(editable,text,2),"Double click remains available for text selection");
    }
    private static async Task TestWindows(ConfigService config, string? outDir)
    {
        if (outDir is not null) Directory.CreateDirectory(outDir);
        var backend = new FakeSettings { GlobalOffset = () => config.Current.Lyrics.GlobalOffsetMs }; using var vm = new SettingsViewModel(config,backend);
        var window = new SettingsWindow(vm, constrainToScreen:false) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            await TestMergedSettings(window, config, outDir, backend);
            if (outDir is not null)
        {
            var taskbarCard=PlaybackTaskbarPreview.Render(vm.State,vm.PlaybackButtonLabel,640,340,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(taskbarCard));
            using var output=File.Create(Path.Combine(outDir,"Taskbar-Playback-Card.png")); encoder.Save(output);
        }
        await TestWindowNavigation(window);
            await TestCalibrationScroll(window, outDir, backend);
            await TestAppearanceSections(window, config, outDir, backend, vm);
            await TestPaletteUi(window, config, vm, outDir);
            foreach (var theme in new[] { "Dark", "Light", "FollowSystem", "HighContrast" })
            {
                config.Update(c => c.General.Theme = theme == "HighContrast" ? "Dark" : theme, scheduleSave:false);
                config.SaveNow();
                ThemeManager.Apply(theme == "HighContrast" ? "Dark" : theme, highContrastOverride:theme == "HighContrast", systemLightOverride:false);
                foreach (var page in Enum.GetValues<SettingsPage>().Where(p=>p!=SettingsPage.Data))
                {
                    window.Navigate(page); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    if(page==SettingsPage.Overview)
                    {
                        var navigation=(ListBox)window.FindName("NavList");
                        var item=(ListBoxItem)navigation.SelectedItem;
                        var rootBorder=(Border)item.Template.FindName("Root",item);
                        item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent=Mouse.PreviewMouseDownEvent });
                        Check(SidebarPress.GetIsPressed(item) && ((SolidColorBrush)rootBorder.Background).Color==((SolidColorBrush)window.FindResource("SurfacePressedBrush")).Color,
                            "Real sidebar press uses the shared pressed surface: "+theme);
                        if(outDir is not null) Capture(window,Path.Combine(outDir,$"Sidebar-Pressed-{theme}.png"),1.5);
                        window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent=Mouse.PreviewMouseUpEvent });
                        Check(!SidebarPress.GetIsPressed(item) && ((SolidColorBrush)rootBorder.Background).Color==((SolidColorBrush)window.FindResource("NavSelectedBrush")).Color,
                            "Releasing sidebar press restores selected feedback: "+theme);
                    }
                    TestControlGeometry(window, page);
                    Check(window.ActualWidth > 0 && window.ActualHeight > 0, "Window layout: " + theme + page);
                    foreach (var scroll in Descendants(window).OfType<ScrollViewer>().Where(s => s.ActualWidth > 0))
                        Check(scroll.ExtentWidth <= scroll.ViewportWidth + 2 || scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden, "Unexpected horizontal overflow: " + theme + page);
                    if (outDir is not null)
                        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d }) Capture(window, Path.Combine(outDir,$"{theme}-{page}-{scale*100:0}.png"),scale);
                    if(page==SettingsPage.About)
                    {
                        var width=window.Width; var height=window.Height;
                        window.Width=window.MinWidth; window.Height=window.MinHeight;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                        if(outDir is not null) Capture(window,Path.Combine(outDir,$"About-Narrow-{theme}.png"),1.5);
                        window.Width=width; window.Height=height;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    }
                }
            }
            ThemeManager.Apply("Dark",false);
            backend.State = Fixture() with { Track = new TrackIdentity("Levitating", "Dua Lipa", TimeSpan.FromSeconds(217), "Future Nostalgia (Explicit)"), DisplayText = "You met me at the perfect time", Clock = Fixture().Clock with { EstimatedPosition = TimeSpan.FromSeconds(27) } };
            vm.Refresh(); window.Navigate(SettingsPage.Overview);
            window.Width = 1040; window.Height = 690;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            if (outDir is not null) Capture(window, Path.Combine(outDir,"Reference-Overview-Dark.png"),1.5);
            ThemeManager.Apply("Light",false);
            if (outDir is not null) Capture(window, Path.Combine(outDir,"Reference-Overview-Light.png"),1.5);
            if (outDir is not null)
            {
                var sidebar=(CheckBox)window.FindName("OverlayToggle");
                var png=new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(RenderElementRaster(sidebar,sidebar,2)));
                using var output=File.Create(Path.Combine(outDir,"Sidebar-Alignment-Light.png"));
                png.Save(output);
            }
            var overviewHost=(ContentControl)window.FindName("PageHost");
            var rematchAction=Descendants(overviewHost).OfType<Button>().Single(b=>b.Content is "选择歌词");
            rematchAction.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice,0){ RoutedEvent=Mouse.MouseEnterEvent });
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Reference-Overview-Light-Hover.png"),1.5);
            rematchAction.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice,0){ RoutedEvent=Mouse.MouseLeaveEvent });
            var card=(Border)((UserControl)overviewHost.Content).FindName("PlaybackCard");
            var progress=(PlaybackSeekSlider)((UserControl)overviewHost.Content).FindName("SongProgress");
            var hoverKey=(DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            progress.SetValue(hoverKey,true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Reference-Overview-Seek-Hover.png"),1.5);
            progress.SetValue(hoverKey,false);
            await TestScrollingReader(window, vm, backend, outDir);
            var cardHeight=card.ActualHeight; var windowHeight=window.ActualHeight;
            var progressY=progress.TransformToAncestor(card).Transform(new Point()).Y;
            var previousState=backend.State;
            backend.State=previousState with { HasLyrics=false, NeedsRematch=true, DisplayText="♪ 波光 (Gold Dust)" };
            vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var attention=Descendants(rematchAction).OfType<AttentionPulse>().Single();
            Check(attention.IsActive && attention.Opacity>0,"Unmatched playback activates the bound button cue");
            Check(!Descendants(overviewHost).OfType<TextBlock>().Any(t=>t.IsVisible && t.Text.Contains("请选择歌词版本")),"Unmatched overview removes the old instruction");
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Reference-Overview-Unmatched.png"),1);
            backend.State=previousState; vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!attention.IsActive && attention.Opacity==0,"Successful playback clears the bound button cue");
            backend.State=previousState with { DisplayText=string.Join("\n",Enumerable.Repeat("A very long lyric line · 多语言歌词不会撑高窗口",12)) };
            vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(Math.Abs(card.ActualHeight-cardHeight)<1 && Math.Abs(window.ActualHeight-windowHeight)<1,"Long lyrics do not resize window or card");
            Check(Math.Abs(progress.TransformToAncestor(card).Transform(new Point()).Y-progressY)<1,"Progress bar stays anchored as lyric length changes");
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Reference-Overview-FixedHeight.png"),1);
            backend.State=previousState; vm.Refresh();
            window.Width = 760; window.Height = 480;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            foreach(var scroll in Descendants(window).OfType<ScrollViewer>().Where(s=>s.ActualWidth>0))
                Check(scroll.ExtentWidth <= scroll.ViewportWidth + 2, "Narrow playback has no horizontal overflow");
            if (outDir is not null) Capture(window, Path.Combine(outDir,"Reference-Overview-Narrow.png"),1);
            backend.State=Fixture(); vm.Refresh(); window.Width=1040; window.Height=720;
            await TestExpansionAndAnimation(window, outDir);
            await TestRealAnimationTriggers(window, config, outDir);
            ThemeManager.Apply("Dark",false);
            window.Width = 800; window.Height = 600; window.Navigate(SettingsPage.Appearance); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            if (outDir is not null) Capture(window,Path.Combine(outDir,"Dark-Appearance-narrow.png"),1);
            var preview = Descendants(window).OfType<LyricsPreview>().Single();
            window.Hide(); Check(!preview.IsPreviewRunning,"Preview stops when window is hidden");
            window.Show(); window.Width=1040; window.Height=720;
            backend.State = Fixture() with { Track = null, SessionConnected = false, HasLyrics = false, DisplayText = "", LyricsMode=LyricsMode.None, LyricsSource=LyricsSourceKind.None, MatchSource="None", Status=PlaybackStatus.Closed }; vm.Refresh(); window.Navigate(SettingsPage.Overview); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Dark-Overview-empty.png"),1);
            backend.State = Fixture() with { Track = new TrackIdentity("風になる · 바람이 분다 · مرحبًا بالعالم · A very long song title that must remain readable", "歌手 / アーティスト / 아티스트",TimeSpan.FromSeconds(245)), HasLyrics = false, Error = "fixture network unavailable", DisplayText="♪ 歌词尚未加载", LyricsMode=LyricsMode.None, LyricsSource=LyricsSourceKind.None }; vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Dark-Overview-error-long.png"),1);
        }
        finally { window.AllowClose=true; window.Close(); }
        ThemeManager.Apply("Light",false);
        var colors = new ColorEditor { Hex="#80245BC1" };
        var popup = (System.Windows.Controls.Primitives.Popup)colors.FindName("Picker");
        var panel = (FrameworkElement)popup.Child;
        panel.Measure(new Size(296,double.PositiveInfinity)); panel.Arrange(new Rect(panel.DesiredSize)); panel.UpdateLayout();
        if(outDir is not null) CaptureElement(panel,Path.Combine(outDir,"Light-ColorEditor.png"),1);
        ThemeManager.Apply("Dark",false);
        var rb = new FakeRematch(); using var rm = new RematchViewModel(rb);
        var load = rm.SearchAsync(true); rb.Requests[0].Source.SetResult(([Song("日光旅行"),Song("日光旅行 (Live)"),Song("日光旅行 · Acoustic")],false,null)); await load;
        var rw = new LyricsRematchWindow(rm, autoSearch:false, constrainToScreen:false) { WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false };
        rw.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var searchBox=(TextBox)rw.FindName("SearchBox"); var searchButton=(Button)rw.FindName("SearchButton");
        Check(Math.Abs(searchBox.ActualHeight-searchButton.ActualHeight)<1 && Math.Abs(searchButton.ActualHeight-searchButton.ActualWidth)<1,"Search icon is square and matches input height");
        Check(rm.ApplyLabel=="应用","Manual selection action is labeled Apply");
        if(outDir is not null) Capture(rw,Path.Combine(outDir,"Dark-Rematch.png"),1);
        rw.Close();
    }
    private static async Task TestPaletteUi(SettingsWindow window, ConfigService config, SettingsViewModel vm, string? outDir)
    {
        window.Navigate(SettingsPage.Appearance);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var page=(UserControl)((ContentControl)window.FindName("PageHost")).Content;
        ((RadioButton)page.FindName("ColorsTab")).IsChecked=true;
        var slider=(PaletteHueSlider)page.FindName("PaletteHueControl");
        var preview=(LyricsPreview)page.FindName("Preview");
        var original=LyricColorScheme.Capture(config.Current);
        var hue=config.Current.Display.PaletteHue;
        var fontSize=config.Current.Display.FontSize;
        var recent=vm.RecentNormalColors.ToArray();
        foreach(var preset in PalettePreset.All)
        {
            slider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty,preset.Hue);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var expected=OklchLyricPalette.Generate(preset.Hue);
            Check(vm.NormalColor==expected.NormalColor && vm.HighlightColor==expected.HighlightColor,
                "Hue slider updates both colors through its real binding: "+preset.Name);
            foreach(var light in new[]{true,false})
            {
                ((RadioButton)preview.FindName(light ? "LightBackgroundButton" : "DarkBackgroundButton")).IsChecked=true;
                preview.RenderFrame(1800);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Check(preview.Config!.Display.NormalColor==expected.NormalColor && preview.Config.Display.HighlightColor==expected.HighlightColor
                    && preview.Config.Display.FontSize==fontSize && preview.Config.Display.ShadowEnabled==original.ShadowEnabled,
                    "Both preview backgrounds preserve exact palette and real rendering settings");
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"Palette-{preset.Name}-{(light ? "Light" : "Dark")}.png"),1.5);
            }
        }
        Check(vm.RecentNormalColors.SequenceEqual(recent),"Dragging hue does not flood recent colors with intermediate values");
        config.Update(c=>{original.Apply(c);c.Display.PaletteHue=hue;},scheduleSave:false);
    }

    private static async Task TestMergedSettings(SettingsWindow window, ConfigService config, string? outDir, FakeSettings backend)
    {
        var previousPage=window.CurrentPage;
        var previousTheme=config.Current.General.Theme;
        var previousReduceMotion=config.Current.General.ReduceMotion;
        var previousStartup=config.Current.General.StartWithWindows;
        var previousBackendStartup=backend.StartupEnabled;
        var previousThemePreference=ThemeManager.CurrentPreference;
        var previousHighContrast=ThemeManager.IsHighContrast;
        var previousDark=ThemeManager.IsDark;
        var previousWidth=window.Width;
        var previousHeight=window.Height;
        var previousState=backend.State;
        var previousOperations=backend.Operations;
        Expander? maintenance=null;
        try
        {
            Check(window.ViewModel.CacheCount=="—", "Cache statistics start unloaded before opening merged settings");
            window.Navigate(SettingsPage.Appearance);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var appearance=(UserControl)((ContentControl)window.FindName("PageHost")).Content;
            var appearanceCard=(Border)appearance.FindName("AppearanceCard");
            var appearanceOrigin=appearanceCard.TranslatePoint(new Point(),appearance);
            var appearanceWidth=appearanceCard.ActualWidth;
            window.Navigate(SettingsPage.Data);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var host=(ContentControl)window.FindName("PageHost");
            var page=(UserControl)host.Content;
            var nav=(ListBox)window.FindName("NavList");
            Check(window.CurrentPage==SettingsPage.General && window.ViewModel.Page==SettingsPage.General
                && ((ListBoxItem)nav.SelectedItem).Tag as string=="General",
                "Legacy data navigation opens merged settings and selects its sidebar entry");
            Check(nav.Items.OfType<ListBoxItem>().All(i=>i.Tag as string!="Data"), "Sidebar exposes a single settings destination for settings and data");
            window.Navigate(SettingsPage.General);
            Check(ReferenceEquals(host.Content,page) && window.GoBack() && window.CurrentPage==SettingsPage.Appearance,
                "Canonical navigation after the legacy route reuses one page and adds no duplicate history entry");
            window.Navigate(SettingsPage.General);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(ReferenceEquals(host.Content,page), "Merged settings page remains cached across navigation");

            var card=(Border)page.FindName("GeneralCard");
            var scroll=(ScrollViewer)page.FindName("GeneralScroll");
            var content=(FrameworkElement)page.FindName("GeneralContent");
            async Task CheckActionClipping(string context)
            {
                var count=(TextBlock)page.FindName("ManualCountText");
                var countRight=count.TranslatePoint(new Point(count.ActualWidth,0),page).X;
                var offsetCount=(TextBlock)page.FindName("OffsetCountText");
                Check(Math.Abs(offsetCount.TranslatePoint(new Point(offsetCount.ActualWidth,0),page).X-countRight)<.1,
                    "Settings read-only statistics share one visual right edge: "+context);
                foreach(var action in Descendants(content).OfType<Button>().Where(b=>b.IsVisible))
                {
                    action.BringIntoView();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    var text=(ContentPresenter)action.Template.FindName("ContentHost",action);
                    Check(Math.Abs(text.TranslatePoint(new Point(text.ActualWidth,0),page).X-countRight)<.1,
                        $"Settings action text aligns with read-only statistics: {context} {action.Content}");
                    var root=(FrameworkElement)action.Template.FindName("Root",action);
                    for(DependencyObject? ancestor=VisualTreeHelper.GetParent(root); ancestor is Visual visual; ancestor=VisualTreeHelper.GetParent(ancestor))
                    {
                        if(VisualTreeHelper.GetClip(visual) is Geometry clip)
                        {
                            var bounds=root.TransformToAncestor(visual).TransformBounds(new Rect(root.RenderSize));
                            bounds.Inflate(-.1,-.1);
                            Check(clip.Bounds.Contains(bounds),
                                $"Settings action and hover surface survive every ancestor clip: {context} {action.Content} ancestor={ancestor.GetType().Name} bounds={bounds} clip={clip.Bounds}");
                        }
                        if(ReferenceEquals(ancestor,scroll)) break;
                    }
                }
            }
            void ShowActionHover(bool visible)
            {
                foreach(var action in Descendants(content).OfType<Button>().Where(b=>b.IsVisible))
                    if(action.Template.FindName("HoverLayer",action) is UIElement hover)
                        hover.SetCurrentValue(UIElement.OpacityProperty,visible?1d:0d);
            }
            var cardOrigin=card.TranslatePoint(new Point(),page);
            Check(Math.Abs(cardOrigin.X-appearanceOrigin.X)<.1 && Math.Abs(cardOrigin.Y-appearanceOrigin.Y)<.1
                && Math.Abs(card.ActualWidth-appearanceWidth)<.1 && card.CornerRadius==appearanceCard.CornerRadius,
                "Merged settings follows the shared appearance card position, width and radius");
            Check(window.ViewModel.CacheCount=="128" && ((TextBlock)page.FindName("CacheSummary")).Text.Contains("128")
                && ((TextBlock)page.FindName("ManualCountText")).Text.Contains("6")
                && ((TextBlock)page.FindName("OffsetCountText")).Text.Contains("12"),
                "Opening merged settings automatically loads and displays isolated cache statistics");
            var refresh=(Button)page.FindName("RefreshStatisticsButton");
            Check(refresh.Command?.CanExecute(refresh.CommandParameter)==true, "Cache statistics retain an available refresh action");
            refresh.Command!.Execute(refresh.CommandParameter);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(window.ViewModel.CacheCount=="128", "Refreshing statistics uses the isolated backend");

            foreach(var (name,value) in new[]{("ThemeLight","Light"),("ThemeDark","Dark"),("ThemeSystem","FollowSystem")})
            {
                ((RadioButton)page.FindName(name)).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.ViewModel.Theme==value && config.Current.General.Theme==value,
                    "Theme selection updates the real temporary configuration binding: "+value);
                Check(new[]{"ThemeLight","ThemeDark","ThemeSystem"}.Count(n=>((RadioButton)page.FindName(n)).IsChecked==true)==1,
                    "Theme selector retains one selected option");
            }
            var startup=(CheckBox)page.FindName("StartupToggle");
            startup.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,!previousBackendStartup);
            Check(backend.StartupEnabled==!previousBackendStartup && config.Current.General.StartWithWindows==!previousBackendStartup,
                "Startup toggle updates only the fake registration backend and temporary configuration");
            startup.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,previousBackendStartup);
            var animations=(CheckBox)page.FindName("AnimationsToggle");
            animations.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            Check(window.ViewModel.AnimationsEnabled && !config.Current.General.ReduceMotion, "Animation switch uses direct enabled semantics");
            animations.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
            Check(!window.ViewModel.AnimationsEnabled && config.Current.General.ReduceMotion, "Disabling animations restores reduced-motion layout checks");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();

            maintenance=(Expander)page.FindName("MaintenanceSettings");
            maintenance.IsExpanded=true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var currentManualReset=(Button)page.FindName("CurrentManualResetButton");
            backend.State=previousState with { SessionConnected=false,Track=null,CacheKey=null };
            window.ViewModel.Refresh();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!currentManualReset.IsEnabled, "Current-song manual reset is unavailable without a song");
            backend.State=previousState; window.ViewModel.Refresh();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(currentManualReset.IsEnabled && backend.Operations==previousOperations,
                "Restoring the song restores its maintenance action without executing cleanup");

            maintenance.IsExpanded=false;
            window.Height=1100;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var width=content.ActualWidth;
            var refreshX=refresh.TranslatePoint(new Point(),page).X;
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed, "Tall merged settings has no unnecessary scrollbar");
            maintenance.IsExpanded=true;
            window.Height=window.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Visible
                && Math.Abs(content.ActualWidth-width)<.1 && Math.Abs(refresh.TranslatePoint(new Point(),page).X-refreshX)<.1,
                "Expanded settings gains scrolling without shifting content or aligned actions");
            maintenance.IsExpanded=false;
            window.Height=1100;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed && Math.Abs(content.ActualWidth-width)<.1,
                "Collapsed settings restores height while preserving reserved scrollbar space");

            foreach(var theme in new[]{"Light","Dark"})
            {
                ThemeManager.Apply(theme,false);
                window.Width=previousWidth; window.Height=previousHeight;
                maintenance.IsExpanded=false;
                scroll.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"General-Merged-{theme}.png"),1.5);
                foreach(var disclosure in new[]{maintenance})
                {
                    var headerText=Descendants(disclosure).OfType<TextBlock>().First(t=>t.Text==disclosure.Header as string);
                    Check(((SolidColorBrush)headerText.Foreground).Color==((SolidColorBrush)page.FindResource("TextPrimaryBrush")).Color,
                        "Merged settings disclosure titles follow the active theme: "+theme);
                }
                maintenance.IsExpanded=true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"General-Expanded-{theme}.png"),1.5);
                await CheckActionClipping(theme+" normal");
                scroll.ScrollToBottom();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"General-Maintenance-{theme}.png"),1.5);
                ShowActionHover(true);
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"General-Actions-Hover-{theme}.png"),1.5);
                ShowActionHover(false);
                var normalCardWidth=card.ActualWidth;
                window.Width=1600; window.Height=1000;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Check(Math.Abs(card.ActualWidth-normalCardWidth-(window.Width-previousWidth))<1,
                    "Settings card continues stretching with wide windows without a width cap");
                await CheckActionClipping(theme+" wide");
                scroll.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var guidedRow=Descendants(maintenance).OfType<SettingRow>().First(r=>ReferenceEquals(r.Content,currentManualReset));
                var rowGuide=(Border)guidedRow.Template.FindName("RowGuide",guidedRow);
                rowGuide.SetCurrentValue(UIElement.OpacityProperty,1d);
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"General-Wide-Row-Guide-{theme}.png"),1.5);
                rowGuide.SetCurrentValue(UIElement.OpacityProperty,0d);
                window.Width=window.MinWidth; window.Height=window.MinHeight;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                await CheckActionClipping(theme+" narrow");
                scroll.ScrollToBottom();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                ShowActionHover(true);
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"General-Actions-Narrow-Hover-{theme}.png"),1.5);
                ShowActionHover(false);
                var viewport=Descendants(scroll).OfType<ScrollContentPresenter>().First();
                foreach(var sectionName in new[]{"GeneralContent","DataSettings","MaintenanceSettings"})
                {
                    var section=(FrameworkElement)page.FindName(sectionName);
                    var bounds=section.TransformToAncestor(viewport).TransformBounds(new Rect(section.RenderSize));
                    Check(bounds.Left>=-.1 && bounds.Right<=viewport.ActualWidth+.1,
                        "Merged settings section fits the narrow viewport: "+theme+" "+sectionName);
                }
                foreach(var actionName in new[]{"RefreshStatisticsButton","CacheFolderButton","CurrentManualResetButton"})
                {
                    var action=(Button)page.FindName(actionName);
                    Check(action.IsVisible && action.ActualHeight>0 && action.ActualWidth>0 && action.Command is not null
                        && action.BorderThickness==new Thickness(0), "Merged settings keeps borderless actions available: "+actionName);
                    action.BringIntoView();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    var bounds=action.TransformToAncestor(viewport).TransformBounds(new Rect(action.RenderSize));
                    Check(bounds.Left>=-.1 && bounds.Right<=viewport.ActualWidth+.1 && bounds.Top>=-.1 && bounds.Bottom<=viewport.ActualHeight+.1,
                        "Merged settings action can be fully revealed in a narrow window: "+theme+" "+actionName);
                }
                scroll.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"General-Narrow-{theme}.png"),1.5);
            }
        }
        finally
        {
            if(maintenance is not null) maintenance.IsExpanded=false;
            backend.State=previousState; backend.SetStartup(previousBackendStartup);
            config.Update(c=>{ c.General.Theme=previousTheme; c.General.ReduceMotion=previousReduceMotion; c.General.StartWithWindows=previousStartup; },scheduleSave:false);
            window.ViewModel.Refresh();
            window.Width=previousWidth; window.Height=previousHeight;
            ThemeManager.Apply(previousThemePreference,previousHighContrast,systemLightOverride:!previousDark);
            window.Navigate(previousPage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        }
    }

    private static async Task TestCalibrationScroll(SettingsWindow window, string? outDir, FakeSettings backend)
    {
        var previousPage=window.CurrentPage;
        var previousTheme=ThemeManager.CurrentPreference;
        var previousHighContrast=ThemeManager.IsHighContrast;
        var previousDark=ThemeManager.IsDark;
        var previousStep=window.ViewModel.TimingStep;
        var windowWidth=window.Width;
        var height=window.Height;
        var originalState=backend.State;
        var originalCapabilities=backend.PlaybackCapabilities;
        ComboBox? stepChoice=null;
        try
        {
            window.Navigate(SettingsPage.Appearance);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var appearance=(UserControl)((ContentControl)window.FindName("PageHost")).Content;
            var appearanceCard=(Border)appearance.FindName("AppearanceCard");
            var appearanceOrigin=appearanceCard.TranslatePoint(new Point(),appearance);
            var appearanceWidth=appearanceCard.ActualWidth;
            var appearanceRadius=appearanceCard.CornerRadius;
            var appearanceBorder=appearanceCard.BorderThickness;

            window.Navigate(SettingsPage.Synchronization);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var page=(UserControl)((ContentControl)window.FindName("PageHost")).Content;
            var card=(Border)page.FindName("CalibrationCard");
            var audition=(FrameworkElement)page.FindName("AuditionArea");
            var workflow=(FrameworkElement)page.FindName("WorkflowContent");
            var trackAdjustment=(FrameworkElement)page.FindName("TrackAdjustmentGroup");
            var trackRow=(FrameworkElement)page.FindName("TrackAdjustmentRow");
            var globalRow=(FrameworkElement)page.FindName("GlobalAdjustmentRow");
            var globalAdjustment=(FrameworkElement)page.FindName("GlobalAdjustmentGroup");
            var trackReset=(Button)page.FindName("TrackResetButton");
            var globalReset=(Button)page.FindName("GlobalResetButton");
            var editor=(NumberEditor)page.FindName("TrackOffsetEditor");
            var globalEditor=(NumberEditor)page.FindName("GlobalOffsetEditor");
            var scroll=(ScrollViewer)page.FindName("CalibrationScroll");
            stepChoice=(ComboBox)page.FindName("TimingStepChoice");
            var global=(FrameworkElement)page.FindName("GlobalCalibration");

            bool UsesSingleCard(FrameworkElement section)
            {
                var framedAncestors=new List<Border>();
                for(var ancestor=VisualTreeHelper.GetParent(section); ancestor is not null && !ReferenceEquals(ancestor,page); ancestor=VisualTreeHelper.GetParent(ancestor))
                    if(ancestor is Border border && border.BorderThickness.Left>0 && border.BorderThickness.Top>0
                        && border.BorderThickness.Right>0 && border.BorderThickness.Bottom>0) framedAncestors.Add(border);
                return framedAncestors.Count==1 && ReferenceEquals(framedAncestors[0],card);
            }

            void CheckSharedGeometry()
            {
                var cardOrigin=card.TranslatePoint(new Point(),page);
                Check(Math.Abs(cardOrigin.X-appearanceOrigin.X)<.1 && Math.Abs(cardOrigin.Y-appearanceOrigin.Y)<.1
                    && Math.Abs(card.ActualWidth-appearanceWidth)<.1,
                    "Calibration and appearance use the same main card position and width");
                Check(card.CornerRadius==appearanceRadius && card.BorderThickness==appearanceBorder,
                    "Calibration and appearance use the same main card outline");
                Check(UsesSingleCard(audition) && UsesSingleCard(trackAdjustment) && UsesSingleCard(global),
                    "Audition, track adjustment and global calibration share one framed surface");
                var auditionLeft=audition.TranslatePoint(new Point(),page).X;
                var workflowLeft=workflow.TranslatePoint(new Point(),page).X;
                Check(Math.Abs(auditionLeft-workflowLeft)<.1 && Math.Abs(auditionLeft+audition.ActualWidth-workflowLeft-workflow.ActualWidth)<.1,
                    "Audition and calibration settings share both horizontal content edges");
            }

            void CheckAdjustmentGeometry()
            {
                var editorOrigin=editor.TranslatePoint(new Point(),page);
                var stepOrigin=stepChoice.TranslatePoint(new Point(),page);
                Check(Math.Abs(editorOrigin.Y+editor.ActualHeight/2-stepOrigin.Y-stepChoice.ActualHeight/2)<.1,
                    "Adjustment step selector shares the current-song input row");
                Check(stepOrigin.X>=trackAdjustment.TranslatePoint(new Point(),page).X+trackAdjustment.ActualWidth,
                    "Adjustment step selector stays beside the compact current-song controls without overlap");
                Check(Math.Abs(editorOrigin.X-globalEditor.TranslatePoint(new Point(),page).X)<.1,
                    "Current-song and global offset inputs share their horizontal position");
                var trackResetOrigin=trackReset.TranslatePoint(new Point(),page);
                var globalResetOrigin=globalReset.TranslatePoint(new Point(),page);
                Check(Math.Abs(trackResetOrigin.X+trackReset.ActualWidth-globalResetOrigin.X-globalReset.ActualWidth)<.1,
                    "Current-song and global reset actions share the right content edge");
                foreach(var reset in new[]{trackReset,globalReset})
                {
                    var header=(Grid)VisualTreeHelper.GetParent(reset);
                    var label=header.Children.OfType<TextBlock>().Single();
                    var resetOrigin=reset.TranslatePoint(new Point(),page);
                    var labelOrigin=label.TranslatePoint(new Point(),page);
                    Check(Math.Abs(resetOrigin.Y+reset.ActualHeight/2-labelOrigin.Y-label.ActualHeight/2)<.1,
                        "Reset action is vertically centered in its section title row");
                }
            }

            async Task ShowAdjustment(FrameworkElement row)
            {
                var viewport=Descendants(scroll).OfType<ScrollContentPresenter>().First();
                var bounds=row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset+bounds.Top-16);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                bounds=row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
                Check(bounds.Top>=-.1 && bounds.Bottom<=viewport.ActualHeight+.1,
                    "Complete timing adjustment row can be scrolled into view at minimum window height");
            }

            CheckSharedGeometry();
            CheckAdjustmentGeometry();
            stepChoice.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,50d);
            Check(window.ViewModel.TimingStep==50,"Calibration step selector updates numeric step binding");
            var calibrationPreview=(LyricsPreview)page.FindName("CalibrationPreview");
            Check(calibrationPreview.CurrentSongOnly && ((ComboBox)calibrationPreview.FindName("SourceChoice")).SelectedIndex==1
                && ((Grid)calibrationPreview.FindName("PreviewToolbar")).Visibility==Visibility.Collapsed,
                "Calibration preview always follows the real current song without demo controls");
            window.Height=1000;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var content=(FrameworkElement)scroll.Content;
            var width=content.ActualWidth;
            Check(editor.Compact && editor.OffsetMode && editor.Step==50,"Compact timing editor preserves integer offset validation and chosen step");
            var x=editor.TranslatePoint(new Point(),page).X;
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed,"Tall calibration page needs no scrollbar");
            Check(global.IsVisible && globalAdjustment.IsVisible && globalEditor.Value==window.ViewModel.GlobalOffset,
                "Global calibration exposes its saved offset without a disclosure interaction");
            Check(trackAdjustment.ActualWidth<400,"Primary adjustment buttons and input form a compact group");
            Check(((Button)page.FindName("AuditionPlayButton")).IsEnabled && page.FindName("ReplayButton") is null,"Calibration offers playback without the removed replay action");
            var progress=(PlaybackSeekSlider)page.FindName("AuditionProgress");
            var adjustmentY=trackAdjustment.TranslatePoint(new Point(),page).Y;
            var progressOrigin=progress.TranslatePoint(new Point(),page);
            backend.State=originalState with { CacheKey=null, LyricsLoading=true };
            window.ViewModel.Refresh();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(!trackAdjustment.IsEnabled && Math.Abs(trackAdjustment.TranslatePoint(new Point(),page).Y-adjustmentY)<.1,
                "Pending lyrics disable song calibration without moving its controls");
            backend.State=originalState with { CacheKey=null, LyricsLoading=false, NeedsRematch=true };
            window.ViewModel.Refresh();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(!trackAdjustment.IsEnabled
                && Math.Abs(trackAdjustment.TranslatePoint(new Point(),page).Y-adjustmentY)<.1,
                "Unmatched songs keep calibration disabled without moving its controls");
            backend.State=originalState;
            backend.PlaybackCapabilities=new(true,true,false);
            window.ViewModel.Refresh();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(trackAdjustment.IsEnabled && !progress.IsEnabled,
                "Restored lyric binding enables calibration independently of unsupported seeking");
            var unavailableProgressOrigin=progress.TranslatePoint(new Point(),page);
            Check(Math.Abs(unavailableProgressOrigin.X-progressOrigin.X)<.1 && Math.Abs(unavailableProgressOrigin.Y-progressOrigin.Y)<.1
                && Math.Abs(trackAdjustment.TranslatePoint(new Point(),page).Y-adjustmentY)<.1,
                "Unavailable seeking preserves progress and calibration control positions");
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Calibration-Seek-Unavailable.png"),1.5);
            backend.PlaybackCapabilities=originalCapabilities;
            window.ViewModel.Refresh();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(progress.IsEnabled && trackAdjustment.IsEnabled
                && Math.Abs(trackAdjustment.TranslatePoint(new Point(),page).Y-adjustmentY)<.1,
                "Recovered seeking re-enables transport without moving calibration controls");
            foreach(var offsetEditor in Descendants(page).OfType<NumberEditor>())
            {
                var offsetInput=(TextBox)offsetEditor.FindName("Input");
                var help=System.Windows.Automation.AutomationProperties.GetHelpText(offsetInput);
                Check(help.Contains("正数提前，负数延后") && help.Contains("5000")
                    && System.Windows.Automation.AutomationProperties.GetName(offsetInput)==offsetEditor.AccessibleName,
                    "Focused offset inputs expose scope, sign and valid range to accessibility tools");
            }
            Check(Math.Abs(content.ActualWidth-width)<.1 && Math.Abs(editor.TranslatePoint(new Point(),page).X-x)<.1,
                "Calibration actions preserve row width and editor position");
            window.Height=window.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Visible,"Short calibration page scrolls");
            Check(Math.Abs(content.ActualWidth-width)<.1 && Math.Abs(editor.TranslatePoint(new Point(),page).X-x)<.1,
                "Calibration scrollbar appearance does not shift content or offset editor");
            CheckSharedGeometry();
            CheckAdjustmentGeometry();
            var previousScrollOffset=scroll.VerticalOffset;
            await ShowAdjustment(trackRow);
            await ShowAdjustment(globalRow);
            scroll.ScrollToVerticalOffset(previousScrollOffset);
            window.Height=1000;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed && Math.Abs(content.ActualWidth-width)<.1,
                "Restored calibration page retains its width when scrollbar disappears");
            window.Height=height;
            window.ViewModel.TimingStep=100;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Calibration-Audition.png"),1.5);
            stepChoice.IsDropDownOpen=true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            if(outDir is not null) CaptureElement((FrameworkElement)((System.Windows.Controls.Primitives.Popup)stepChoice.Template.FindName("PART_Popup",stepChoice)).Child,Path.Combine(outDir,"Calibration-Step-Menu.png"),1.5);
            stepChoice.IsDropDownOpen=false;

            foreach(var theme in new[]{"Light","Dark"})
            {
                ThemeManager.Apply(theme,false);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                CheckSharedGeometry();
                CheckAdjustmentGeometry();
                Check(((SolidColorBrush)card.Background).Color==((SolidColorBrush)page.FindResource("SurfaceBrush")).Color
                    && ((SolidColorBrush)card.BorderBrush).Color==((SolidColorBrush)page.FindResource("BorderSubtleBrush")).Color,
                    "Calibration main surface follows the shared theme: "+theme);
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"Calibration-Audition-{theme}.png"),1.5);
                stepChoice.IsDropDownOpen=true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                if(outDir is not null) CaptureElement((FrameworkElement)((System.Windows.Controls.Primitives.Popup)stepChoice.Template.FindName("PART_Popup",stepChoice)).Child,Path.Combine(outDir,$"Calibration-Step-Menu-{theme}.png"),1.5);
                stepChoice.IsDropDownOpen=false;

                window.Width=window.MinWidth;
                window.Height=window.MinHeight;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                CheckAdjustmentGeometry();
                previousScrollOffset=scroll.VerticalOffset;
                await ShowAdjustment(trackRow);
                var viewport=Descendants(scroll).OfType<ScrollContentPresenter>().First();
                foreach(var element in new[]{trackRow,globalRow,trackReset,globalReset})
                {
                    var adjustmentBounds=element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
                    Check(adjustmentBounds.Left>=-.1 && adjustmentBounds.Right<=viewport.ActualWidth+.1,
                        "Timing controls and reset actions stay inside the viewport at minimum window width: "+theme);
                }
                if(outDir is not null) Capture(window,Path.Combine(outDir,$"Calibration-MinWidth-{theme}.png"),1.5);
                await ShowAdjustment(globalRow);
                scroll.ScrollToVerticalOffset(previousScrollOffset);
                window.Width=windowWidth;
                window.Height=height;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            }
        }
        finally
        {
            backend.State=originalState;
            backend.PlaybackCapabilities=originalCapabilities;
            window.ViewModel.Refresh();
            if(stepChoice is not null) stepChoice.IsDropDownOpen=false;
            window.ViewModel.TimingStep=previousStep;
            window.Width=windowWidth;
            window.Height=height;
            ThemeManager.Apply(previousTheme,previousHighContrast,systemLightOverride:!previousDark);
            window.Navigate(previousPage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        }
    }

    private static async Task TestAppearanceSections(SettingsWindow window, ConfigService config, string? outDir, FakeSettings backend, SettingsViewModel vm)
    {
        window.Navigate(SettingsPage.Appearance);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var page=(UserControl)((ContentControl)window.FindName("PageHost")).Content;
        var preview=(LyricsPreview)page.FindName("Preview");
        Check(((Border)page.FindName("SectionTabs")).HorizontalAlignment==HorizontalAlignment.Right,"Appearance section tabs align to the right");
        Check(!Descendants(page).OfType<TextBlock>().Any(t=>t.Text=="已保存"),"Appearance does not show a saved footer");
        Check(!Descendants(page).OfType<Button>().Any(b=>b.Content as string=="恢复默认外观"),"Appearance reset is moved out of the main page");
        var primaryFont=(FontPicker)page.FindName("PrimaryFontPicker");
        var fontSlider=(Slider)page.FindName("FontSizeSlider");
        Check(Math.Abs(primaryFont.TranslatePoint(new Point(),page).X-fontSlider.TranslatePoint(new Point(),page).X)<1,"Font and size controls share their left edge");
        var colors=LyricColorScheme.Capture(config.Current);
        var previousTheme=config.Current.General.Theme;
        foreach(var section in new[]{("TextTab","TextSettings"),("ColorsTab","ColorSettings"),("PositionTab","PositionSettings")})
        {
            ((RadioButton)page.FindName(section.Item1)).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(((FrameworkElement)page.FindName(section.Item2)).IsVisible,"Appearance section becomes visible: "+section.Item1);
            var expectedIndex=section.Item1=="TextTab" ? 0 : section.Item1=="ColorsTab" ? 1 : 2;
            Check(Math.Abs(((TranslateTransform)page.FindName("IndicatorTranslate")).X-expectedIndex*76)<.1,
                "Reduced motion section indicator immediately reaches the selected tab");
            Check(((TranslateTransform)page.FindName("ContentTranslate")).X==0,
                "Reduced motion section content remains at its resting position");
            Check(new[]{"TextSettings","ColorSettings","PositionSettings"}.Count(name=>((FrameworkElement)page.FindName(name)).IsVisible)==1,"Only selected appearance section is shown");
            Check(ReferenceEquals(preview,page.FindName("Preview")) && preview.IsVisible,"Appearance sections keep the shared preview");
            if(section.Item1=="PositionTab")
            {
                var positionRows=Descendants((FrameworkElement)page.FindName("PositionSettings")).OfType<SettingRow>().Where(r=>r.Label!="自动适应").ToArray();
                Check(positionRows.Single(r=>r.Label=="最小宽度").Visibility==Visibility.Collapsed,
                    "Automatic layout hides the saved manual minimum");
                vm.AutoFit=false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Check(positionRows.Single(r=>r.Label=="最小宽度").IsVisible,
                    "Manual layout reveals the saved minimum");
                var fallbackRow=positionRows.Single(r=>r.Label=="手动设置宽度");
                Check(fallbackRow.Visibility==Visibility.Collapsed,"Unused fallback row is hidden before measurement");
                backend.Layout=OverlayLayoutSnapshot.Empty with { TaskbarWidthPx=1920 };
                vm.Refresh();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Check(fallbackRow.IsVisible,"Fallback editor appears when measurement fails");
                Func<double>[] values={()=>config.Current.Display.VerticalOffsetPx,()=>config.Current.Overlay.LeftMarginPx,()=>config.Current.Overlay.RightSafetyMarginPx,()=>config.Current.Overlay.MinWidthPx,()=>config.Current.Overlay.MaxWidthPx,()=>config.Current.Overlay.WidthRatio*100};
                for(var i=0;i<positionRows.Length;i++)
                {
                    var slider=Descendants(positionRows[i]).OfType<Slider>().Single();
                    var number=Descendants(positionRows[i]).OfType<NumberEditor>().Single();
                    var before=values[i](); var next=Math.Min(slider.Maximum,before+slider.SmallChange);
                    slider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty,next);
                    Check(Math.Abs(values[i]()-next)<.0001 && Math.Abs(number.Value-next)<.0001,"Position slider, number and configuration stay in sync: "+positionRows[i].Label);
                    slider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty,before);
                }
                var firstNumber=Descendants(positionRows[0]).OfType<NumberEditor>().Single();
                var firstInput=(TextBox)firstNumber.FindName("Input"); var oldValue=firstNumber.Value;
                firstInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,System.Windows.Input.Key.Up){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent});
                Check(firstNumber.Value==oldValue+firstNumber.Step,"Compact position field supports precise keyboard increments");
                firstNumber.SetCurrentValue(NumberEditor.ValueProperty,oldValue);                backend.Layout=backend.Layout with { FreeBandPx=900 };
                vm.Refresh();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Check(fallbackRow.Visibility==Visibility.Collapsed,"Recovered measurement hides fallback editor again");
            }
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Appearance-"+section.Item1+".png"),1.5);
        }
        foreach(var background in new[]{("LightBackgroundButton","#FFF0F0F2"),("DarkBackgroundButton","#FF242426")})
        {
            ((RadioButton)preview.FindName(background.Item1)).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            Check(((SolidColorBrush)((Border)preview.FindName("TaskbarSurface")).Background).Color.ToString()==background.Item2,"Preview background choice is explicit");
        }
        Check(LyricColorScheme.Capture(config.Current)==colors && config.Current.General.Theme==previousTheme,"Browsing appearance sections and preview backgrounds preserves the existing palette");
        ((RadioButton)page.FindName("TextTab")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        foreach(var alignment in new[]{"Left","Right","Center"})
        {
            ((RadioButton)page.FindName("Align"+alignment)).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            Check(config.Current.Display.TextAlignment==alignment,"Alignment icon updates the stored setting: "+alignment);
        }
        var languages=(Expander)page.FindName("LanguageSettings"); languages.IsExpanded=true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(Descendants(languages).OfType<FontPicker>().Count(f=>f.IsVisible)==6,"Language disclosure exposes all existing per-language fonts");
        if(outDir is not null) Capture(window,Path.Combine(outDir,"Appearance-Languages.png"),1.5);
        var languageToggle=Descendants(languages).OfType<System.Windows.Controls.Primitives.ToggleButton>().First();
        var languageRoot=(Border)languageToggle.Template.FindName("Root",languageToggle);
        var primaryRow=Descendants(page).OfType<SettingRow>().First(r=>r.Label=="字体");
        Check(languageRoot.ActualWidth>=primaryRow.ActualWidth+21,"Language disclosure hit area extends through the safe padding on both sides");
        var scrollViewport=Descendants((ScrollViewer)page.FindName("SettingsScroll")).OfType<ScrollContentPresenter>().First();
        var hoverBounds=languageRoot.TransformToAncestor(scrollViewport).TransformBounds(new Rect(0,0,languageRoot.ActualWidth,languageRoot.ActualHeight));
        Check(hoverBounds.Left>=-1 && hoverBounds.Right<=scrollViewport.ActualWidth+1,"Language disclosure hit area remains within the unclipped scroll viewport");
        Check(languageRoot.CornerRadius==new CornerRadius(8),"Language disclosure keeps the common control radius");
        if(outDir is not null)
        {
            Capture(window,Path.Combine(outDir,"Appearance-LanguageDisclosure.png"),1.5);
        }
        languages.IsExpanded=false;
        ((ScrollViewer)page.FindName("SettingsScroll")).ScrollToTop();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var settingsScroll=(ScrollViewer)page.FindName("SettingsScroll");
        window.UpdateLayout();
        Check(settingsScroll.ExtentHeight<=settingsScroll.ViewportHeight+1,"Default text section includes language settings without scrolling at the standard window size");
        var oldFontSize=config.Current.Display.FontSize;
        ((Slider)page.FindName("FontSizeSlider")).SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty,35d);
        Check(config.Current.Display.FontSize==35 && preview.Config?.Display.FontSize==35,"Font slider updates both settings and shared preview");
        config.SaveNow(); ThemeManager.Apply("Light",false);
        ((RadioButton)preview.FindName("LightBackgroundButton")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        preview.RenderFrame(1800); window.UpdateLayout();
        if(outDir is not null) Capture(window,Path.Combine(outDir,"Reference-Appearance-Light.png"),1.5);
        primaryFont.CatalogProvider=()=>Task.FromResult(new[]{"Microsoft YaHei UI","Resource Han Rounded CN VF — Long Font Name for Overflow Regression"}.Concat(Enumerable.Range(1,40).Select(i=>$"Regression Font {i:00}")).ToArray());
        // Native popups are screen-clamped, so an off-screen fixture cannot validate owner bounds.
        var popupTestLeft=window.Left; var popupTestTop=window.Top; var popupTestWidth=window.Width;
        var screenWork=System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle).WorkingArea;
        var fromDevice=PresentationSource.FromVisual(window)!.CompositionTarget!.TransformFromDevice;
        var workOrigin=fromDevice.Transform(new Point(screenWork.Left,screenWork.Top));
        var workSize=fromDevice.Transform(new Point(screenWork.Width,screenWork.Height));
        window.Width=Math.Min(window.Width,workSize.X-24);
        window.Left=workOrigin.X; window.Top=workOrigin.Y;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(!primaryFont.IsEditable && Descendants(page).OfType<FontPicker>().All(p=>!p.IsEditable),"Main and language-specific font fields are selection-only");
        var fontDisplay=(TextBlock)primaryFont.Template.FindName("FontDisplay",primaryFont);
        Check(fontDisplay.IsVisible && fontDisplay.Text==primaryFont.Text,"Selection-only font field displays the saved font name");
        primaryFont.IsDropDownOpen=true;
        await primaryFont.EnsureCatalogAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var fontPopup=(System.Windows.Controls.Primitives.Popup)primaryFont.Template.FindName("PART_Popup",primaryFont);
        var popupContent=(FrameworkElement)fontPopup.Child;
        var popupRight=popupContent.PointToScreen(new Point(popupContent.ActualWidth,0)).X;
        var windowRight=window.PointToScreen(new Point(window.ActualWidth,0)).X;
        Check(Math.Abs(popupContent.ActualWidth-primaryFont.ActualWidth)<1,"Font popup matches the field width");
        Check(popupContent.PointToScreen(new Point(0,popupContent.ActualHeight)).Y<=window.PointToScreen(new Point(0,window.ActualHeight)).Y+1,"Font menu bottom stays inside its owner window");
        Check(popupRight<=windowRight+1,"Opened font popup stays inside the application right edge");
        var fieldRight=primaryFont.PointToScreen(new Point(primaryFont.ActualWidth,0)).X;
        Check(Math.Abs(popupRight-fieldRight)<=1,"Font popup visible surface aligns exactly with its field right edge");
        var fontEditor=(TextBox)primaryFont.Template.FindName("PART_EditableTextBox",primaryFont);
        Check(fontEditor.Visibility==Visibility.Collapsed && !fontEditor.IsVisible,"Font picker exposes no manual input or text selection rectangle");
        var longItem=(ComboBoxItem)primaryFont.ItemContainerGenerator.ContainerFromIndex(1);
        var longViewport=(FontNameMarquee)longItem.Template.FindName("LabelViewport",longItem);
        Check(longViewport.OverflowWidth>0 && longViewport.ActualWidth<popupContent.ActualWidth,"Long names actually overflow the font popup viewport");
        var row0=(FontPickerItem)primaryFont.ItemContainerGenerator.ContainerFromIndex(0);
        var row1=(FontPickerItem)longItem;
        var valueBeforeHover=primaryFont.Text;
        row0.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseEnterEvent});
        Check(row0.IsActive && ReferenceEquals(primaryFont.ActiveItem,row0),"Font pointer enter highlights immediately without dispatching");
        row1.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseMoveEvent});
        Check(row1.IsActive && !row0.IsActive && longViewport.IsActive,"Font pointer movement immediately transfers the only highlight and scrolling target");
        Check(primaryFont.Text==valueBeforeHover,"Pointer feedback never commits a different font");
        if(outDir is not null) CaptureElement(popupContent,Path.Combine(outDir,"Appearance-FontPopup.png"),1.5);
        primaryFont.IsDropDownOpen=false;
        ((RadioButton)page.FindName("ColorsTab")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var customToggle=(System.Windows.Controls.Primitives.ToggleButton)page.FindName("CustomPaletteToggle");
        var customSettings=(StackPanel)page.FindName("CustomPaletteSettings");
        var separateColors=(System.Windows.Controls.Primitives.ToggleButton)page.FindName("SeparateColors");
        Check(!customSettings.IsVisible && separateColors.IsChecked!=true,"Default palette hides hue and independent color values");
        var paletteButtons=Descendants((FrameworkElement)page.FindName("ColorSettings")).OfType<Button>().Where(b=>b.DataContext is PalettePreset).ToArray();
        Check(paletteButtons.Length==8 && paletteButtons.All(b=>b.Content is null && b.ToolTip is string),"Eight dual-color presets show names only on hover");
        var compactCardHeight=((Border)page.FindName("AppearanceCard")).ActualHeight;
        customToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        separateColors.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(((PaletteHueSlider)page.FindName("PaletteHueControl")).IsVisible,"Custom palette reveals hue control");
        var colorSettings=(FrameworkElement)page.FindName("ColorSettings");
        var colorLeft=colorSettings.TranslatePoint(new Point(),page).X;
        var colorRight=colorSettings.TranslatePoint(new Point(colorSettings.ActualWidth,0),page).X;
        Check(Math.Abs(colorLeft-preview.TranslatePoint(new Point(),page).X)<1 && Math.Abs(colorRight-preview.TranslatePoint(new Point(preview.ActualWidth,0),page).X)<1,"Appearance preview and settings share both visible content edges despite reserved scrollbar");
        var paletteSlider=(PaletteHueSlider)page.FindName("PaletteHueControl");
        var originalHue=paletteSlider.Value;
        var originalScheme=LyricColorScheme.Capture(config.Current);
        foreach(var edgeHue in new[]{0d,360d})
        {
            paletteSlider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty,edgeHue);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var thumb=((System.Windows.Controls.Primitives.Track)paletteSlider.Template.FindName("PART_Track",paletteSlider)).Thumb;
            var thumbLeft=thumb.TranslatePoint(new Point(),page).X;
            var thumbRight=thumb.TranslatePoint(new Point(thumb.ActualWidth,0),page).X;
            Check(thumbLeft>=colorLeft-.1 && thumbRight<=colorRight+.1,"Entire circular hue thumb remains inside content at both endpoints");
            var thumbColor=(System.Windows.Shapes.Ellipse)thumb.Template.FindName("ThumbColor",thumb);
            Check(((SolidColorBrush)thumbColor.Fill).Color.ToString()==vm.NormalColor,"Hue thumb shows the current generated color");
        }
        config.Update(c=>{originalScheme.Apply(c);c.Display.PaletteHue=originalHue;},scheduleSave:false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();

        vm.PresetCommand.Execute(PalettePreset.All[0]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(customToggle.IsChecked==true && separateColors.IsChecked==false,"Selecting a preset keeps the custom hue editor open");
        paletteSlider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty,290d);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(customToggle.IsChecked==true && separateColors.IsChecked==false,"Non-preset hue keeps custom open while generated pair needs no manual editor");
        customToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(customToggle.IsChecked==true && customSettings.IsVisible,"Custom disclosure cannot hide an active non-preset palette");
        vm.NormalColor="#FF123456";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(customToggle.IsChecked==true && separateColors.IsChecked==true,"Independent color opens both disclosures automatically");
        separateColors.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(separateColors.IsChecked==true,"Manual disclosure cannot hide independently edited colors");
        foreach(var disclosure in new[]{customToggle,separateColors})
            Check(((FrameworkElement)disclosure.Template.FindName("DisclosureArrow",disclosure)).RenderTransform is RotateTransform { Angle:90 },"Expanded palette disclosure uses a downward arrow");
        vm.PresetCommand.Execute(PalettePreset.All[0]);
        config.Update(c=>{originalScheme.Apply(c);c.Display.PaletteHue=originalHue;},scheduleSave:false);
        customToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        separateColors.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(((Border)page.FindName("AppearanceCard")).ActualHeight>compactCardHeight,"Palette card grows naturally with expanded settings");
        if(outDir is not null) Capture(window,Path.Combine(outDir,"Appearance-Colors-Custom.png"),1.5);
        var colorEditor=Descendants((FrameworkElement)page.FindName("ColorSettings")).OfType<ColorEditor>().First();
        var shadowToggle=(CheckBox)page.FindName("ShadowToggle");
        var shadowRow=Descendants((FrameworkElement)page.FindName("ColorSettings")).OfType<SettingRow>().Single(r=>r.Label=="阴影颜色");
        var previousShadow=vm.ShadowEnabled;
        var previousShadowColor=vm.ShadowColor;
        foreach(var enabled in new[]{false,true,false})
        {
            shadowToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,enabled);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(shadowRow.Visibility==(enabled ? Visibility.Visible : Visibility.Collapsed),"Shadow color row follows shadow switch without leaving empty space");
            Check(vm.ShadowColor==previousShadowColor,"Hiding shadow color preserves the selected color");
        }
        vm.ShadowEnabled=previousShadow;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var shadowTrack=(Border)shadowToggle.Template.FindName("Track",shadowToggle);
        var manualEditors=Descendants((FrameworkElement)page.FindName("ManualColors")).OfType<ColorEditor>().ToArray();
        Check(manualEditors.Length==2 && Math.Abs(manualEditors[0].TranslatePoint(new Point(),page).Y-manualEditors[1].TranslatePoint(new Point(),page).Y)<1,"Manual lyric colors share one row");
        Check(Math.Abs(manualEditors[1].PointToScreen(new Point(manualEditors[1].ActualWidth,0)).X-shadowTrack.PointToScreen(new Point(shadowTrack.ActualWidth,0)).X)<1,"Visible right manual color and shadow toggle align to the same edge");
        foreach(var editor in manualEditors)
        {
            var swatch=(Border)editor.FindName("Swatch");
            Check(swatch.ActualWidth==swatch.ActualHeight && swatch.CornerRadius.TopLeft==swatch.ActualWidth/2,"Manual color swatch is circular");
        }
        foreach(var editor in Descendants(colorSettings).OfType<ColorEditor>().Where(e=>e.IsVisible))
        {
            var swatch=(FrameworkElement)editor.FindName("Swatch");
            var button=(Button)editor.FindName("SwatchButton");
            var ring=(FrameworkElement)button.Template.FindName("HoverRing",button);
            foreach(var shape in new[]{swatch,ring})
            {
                var bounds=shape.TransformToAncestor(editor).TransformBounds(new Rect(0,0,shape.ActualWidth,shape.ActualHeight));
                Check(bounds.Left>=0 && bounds.Top>=0 && bounds.Right<=editor.ActualWidth && bounds.Bottom<=editor.ActualHeight,$"Circle and hover ring fit wholly within their color editor layout: {editor.AccessibleName} {shape.Name} editor={editor.ActualWidth}x{editor.ActualHeight} bounds={bounds}");
                for(DependencyObject? ancestor=VisualTreeHelper.GetParent(shape); ancestor is Visual visual; ancestor=VisualTreeHelper.GetParent(ancestor))
                {
                    if(VisualTreeHelper.GetClip(visual) is Geometry clip)
                    {
                        var visibleBounds=shape.TransformToAncestor(visual).TransformBounds(new Rect(0,0,shape.ActualWidth,shape.ActualHeight));
                        visibleBounds.Inflate(-.1,-.1);
                        Check(clip.Bounds.Contains(visibleBounds),"Color circle survives every ancestor layout clip");
                    }
                    if(ReferenceEquals(ancestor,editor)) break;
                }
            }
        }
        var editorRings=Descendants(colorSettings).OfType<ColorEditor>().Where(e=>e.IsVisible).Select(e=>
        {
            var swatchButton=(Button)e.FindName("SwatchButton");
            return (UIElement)swatchButton.Template.FindName("HoverRing",swatchButton);
        }).ToArray();
        foreach(var ring in editorRings) ring.SetCurrentValue(UIElement.OpacityProperty,1d);
        if(outDir is not null) Capture(window,Path.Combine(outDir,"Appearance-Colors-HoverRings.png"),1.5);
        foreach(var ring in editorRings) ring.ClearValue(UIElement.OpacityProperty);
        foreach(var sample in new[]{"#FF123456","#FF345678","#FF56789A","#FF789ABC","#FF9ABCDE","#FFABCDEF"}) vm.RememberNormalColorCommand.Execute(sample);
        ((Button)colorEditor.FindName("SwatchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var colorPopup=(System.Windows.Controls.Primitives.Popup)colorEditor.FindName("Picker");
        var colorSurface=(FrameworkElement)colorPopup.Child;
        Check(Math.Abs(colorSurface.ActualWidth-280)<1,"Color popup provides room for the spectrum independently of the compact swatch");
        Check(Math.Abs(colorSurface.PointToScreen(new Point(colorSurface.ActualWidth,0)).X-colorEditor.PointToScreen(new Point(colorEditor.ActualWidth,0)).X)<1,"Color popup right edge aligns with the color field");
        Check(colorSurface.PointToScreen(new Point(0,colorSurface.ActualHeight)).Y<=window.PointToScreen(new Point(0,window.ActualHeight)).Y+1,"Color popup stays inside the bottom of the window");
        var recentItems=(ItemsControl)colorEditor.FindName("RecentItems");
        var recentGrid=Descendants(recentItems).OfType<RecentColorPanel>().Single();
        Check(RecentColorPanel.Capacity==6 && recentItems.Items.Count==6,"Recent colors reserve six evenly spaced positions in one row");
        var recentButtons=Descendants(recentItems).OfType<Button>().ToArray();
        Check(recentButtons.Length==6 && recentButtons.All(b=>b.Template.FindName("RecentSurface",b) is System.Windows.Shapes.Ellipse),"All six recent swatches are circles");
        var firstCircle=(FrameworkElement)recentButtons[0].Template.FindName("RecentSurface",recentButtons[0]);
        var lastCircle=(FrameworkElement)recentButtons[^1].Template.FindName("RecentSurface",recentButtons[^1]);
        Check(Math.Abs(firstCircle.TranslatePoint(new Point(),recentItems).X)<1 && Math.Abs(lastCircle.TranslatePoint(new Point(lastCircle.ActualWidth,0),recentItems).X-recentItems.ActualWidth)<1,"Visible recent circles fill the row exactly from left to right");
        var spectrum=(FrameworkElement)colorEditor.FindName("Spectrum");
        var spectrumLeft=spectrum.TranslatePoint(new Point(),colorSurface).X;
        var spectrumRight=spectrum.TranslatePoint(new Point(spectrum.ActualWidth,0),colorSurface).X;
        Check(Math.Abs(spectrumLeft-(colorSurface.ActualWidth-spectrumRight))<1,"Color popup reserves scrollbar space inside balanced left and right padding");

        var popupViewport=Descendants(colorSurface).OfType<ScrollContentPresenter>().First();
        foreach(var edgeButton in new[]{recentButtons[0],recentButtons[^1]})
        {
            var ring=(System.Windows.Shapes.Ellipse)edgeButton.Template.FindName("RecentRing",edgeButton);
            var ringBounds=ring.TransformToAncestor(popupViewport).TransformBounds(new Rect(0,0,ring.ActualWidth,ring.ActualHeight));
            Check(ringBounds.Left>=0 && ringBounds.Right<=popupViewport.ActualWidth,"First and last recent hover rings remain fully inside the scroll viewport");
            ring.SetCurrentValue(UIElement.OpacityProperty,1d);
        }
        if(outDir is not null) CaptureElement(colorSurface,Path.Combine(outDir,"Appearance-ColorPopup-HoverEdges.png"),1.5);
        foreach(var edgeButton in new[]{recentButtons[0],recentButtons[^1]}) ((UIElement)edgeButton.Template.FindName("RecentRing",edgeButton)).ClearValue(UIElement.OpacityProperty);
        if(outDir is not null) CaptureElement(colorSurface,Path.Combine(outDir,"Appearance-ColorPopup.png"),1.5);
        var colorsBeforeDrag=colorEditor.RecentColors?.ToArray() ?? Array.Empty<string>();
        var originalColor=colorEditor.Hex;
        var colorHue=(Slider)colorEditor.FindName("Hue");
        colorHue.Value=40; colorHue.Value=75;
        Check((colorEditor.RecentColors ?? Array.Empty<string>()).SequenceEqual(colorsBeforeDrag),"Dragging the color picker does not fill history with intermediate colors");
        var finalColor=colorEditor.Hex;
        colorSurface.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,MouseButton.Left)
            { RoutedEvent=System.Windows.Input.Mouse.MouseUpEvent });
        Check(colorPopup.IsOpen && (colorEditor.RecentColors ?? Array.Empty<string>()).SequenceEqual(colorsBeforeDrag),
            "Releasing a color selection does not update history while popup remains open");
        recentButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((colorEditor.RecentColors ?? Array.Empty<string>()).SequenceEqual(colorsBeforeDrag),"Selecting history leaves recent order unchanged until close");
        var hexInput=(TextBox)colorEditor.FindName("HexInput");
        hexInput.Text="#FF2468AC";
        hexInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
        finalColor=colorEditor.Hex;
        Check(finalColor=="#FF2468AC" && (colorEditor.RecentColors ?? Array.Empty<string>()).SequenceEqual(colorsBeforeDrag),"Manual hex applies live without saving recent colors early");
        colorPopup.IsOpen=false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(colorEditor.RecentColors?.FirstOrDefault()==finalColor,"Closing the picker remembers the final color");
        colorEditor.SetCurrentValue(ColorEditor.HexProperty,originalColor);
        var expandedWidth=window.Width; var expandedHeight=window.Height;
        window.Width=760; window.Height=480;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(settingsScroll.ExtentWidth<=settingsScroll.ViewportWidth+1,"Expanded custom palette fits a narrow window without horizontal overflow");
        if(outDir is not null) Capture(window,Path.Combine(outDir,"Appearance-Colors-Custom-Narrow.png"),1);
        window.Width=expandedWidth; window.Height=expandedHeight;
        customToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false); separateColors.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
        window.Left=popupTestLeft; window.Top=popupTestTop; window.Width=popupTestWidth;        foreach(var tab in new[]{"ColorsTab","PositionTab"})
        {
            ((RadioButton)page.FindName(tab)).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Reference-"+tab+"-Light.png"),1.5);
        }
        ((RadioButton)page.FindName("TextTab")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var sourceChoice=(ComboBox)preview.FindName("SourceChoice"); sourceChoice.IsDropDownOpen=true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var sourceSurface=(Border)sourceChoice.Template.FindName("PopupSurface",sourceChoice);
        Check(Math.Abs(sourceSurface.ActualWidth-68)<1 && sourceSurface.ActualWidth<sourceChoice.ActualWidth,"Preview source popup removes the unused arrow column from its width");
        if(outDir is not null) CaptureElement(sourceSurface,Path.Combine(outDir,"Preview-Source-Compact.png"),2);
        var sourceRoot=(Border)sourceChoice.Template.FindName("Root",sourceChoice);
        Check(sourceRoot.CornerRadius==sourceSurface.CornerRadius,"Preview trigger and menu share the same corner radius");
        Check(((SolidColorBrush)sourceRoot.Background).Color==((SolidColorBrush)sourceChoice.FindResource("SurfaceBrush")).Color,"Open source menu does not retain a blue fill without pointer hover");
        var backgroundSegments=(Border)preview.FindName("BackgroundSegmentsContainer");
        Check(backgroundSegments.Padding==new Thickness(0) && backgroundSegments.ActualHeight==((RadioButton)preview.FindName("LightBackgroundButton")).ActualHeight,"Preview selected background fills the entire segment height without an inset");
        Check(sourceChoice.Template.FindName("KeyboardOutline",sourceChoice) is null,"Source choice has no persistent blue focus frame");
        var selectedPreview=(RadioButton)preview.FindName("LightBackgroundButton");
        var selectedFill=(Border)preview.FindName("BackgroundIndicator");
        var selectedSection=(RadioButton)page.FindName("TextTab");
        var selectedSectionFill=(Border)page.FindName("SectionIndicator");
        Check(selectedFill.BorderThickness==new Thickness(0) && selectedSectionFill.BorderThickness==new Thickness(0),"Segment fills have no transparent border seams");
        Check(((SolidColorBrush)selectedFill.Background).Color==((SolidColorBrush)page.FindResource("PlaybackPressedBrush")).Color && ((SolidColorBrush)selectedSectionFill.Background).Color==((SolidColorBrush)selectedFill.Background).Color,"Preview and section selected states share the stronger blue");
        Check(((SolidColorBrush)page.FindResource("PlaybackHoverBrush")).Color!=((SolidColorBrush)selectedFill.Background).Color,"Hovered and selected states are distinguishable");
        sourceChoice.IsDropDownOpen=false;
        config.Update(c=>c.Display.FontSize=oldFontSize,scheduleSave:false); config.SaveNow();
        ((RadioButton)preview.FindName("DarkBackgroundButton")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        var oldWidth=window.Width; var oldHeight=window.Height;
        window.Width=760; window.Height=480;
        foreach(var sectionName in new[]{"TextTab","ColorsTab","PositionTab"})
        {
            ((RadioButton)page.FindName(sectionName)).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(settingsScroll.ExtentWidth<=settingsScroll.ViewportWidth+1,"Narrow appearance section has no horizontal overflow: "+sectionName);
            Check(settingsScroll.ViewportHeight>40 && preview.IsVisible,"Narrow appearance keeps preview and scrollable settings available");
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Appearance-Narrow-"+sectionName+".png"),1);
        }
        window.Width=oldWidth; window.Height=oldHeight;
        ((RadioButton)page.FindName("TextTab")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var oldOffset=config.Current.Display.VerticalOffsetPx;
        foreach(var offset in new[]{-10d,10d})
        {
            window.Height=480;
            config.Update(c=>{c.Display.FontSize=72;c.Display.VerticalOffsetPx=offset;},scheduleSave:false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var renderer=(FrameworkElement)preview.FindName("Karaoke");
            var viewport=(FrameworkElement)preview.FindName("PreviewViewport");
            var bounds=renderer.TransformToAncestor(viewport).TransformBounds(new Rect(0,0,renderer.ActualWidth,renderer.ActualHeight));
            Check(bounds.Top>=-1 && bounds.Bottom<=viewport.ActualHeight+1,"Largest preview text and vertical offset fit inside a short window");
            if(outDir is not null) Capture(window,Path.Combine(outDir,$"Appearance-MaxFont-Offset{offset:0}.png"),1);
        }
        config.Update(c=>{c.Display.FontSize=oldFontSize;c.Display.VerticalOffsetPx=oldOffset;},scheduleSave:false); config.SaveNow();
        window.Height=oldHeight;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task TestScrollingReader(SettingsWindow window, SettingsViewModel vm, FakeSettings backend, string? outDir)
    {
        var previous = backend.State;
        var page = (TaskbarLyrics.App.Pages.OverviewPage)((ContentControl)window.FindName("PageHost")).Content;
        var reader = (ScrollingLyrics)page.FindName("CurrentLyric");
        Motion.SetReduce(page, true);
        var phrases = new[] { "清晨的风穿过树梢", "我们沿着小路出发", "把昨日留在身后", "雨停以后，风还在吹", "路边的花慢慢盛开", "抬头看见辽阔的天空", "远处的灯照亮归途", "今天也有新的故事", "让脚步随着音乐", "一起走到下一站" };
        var translations = new[] { "Morning wind through the trees", "We follow the quiet path", "Leave yesterday behind", "After the rain, the wind still blows", "Flowers open by the road", "Look up at the open sky", "Distant lights guide us home", "A new story starts today", "Let our steps follow the music", "Together to the next stop" };
        var lines = phrases.Select((text, i) => new QrcLine { Text = text, StartMs = 69000 + i * 4000, DurationMs = 4000,
            Words = text.Select((c, j) => new QrcWord { Text = c.ToString(), StartMs = 69000 + i * 4000 + j * (4000 / text.Length), DurationMs = 4000 / text.Length }).ToList() }).ToArray();
        var document = new LyricsDocument { Mode = LyricsMode.Qrc, Lines = lines,
            Translation = string.Join("\n", lines.Select((l, i) => $"[{l.StartMs / 60000:00}:{l.StartMs / 1000 % 60:00}.00]{translations[i]}")) };
        try
        {
            var translationFile = Path.Combine(Path.GetTempPath(), "TaskbarLyrics-translation-" + Guid.NewGuid() + ".lrc");
            try
            {
                await File.WriteAllTextAsync(translationFile, document.Translation!);
                Check(await LyricsCache.ReadOptionalTranslationAsync(translationFile) == document.Translation, "Cached translation is read without touching production cache");
            }
            finally { File.Delete(translationFile); }
            Check(await LyricsCache.ReadOptionalTranslationAsync(translationFile) is null, "Missing optional translation preserves original lyrics");
            var rows = LyricReaderRows.Build(document);
            Check(rows.Count == 10 && rows[3].Translation == translations[3], "Timed translations align to original lyric rows");
            Check(LyricReaderRows.Build(new LyricsDocument { Lines = lines }).All(r => r.Translation is null), "Original-only lyrics add no empty translation rows");
            var resolved = new TaskbarLyrics.Core.Lyrics.LyricsSynchronizer().Resolve(lines, TimeSpan.FromMilliseconds(83000));
            backend.State = previous with { Status = PlaybackStatus.Paused, HasLyrics = true, Document = document, Lyric = resolved,
                DisplayText = resolved.LineText!, CurrentLineWords = lines[3].Words, WordIndex = resolved.WordIndex, WordProgress = resolved.WordProgress };
            vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(reader.VisibleLineCount == 10 && reader.ActiveLineIndex == 3, "Adaptive reader follows the current lyric in the complete document");
            Check(PlaybackTaskbarPreview.CurrentTranslation(vm.State)==translations[3], "Taskbar card translation follows the active lyric timestamp");
            Check(PlaybackTaskbarPreview.CurrentTranslation(vm.State with {Raw=vm.State.Raw with {Document=null}}) is null,
                "Taskbar card cannot reuse translations after the document is cleared");
            if(outDir is not null)
            {
                var translatedCard=PlaybackTaskbarPreview.Render(vm.State,vm.PlaybackButtonLabel,420,300,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(translatedCard));
                using var output=File.Create(Path.Combine(outDir,"Taskbar-Translated-Card.png")); encoder.Save(output);
            }
            var normalBrush = (SolidColorBrush)typeof(ScrollingLyrics).GetField("_normal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
            Check(normalBrush.Color.A > 0, "Initial matching default palette renders visible current lyrics regardless of binding order");
            var originalHeight = window.Height;
            var compactHeight = reader.ActualHeight;
            Check(page.FindName("ExpandLyricsButton") is null, "Lyric reader has no expand/collapse action");
            if (outDir is not null) Capture(window, Path.Combine(outDir, "Overview-Lyrics-Adaptive.png"), 1);
            window.Height = originalHeight + 220; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(reader.ActualHeight > compactHeight + 200, "Reader grows with available page height without a click");
            if (outDir is not null) Capture(window, Path.Combine(outDir, "Overview-Lyrics-Tall.png"), 1);
            var followedOffset = reader.ScrollOffset;
            reader.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(Math.Abs(reader.ScrollOffset - followedOffset) < .1, "Mouse wheel cannot switch lyrics into manual browsing");
            Check(!Descendants(page).OfType<Button>().Any(b => Equals(b.Content, "回到当前")), "Playback page no longer contains return-to-current");
            var later = new TaskbarLyrics.Core.Lyrics.LyricsSynchronizer().Resolve(lines, TimeSpan.FromMilliseconds(90000));
            backend.State = backend.State with { Lyric = later };
            vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(reader.ActiveLineIndex == later.LineIndex && Math.Abs(reader.ScrollOffset - followedOffset) > 20,
                "Progress changes always move the lyric reader to the current line");
            window.Height = originalHeight; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(Math.Abs(reader.ActualHeight - compactHeight) < 1, "Shrinking the window restores the lyric viewport geometry");
            backend.State = previous with { Generation = previous.Generation + 1, HasLyrics = false, LyricsLoading = true, Document = null };
            vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(reader.VisibleLineCount == 0, "Track change clears old lyrics immediately");
            backend.State = previous with { Generation = previous.Generation + 2, HasLyrics = true,
                Document = new LyricsDocument { Lines = new[] { lines[0] } },
                Lyric = new TaskbarLyrics.Core.Lyrics.LyricsSynchronizer().Resolve(new[] { lines[0] }, TimeSpan.Zero) };
            vm.Refresh(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Check(reader.VisibleLineCount == 1 && reader.ActiveLineIndex == -1, "A shorter replacement document with an intro cannot reuse old row indexes");
        }
        finally { backend.State = previous; vm.Refresh(); Motion.SetReduce(page, false); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    }

    private static async Task TestWindowNavigation(SettingsWindow window)
    {
        Check(PlaybackTaskbarPreview.WindowsModeIsLight(1) && !PlaybackTaskbarPreview.WindowsModeIsLight(0)
            && !PlaybackTaskbarPreview.WindowsModeIsLight(null), "Preview uses Windows mode with dark fallback");
        Check(window.TaskbarItemInfo?.ThumbButtonInfos.Count==3, "Taskbar preview exposes three media controls");
        Check(TaskbarLyrics.Windows.TaskbarThumbnail.PreventPeek(new System.Windows.Interop.WindowInteropHelper(window).Handle),
            "Windows accepts disabling the desktop Peek card for this window");
        var narrowCard=PlaybackTaskbarPreview.Render(window.ViewModel.State,window.ViewModel.PlaybackButtonLabel,
            320,150,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue);
        Check(narrowCard.PixelWidth==180 && narrowCard.PixelHeight==150,
            "Thumbnail submits a compact card rather than padding to the shell maximum");
        Check(window.TaskbarItemInfo!.ThumbButtonInfos.All(button => button.Command is TaskbarPreviewCommand),
            "Thumbnail controls avoid transient busy dimming");
        Check(window.TaskbarItemInfo.Description=="TaskbarLyrics", "Shell caption is independent of song title length");
        var mediaAvailable=true;
        var mediaSends=0;
        var guardedPreviewCommand=new TaskbarPreviewCommand(new RelayCommand(_ => mediaSends++, _ => mediaAvailable));
        guardedPreviewCommand.Execute(null);
        mediaAvailable=false;
        Check(guardedPreviewCommand.CanExecute(null), "Busy preview retains button appearance");
        guardedPreviewCommand.Execute(null);
        Check(mediaSends==1, "Busy preview rejects duplicate or unsupported commands");
        mediaAvailable=true;
        guardedPreviewCommand.Execute(null);
        Check(mediaSends==2, "Preview command resumes after busy state ends");
        var longState=window.ViewModel.State with {Raw=window.ViewModel.State.Raw with {DisplayText=new string('长',500)}};
        var longCard=PlaybackTaskbarPreview.Render(longState,"播放",320,150,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue,4000);
        Check(longCard.PixelWidth==narrowCard.PixelWidth && longCard.PixelHeight==narrowCard.PixelHeight,
            "Very long lyrics never resize the thumbnail");
        var reportedCard=PlaybackTaskbarPreview.Render(window.ViewModel.State,"播放",400,215,
            Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue,dpiScale:2);
        Check(reportedCard.PixelWidth==258 && reportedCard.PixelHeight==215,
            "Reported 400x215 shell request has a 258px card without wide side padding");
        var staticBefore=PlaybackTaskbarPreview.Render(longState,"播放",400,215,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue,0);
        var staticAfter=PlaybackTaskbarPreview.Render(longState,"暂停",400,215,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue,9000);
        var beforePixels=new byte[staticBefore.PixelWidth*staticBefore.PixelHeight*4];
        var afterPixels=new byte[beforePixels.Length];
        staticBefore.CopyPixels(beforePixels,staticBefore.PixelWidth*4,0);
        staticAfter.CopyPixels(afterPixels,staticAfter.PixelWidth*4,0);
        Check(beforePixels.SequenceEqual(afterPixels), "Long thumbnail lyrics are static across elapsed time and toolbar labels");
        Check(PlaybackTaskbarPreview.BitmapContentKey(window.ViewModel.State)!=PlaybackTaskbarPreview.BitmapContentKey(longState),
            "Changed lyric content invalidates the static preview");
        var transparentCard=PlaybackTaskbarPreview.Render(window.ViewModel.State,"播放",400,215,
            Brushes.Transparent,Brushes.White,Brushes.LightGray,Brushes.SteelBlue,dpiScale:2);
        var transparentPixels=new byte[transparentCard.PixelWidth*transparentCard.PixelHeight*4];
        transparentCard.CopyPixels(transparentPixels,transparentCard.PixelWidth*4,0);
        Check(transparentPixels[3]==0 && transparentPixels[^1]==0,
            "Transparent preview leaves corners transparent without an opaque backing fill");
        Check(Enumerable.Range(0,transparentPixels.Length/4).Any(i=>transparentPixels[i*4+3]>0),
            "Transparent preview preserves visible text pixels");
        Check(Enumerable.Range(0,transparentPixels.Length/4).All(i=>
            transparentPixels[i*4]<=transparentPixels[i*4+3] && transparentPixels[i*4+1]<=transparentPixels[i*4+3]
            && transparentPixels[i*4+2]<=transparentPixels[i*4+3]), "Preview pixels retain valid premultiplied alpha");
        foreach(var dpi in new[]{1.0,1.25,1.5,2.0})
        {
            var fixedCard=PlaybackTaskbarPreview.Render(window.ViewModel.State,"播放",640,480,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue,dpiScale:dpi);
            var largerBudget=PlaybackTaskbarPreview.Render(window.ViewModel.State,"播放",1024,768,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue,dpiScale:dpi);
            Check(fixedCard.PixelWidth==largerBudget.PixelWidth && fixedCard.PixelHeight==largerBudget.PixelHeight,
                "Extra shell capacity does not add whitespace to the compact card");
            var pixels=new byte[fixedCard.PixelWidth*fixedCard.PixelHeight*4]; fixedCard.CopyPixels(pixels,fixedCard.PixelWidth*4,0);
            Check(Enumerable.Range(0,pixels.Length/4).All(i=>pixels[i*4+3]==255),
                "Every preview pixel is opaque, including right and bottom edges at fractional DPI");
        }
        foreach(var size in new[]{(320,170),(200,100),(640,340)})
        {
            var bitmap=PlaybackTaskbarPreview.Render(window.ViewModel.State,window.ViewModel.PlaybackButtonLabel,
                size.Item1,size.Item2,Brushes.White,Brushes.Black,Brushes.Gray,Brushes.SteelBlue);
            Check(bitmap.PixelWidth<=size.Item1 && bitmap.PixelHeight<=size.Item2 && bitmap.IsFrozen,
                "Playback card fits the system thumbnail request at every scale");
        }

        window.Navigate(SettingsPage.Overview);
        var reduced = Motion.GetReduce(window);
        window.SetCurrentValue(Motion.ReduceProperty, false);
        try
        {
            foreach (var destination in new[] { SettingsPage.About, SettingsPage.Appearance, SettingsPage.General, SettingsPage.Overview })
            {
                window.Navigate(destination);
                var list = (ListBox)window.FindName("NavList");
                var target = list.SelectedItem as FrameworkElement ?? (FrameworkElement)window.FindName("AboutButton");
                var indicator = (Border)window.FindName("NavigationIndicator");
                var sidebar = (FrameworkElement)window.FindName("SidebarLayout");
                if (indicator.Visibility == Visibility.Visible)
                {
                    var expected = target.TranslatePoint(new Point(), sidebar);
                    Check(Math.Abs(Canvas.GetTop(indicator) - expected.Y) < .1 &&
                        Math.Abs(Canvas.GetLeft(indicator) - expected.X) < .1,
                        "Navigation highlight stays inside the target even across large sidebar gaps");
                    Check(!DependencyPropertyHelper.GetValueSource(indicator, Canvas.TopProperty).IsAnimated,
                        "Navigation never animates a selected surface through empty sidebar space");
                }
            }
        }
        finally { window.SetCurrentValue(Motion.ReduceProperty, reduced); }
        window.Navigate(SettingsPage.Appearance);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var cached = ((ContentControl)window.FindName("PageHost")).Content;
        window.Navigate(SettingsPage.General);
        Check(window.FindName("BackButton") is null, "Title bar has no back arrow");
        window.GoBack();
        Check(window.CurrentPage == SettingsPage.Appearance, "Navigation history restores quick-action destination");
        Check(ReferenceEquals(cached, ((ContentControl)window.FindName("PageHost")).Content), "Back preserves cached page and scroll state");
        Check(window.GoBack() && window.CurrentPage == SettingsPage.Overview, "Window returns to playback");
        window.Navigate(SettingsPage.About);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var aboutButton=(Button)window.FindName("AboutButton");
        Check(((SolidColorBrush)aboutButton.Background).Color==((SolidColorBrush)window.FindResource("NavSelectedBrush")).Color,
            "About navigation uses the same active-page feedback as the sidebar");
        var about=(UserControl)((ContentControl)window.FindName("PageHost")).Content;
        Check(((TextBlock)about.FindName("VersionValue")).Text==window.ViewModel.Version,
            "About presents the running assembly version");
        window.GoBack();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(((SolidColorBrush)aboutButton.Background).Color.A==0,
            "Leaving About clears its active-page feedback");
        var navigation=(ListBox)window.FindName("NavList");
        var originalHeight=navigation.Height;
        try
        {
            navigation.Height=120;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var scroll=Descendants(navigation).OfType<ScrollViewer>().First();
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Visible && scroll.ScrollableHeight>0,
                "Short sidebar retains an available scrollbar");
            scroll.ScrollToBottom();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var item=(ListBoxItem)navigation.Items[navigation.Items.Count-1];
            var itemRoot=(Border)item.Template.FindName("Root",item);
            var aboutRoot=(Border)aboutButton.Template.FindName("Root",aboutButton);
            Check(scroll.VerticalOffset>0 && Math.Abs(itemRoot.ActualWidth-aboutRoot.ActualWidth)<1,
                "Sidebar overflow scrolls without narrowing navigation feedback");
        }
        finally
        {
            navigation.Height=originalHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        }
    }
    private static void TestControlGeometry(SettingsWindow window, SettingsPage page)
    {
        if (page == SettingsPage.Overview)
        {
            var overview=(UserControl)((ContentControl)window.FindName("PageHost")).Content;
            Check(((ScrollingLyrics)overview.FindName("CurrentLyric")).FontWeight==FontWeights.SemiBold,"Playback lyric retains semibold weight");
            Check(((ScrollingLyrics)overview.FindName("CurrentLyric")).FontFamily.Source == ((FontFamily)Application.Current.FindResource("PlaybackLyricFont")).Source,
                "In-app lyrics use the regular UI lyric font family independently of taskbar font settings");
        }
        var footerLabel=(FrameworkElement)((CheckBox)window.FindName("OverlayToggle")).Template.FindName("Label",(CheckBox)window.FindName("OverlayToggle"));
        var sidebarToggle=(CheckBox)window.FindName("OverlayToggle");
        var track=(FrameworkElement)sidebarToggle.Template.FindName("Track",sidebarToggle);
        var thumb=(FrameworkElement)sidebarToggle.Template.FindName("Thumb",sidebarToggle);
        var icon=(FrameworkElement)sidebarToggle.Template.FindName("Icon",sidebarToggle);
        var trackCenter=track.TransformToAncestor(sidebarToggle).Transform(new Point(0,track.ActualHeight/2)).Y;
        var thumbCenter=thumb.TransformToAncestor(sidebarToggle).Transform(new Point(0,thumb.ActualHeight/2)).Y;
        var iconCenter=icon.TransformToAncestor(sidebarToggle).Transform(new Point(0,icon.ActualHeight/2)).Y;
        Check(Math.Abs(trackCenter-thumbCenter)<=1 && Math.Abs(trackCenter-iconCenter)<=1,"Sidebar switch, icon and thumb are vertically centered");
        if (page == SettingsPage.Overview) TestSidebarInkCenters(sidebarToggle, footerLabel, track, icon);
        Check(Math.Abs(track.ActualWidth-42)<=1 && Math.Abs(track.ActualHeight-24)<=1,"Sidebar switch uses the rounded 42 by 24 DIP track");
        var naturalLabel=new TextBlock { Text=sidebarToggle.Content.ToString(), FontFamily=sidebarToggle.FontFamily, FontSize=sidebarToggle.FontSize, FontWeight=sidebarToggle.FontWeight };
        naturalLabel.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        var labelLeft=footerLabel.TransformToAncestor(sidebarToggle).Transform(new Point()).X;
        var trackLeft=track.TransformToAncestor(sidebarToggle).Transform(new Point()).X;
        Check(footerLabel.ActualWidth>=naturalLabel.DesiredSize.Width-1 && labelLeft+naturalLabel.DesiredSize.Width+5<=trackLeft,"Longer sidebar switch leaves the complete Chinese label and a gap visible");
        Check(trackLeft+track.ActualWidth<=sidebarToggle.ActualWidth-5,"Sidebar switch remains inside the right inset");
        var aboutLabel=(FrameworkElement)window.FindName("AboutLabel");
        var aboutButton=(Button)window.FindName("AboutButton");
        var aboutRoot=(Border)aboutButton.Template.FindName("Root",aboutButton);
        var sidebarRoot=(Border)sidebarToggle.Template.FindName("Root",sidebarToggle);
        foreach(var root in ((ListBox)window.FindName("NavList")).Items.OfType<ListBoxItem>()
                    .Select(item=>(Border)item.Template.FindName("Root",item)).Append(sidebarRoot))
        {
            Check(Math.Abs(root.ActualWidth-aboutRoot.ActualWidth)<1 && Math.Abs(root.ActualHeight-aboutRoot.ActualHeight)<1
                && root.CornerRadius==aboutRoot.CornerRadius
                && Math.Abs(root.TranslatePoint(new Point(),window).X-aboutRoot.TranslatePoint(new Point(),window).X)<1,
                "Every sidebar surface shares the same bounds and corner radius");
        }
        var navLabel=(TextBlock)((StackPanel)((ListBoxItem)((ListBox)window.FindName("NavList")).Items[0]).Content).Children[1];
        var navX=navLabel.TransformToAncestor(window).Transform(new Point()).X;
        Check(Math.Abs(footerLabel.TransformToAncestor(window).Transform(new Point()).X-navX)<=1 && Math.Abs(aboutLabel.TransformToAncestor(window).Transform(new Point()).X-navX)<=1,"Footer labels share navigation text alignment");
        foreach (var button in Descendants(window).OfType<Button>().Where(b => b.IsVisible && b.ActualWidth > 0 && b.Content is string))
        {
            if (button.Template.FindName("ContentHost",button) is not FrameworkElement host) continue;
            if (button.HorizontalContentAlignment != HorizontalAlignment.Center || button.VerticalContentAlignment != VerticalAlignment.Center) continue;
            var p = host.TransformToAncestor(button).Transform(new Point(0,0));
            Check(Math.Abs(p.X + host.ActualWidth/2 - button.ActualWidth/2) <= 1.1, "Button text horizontal center: " + button.Content);
            Check(Math.Abs(p.Y + host.ActualHeight/2 - button.ActualHeight/2) <= 1.1, "Button text vertical center: " + button.Content);
        }
        foreach (var editor in Descendants(window).OfType<NumberEditor>().Where(n => n.IsVisible))
        {
            var input = (TextBox)editor.FindName("Input");
            Check(Math.Abs(input.ActualHeight-30) <= 1, "Numeric input uses compact 30 DIP height");
        }
        foreach(var combo in Descendants(window).OfType<ComboBox>().Where(c=>c.IsVisible && c.ActualWidth>0 && !c.IsEditable))
        {
            var toggle=(System.Windows.Controls.Primitives.ToggleButton)combo.Template.FindName("DropDownToggle",combo);
            Check(toggle.ActualWidth>=combo.ActualWidth-3,"Dropdown toggle spans complete box");
            var hit=combo.InputHitTest(new Point(8,combo.ActualHeight/2)) as DependencyObject;
            while(hit is not null && !ReferenceEquals(hit,toggle) && !ReferenceEquals(hit,combo)) hit=hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            Check(ReferenceEquals(hit,toggle),"Left-hand side of dropdown is clickable");
        }
        if(page==SettingsPage.Overview)
        {
            var host=(ContentControl)window.FindName("PageHost");
            Check(!Descendants(host).OfType<TextBlock>().Any(t=>t.Text is "逐字" or "本地" or "自动匹配" or "手动匹配"),"Playback metadata removed");
            var primary = Descendants(host).OfType<Button>().Single(b=>b.Content is "选择歌词");
            Check((primary.Background as SolidColorBrush)?.Color.A == 0, "Playback action has no permanent selected fill");
            foreach(var action in Descendants(host).OfType<Button>().Where(b=>b.Content is "选择歌词"))
            {
                var hover = (FrameworkElement)action.Template.FindName("HoverLayer",action);
                var rootBorder=(Border)action.Template.FindName("Root",action);
                Check(rootBorder.CornerRadius==(CornerRadius)window.FindResource("AppearanceControlCornerRadius"),"Lyric selection follows the shared control corner radius");
                Check(action.BorderThickness==new Thickness(0),"Playback actions have no gray outline");
                Check(action.ActualWidth <= 80 && Math.Abs(action.ActualWidth-hover.ActualWidth)<1,"Compact lyric selection keeps feedback fitted to its content width");
                var press = (FrameworkElement)action.Template.FindName("PressLayer",action);
                Check(Math.Abs(hover.ActualWidth-action.ActualWidth)<=2 && Math.Abs(hover.ActualHeight-action.ActualHeight)<=2,"Hover fills complete button, not text: "+action.Content);
                Check(Math.Abs(press.ActualWidth-hover.ActualWidth)<1 && Math.Abs(press.ActualHeight-hover.ActualHeight)<1,"Pressed feedback fills same bounds");
                var reduced=Motion.GetReduce(action); Motion.SetReduce(action,true);
                action.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice,0){ RoutedEvent=Mouse.MouseEnterEvent });
                Check(hover.Opacity==1,"Pointer entry highlights full action");
                action.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice,0){ RoutedEvent=Mouse.MouseLeaveEvent });
                Check(hover.Opacity==0 && press.Opacity==0,"Pointer exit clears feedback rather than leaving selection");
                Motion.SetReduce(action,reduced);
            }
            Check(!Descendants(window).OfType<TextBlock>().Any(t=>t.IsVisible && t.Text == "已保存"), "No persistent saved footer");
            Check(!Descendants(host).OfType<TextBlock>().Any(t=>t.ToolTip is not null),"Song metadata and lyrics have no hover popups");
        }
    }
    private static async Task TestExpansionAndAnimation(SettingsWindow window,string? outDir)
    {
        ThemeManager.Apply("Light",false);
        window.Navigate(SettingsPage.General);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var expander=Descendants(window).OfType<Expander>().First(e=>e.Header as string=="重置与清理");
        expander.IsExpanded=true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var expansion=(Border)expander.Template.FindName("ExpansionHost",expander);
        Check(expansion.Visibility==Visibility.Visible && expansion.ActualHeight>0,"Expander opens under reduced motion without losing content");
        expander.IsExpanded=false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(expansion.Visibility==Visibility.Collapsed,"Collapsed expander does not leave focusable hidden controls");
        window.Navigate(SettingsPage.Appearance);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var host=(FrameworkElement)window.FindName("PageHost");
        // Drive the storyboard clock explicitly; no sleeps or machine-timing assumptions.
        var storyboard=Motion.CreateRevealStoryboard(host);
        storyboard.Begin(host,System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace,true);
        storyboard.Pause(host);
        foreach(var ms in new[]{0,55,110,165,220})
        {
            storyboard.SeekAlignedToLastTick(host,TimeSpan.FromMilliseconds(ms),System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            window.UpdateLayout();
            Check(host.Opacity >=0 && host.Opacity <=1,"Page fade stays in opacity range");
            if(ms==110) Check(host.Opacity>0 && host.Opacity<1,"Page transition has a visible intermediate state");
            if(outDir is not null) Capture(window,Path.Combine(outDir,$"Page-transition-{ms:000}.png"),1);
        }
        storyboard.Remove(host);
    }
    private static void TestColorRecovery(ConfigService config)
    {
        config.Update(c=>{ c.Display.FontSize=23; c.Display.VerticalOffsetPx=3; c.Lyrics.GlobalOffsetMs=170; c.Display.NormalColor="#FF123456"; c.Display.HighlightColor="#FFABCDEF"; },scheduleSave:false);
        using var vm=new SettingsViewModel(config,new FakeSettings());
        vm.Activate(SettingsPage.Appearance);
        vm.PresetCommand.Execute(PalettePreset.All[1]);
        vm.RememberNormalColorCommand.Execute("#FF123456");
        vm.RememberNormalColorCommand.Execute("#FFABCDEF");
        Check(vm.RecentNormalColors[0]=="#FFABCDEF","Recently committed colors appear newest first");
        vm.Activate(SettingsPage.General);
        vm.ResetColorsCommand.Execute(null);
        Check(LyricColorScheme.Capture(config.Current)==LyricColorScheme.Default,"Color reset returns original default palette");
        Check(config.Current.Display.FontSize==23 && config.Current.Display.VerticalOffsetPx==3 && config.Current.Lyrics.GlobalOffsetMs==170,"Color reset preserves fonts, placement and timing");
        Check(vm.RecentNormalColors.Contains("#FF123456"),"Resetting colors preserves the user's recent colors");
        vm.AnimationsEnabled=true; Check(!config.Current.General.ReduceMotion,"Animation switch on has direct enabled semantics");
        vm.AnimationsEnabled=false; Check(config.Current.General.ReduceMotion,"Animation switch off maps to reduced motion");
        config.ResetAppearanceToDefaults(); config.Update(c=>c.Lyrics.GlobalOffsetMs=100,scheduleSave:false);
    }
    private static async Task TestRealAnimationTriggers(SettingsWindow window,ConfigService config,string? outDir)
    {
        config.Update(c=>c.General.ReduceMotion=false,scheduleSave:false);
        ThemeManager.Apply("Light",false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var host=(FrameworkElement)window.FindName("PageHost");
        var allowed=Motion.Allowed(host);
        var before=Motion.GetTransitionStartCount(host);
        var nav=(ListBox)window.FindName("NavList");
        nav.SelectedItem=nav.Items.OfType<ListBoxItem>().Single(i=>i.Tag as string=="General");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(window.CurrentPage==SettingsPage.General,"Real sidebar selection navigates");
        Check(Motion.GetTransitionStartCount(host)==before+(allowed?1:0),"Real page selection reaches the animation entry point");
        if(allowed)
        {
            var storyboard=Motion.GetActiveStoryboard(host)!;
            storyboard.Pause(host); storyboard.SeekAlignedToLastTick(host,TimeSpan.FromMilliseconds(110),System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            Check(host.Opacity>0 && host.Opacity<1,"Actual navigation, not only factory, fades between frames");
            var shift=((TranslateTransform)host.RenderTransform).Y;
            Check(shift>0 && shift<8,"Actual page transition moves the displayed transform");
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Actual-page-animation-mid.png"),1);
            storyboard.Remove(host);
        }
        var check=(CheckBox)window.FindName("OverlayToggle");
        var old=check.IsChecked;
        var oldStarts=Motion.GetTransitionStartCount(check);
        check.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,old!=true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(Motion.GetTransitionStartCount(check)==oldStarts+(allowed?1:0),"Actual checked event starts the switch animation");
        if(allowed)
        {
            var storyboard=Motion.GetActiveStoryboard(check)!;
            storyboard.Pause(check); storyboard.SeekAlignedToLastTick(check,TimeSpan.FromMilliseconds(80),System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            var thumb=(FrameworkElement)check.Template.FindName("Thumb",check);
            var track=(Border)check.Template.FindName("Track",check);
            var travel=Motion.SwitchTravel(track.ActualWidth,thumb.ActualWidth,track.BorderThickness.Left+track.BorderThickness.Right+thumb.Margin.Left+thumb.Margin.Right);
            var x=((TranslateTransform)thumb.RenderTransform).X;
            Check(x>0 && x<travel,$"Switch thumb has an intermediate position across its actual track (x={x}, travel={travel})");
            if(outDir is not null) Capture(window,Path.Combine(outDir,"Actual-switch-animation-mid.png"),1);
            storyboard.SeekAlignedToLastTick(check,TimeSpan.FromMilliseconds(200),System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            Check(Math.Abs(((TranslateTransform)thumb.RenderTransform).X-(check.IsChecked==true?travel:0))<.01,"Switch animation finishes at the correct endpoint of the longer track");
            storyboard.Remove(check);
        }
        check.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,old);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var restoredStoryboard=Motion.GetActiveStoryboard(check);
        restoredStoryboard?.SeekAlignedToLastTick(check,TimeSpan.FromMilliseconds(200),System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
        var restoredThumb=(FrameworkElement)check.Template.FindName("Thumb",check);
        var restoredTrack=(Border)check.Template.FindName("Track",check);
        var restoredTravel=Motion.SwitchTravel(restoredTrack.ActualWidth,restoredThumb.ActualWidth,restoredTrack.BorderThickness.Left+restoredTrack.BorderThickness.Right+restoredThumb.Margin.Left+restoredThumb.Margin.Right);
        Check(Math.Abs(((TranslateTransform)restoredThumb.RenderTransform).X-(old==true?restoredTravel:0))<.01,"Reverse toggle restores the opposite endpoint of the longer track");
        restoredStoryboard?.Remove(check);
        window.Navigate(SettingsPage.Appearance);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var appearance=(UserControl)((ContentControl)window.FindName("PageHost")).Content;
        var combo=(ComboBox)((LyricsPreview)appearance.FindName("Preview")).FindName("SourceChoice");
        var popup=(System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup",combo);
        var child=(FrameworkElement)popup.Child;
        var popupStarts=Motion.GetTransitionStartCount(child);
        try
        {
            combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty,true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(combo.IsDropDownOpen,"Actual dropdown opens");
            Check(Motion.GetTransitionStartCount(child)==popupStarts+(allowed?1:0),"Popup open state reaches its animation entry point");
            if(allowed)
            {
                var storyboard=Motion.GetActiveStoryboard(child)!;
                storyboard.Pause(child); storyboard.SeekAlignedToLastTick(child,TimeSpan.FromMilliseconds(80),System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
                Check(child.Opacity>0 && child.Opacity<1,"Actual dropdown has a visible fade interval");
                var shift=((TranslateTransform)child.RenderTransform).Y;
                Check(shift<0 && shift>-4,"Dropdown opening moves downward from above the resting surface");
                if(outDir is not null) CaptureElement(child,Path.Combine(outDir,"Actual-dropdown-animation-mid.png"),1);
            }
        }
        finally { combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty,false); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if(allowed)
        {
            Check(popup.IsOpen && !child.IsHitTestVisible,"Closing dropdown remains visible but cannot activate stale items");
            var closing=Motion.GetActiveStoryboard(child)!;
            closing.Pause(child);
            closing.SeekAlignedToLastTick(child,TimeSpan.FromMilliseconds(70),System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            Check(child.Opacity>0 && child.Opacity<1,"Dropdown closing has a visible reverse animation");
            if(outDir is not null) CaptureElement(child,Path.Combine(outDir,"Actual-dropdown-close-mid.png"),1);
            closing.SeekAlignedToLastTick(child,TimeSpan.FromMilliseconds(250),System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Check(!popup.IsOpen && child.Opacity==1,"Dropdown hides only after its closing animation and resets for reopening");
        config.Update(c=>c.General.ReduceMotion=true,scheduleSave:false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void TestTray(ConfigService config,string? outDir)
    {
        using var icon = new System.Windows.Forms.NotifyIcon { Visible=false };
        var state=Program.Fixture();
        var invoked=new List<string>();
        using var tray = new ModernTrayMenu(icon,config,()=>invoked.Add("open"),()=>invoked.Add("rematch"),
            ()=>invoked.Add("exit"),_=>invoked.Add("overlay"),()=>state);
        var menu=(System.Windows.Forms.ContextMenuStrip)typeof(ModernTrayMenu).GetField("_menu",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(tray)!;
        var actions=menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
        Check(actions.Select(item=>item.Text).SequenceEqual(new[]{"显示任务栏歌词","打开窗口","选择歌词","退出"}),
            "Tray contains only overlay, open window, choose lyrics and exit actions");
        var labels=menu.Items.OfType<System.Windows.Forms.ToolStripLabel>().ToArray();
        Check(labels.Length==2 && labels.All(item=>item.TextAlign==System.Drawing.ContentAlignment.MiddleLeft),
            "Tray track and status labels are left aligned");
        var rematch=actions.Single(item=>item.Text=="选择歌词");
        foreach(var theme in new[]{"Dark","Light"})
        {
            ThemeManager.Apply(theme,false); tray.RefreshPresentation();
            // Exercise native ShowCore sizing at real DPI without displaying an icon
            // or a visible menu. RoundedContextMenuStrip also has WS_EX_NOACTIVATE.
            menu.Opacity=0;
            try
            {
                menu.Show(new System.Drawing.Point(0,0));
                Check(labels.Cast<System.Windows.Forms.ToolStripItem>().Concat(actions).All(item=>menu.ClientRectangle.Contains(item.Bounds)),
                    "First native tray show fully contains its rows at the current DPI");
            }
            finally { menu.Close(); menu.Opacity=1; }
            menu.Size=menu.GetPreferredSize(System.Drawing.Size.Empty); menu.PerformLayout();
            var firstLayoutSize=menu.Size;
            menu.Size=menu.GetPreferredSize(System.Drawing.Size.Empty); menu.PerformLayout();
            Check(menu.Size==firstLayoutSize,"Two successive tray layouts have identical dimensions");
            // Separators cannot select; clear the prior render's selection without input injection.
            typeof(System.Windows.Forms.ToolStrip).GetMethod("ClearAllSelections",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                .Invoke(menu,null);
            using var normal=new System.Drawing.Bitmap(menu.Width,menu.Height);
            menu.DrawToBitmap(normal,new System.Drawing.Rectangle(0,0,menu.Width,menu.Height));
            if(outDir is not null) normal.Save(Path.Combine(outDir,theme+"-Tray.png"),System.Drawing.Imaging.ImageFormat.Png);
            Check(menu.GetPreferredSize(System.Drawing.Size.Empty)==menu.Size,"Tray sizing stabilizes after native DPI layout");
            Check(labels.Cast<System.Windows.Forms.ToolStripItem>().Concat(actions).All(item=>menu.ClientRectangle.Contains(item.Bounds)),
                "Tray rows fit within the real menu rather than clipping on the right or bottom");
            foreach(var item in labels.Cast<System.Windows.Forms.ToolStripItem>().Concat(actions))
                CheckInkLeftAligned(normal,item,theme);
            rematch.Select();
            Check(rematch.Selected,"Tray hover fixture selects the choose-lyrics action without desktop input");
            using var hover=new System.Drawing.Bitmap(menu.Width,menu.Height);
            menu.DrawToBitmap(hover,new System.Drawing.Rectangle(0,0,menu.Width,menu.Height));
            var brush=(SolidColorBrush)Application.Current.FindResource("PlaybackHoverBrush");
            var expected=System.Drawing.Color.FromArgb(brush.Color.A,brush.Color.R,brush.Color.G,brush.Color.B);
            var sample=hover.GetPixel(rematch.Bounds.Left+8,rematch.Bounds.Top+rematch.Height/2);
            Check(sample.ToArgb()==expected.ToArgb(),theme+" tray hover uses the shared playback blue");
            CheckInkLeftAligned(hover,rematch,theme+" hover");
            if(outDir is not null)
            {
                normal.Save(Path.Combine(outDir,theme+"-Tray.png"),System.Drawing.Imaging.ImageFormat.Png);
                hover.Save(Path.Combine(outDir,theme+"-Tray-Hover.png"),System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        state=state with { LyricsLoading=true };
        tray.RefreshPresentation();
        Check(!rematch.Enabled,"Tray lyric selection is disabled while lyrics load");
        rematch.PerformClick();
        Check(invoked.Count==0,"Disabled tray lyric selection cannot dispatch an action");
        state=state with { LyricsLoading=false, Track=null };
        tray.RefreshPresentation();
        rematch.PerformClick();
        Check(!rematch.Enabled && invoked.Count==0,"Tray lyric selection remains inert without a track");
        state=Program.Fixture(); tray.RefreshPresentation();
        var overlayBefore=config.Current.General.ShowOverlay;
        foreach(var action in actions) action.PerformClick();
        Check(invoked.SequenceEqual(new[]{"overlay","open","rematch","exit"}),"Four tray actions dispatch their own callbacks exactly once");
        Check(config.Current.General.ShowOverlay!=overlayBefore,"Tray overlay toggle updates the isolated configuration");
        // Restore this shared fixture's state for later checks.
        config.Update(c=>c.General.ShowOverlay=overlayBefore);

        static void CheckInkLeftAligned(System.Drawing.Bitmap bitmap,System.Windows.Forms.ToolStripItem item,string theme)
        {
            var color=item.ForeColor;
            var bounds=item.Bounds;
            var left=bitmap.Width; var right=-1;
            // Exact/near foreground pixels identify real glyph ink; ignore blue check marks,
            // rounded menu outlines and antialiased fringes around the glyph edges.
            for(var y=Math.Max(0,bounds.Top+3);y<Math.Min(bitmap.Height,bounds.Bottom-3);y++)
                for(var x=Math.Max(0,bounds.Left+5);x<Math.Min(bitmap.Width,bounds.Right-5);x++)
                {
                    var pixel=bitmap.GetPixel(x,y);
                    if(Math.Abs(pixel.R-color.R)<=12 && Math.Abs(pixel.G-color.G)<=12 && Math.Abs(pixel.B-color.B)<=12)
                    { left=Math.Min(left,x);right=Math.Max(right,x); }
                }
            var expected=bounds.Left+Math.Round(22*(item.Owner?.DeviceDpi ?? 96)/96d);
            // Glyph side bearings scale with the native menu font; use DIP tolerance at high DPI.
            var tolerance=4*(item.Owner?.DeviceDpi ?? 96)/96d;
            Check(right>=left && Math.Abs(left-expected)<=tolerance,
                $"{theme} tray text '{item.Text}' follows the shared left edge (ink={left:F1}, expected={expected:F1})");
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd,int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    private static async Task TestShell(string? outDir)
    {
        // Explicit opt-in only: genuine Win32 integration, NOT part of deterministic Fast/Full gates.
        var before=GetForegroundWindow();
        var spaceMode = 0;
        var fixtureFullscreen = false;
        long layoutNow = 1000;
        var uiThread = Environment.CurrentManagedThreadId;
        var measuredOnUi = false;
        using var measurementGate = new System.Threading.ManualResetEventSlim(true);
        TaskCompletionSource? readStarted = null;
        var overlay=new TaskbarLyrics.App.MainWindow((bar, left, right) =>
        {
            measuredOnUi |= Environment.CurrentManagedThreadId == uiThread;
            readStarted?.TrySetResult();
            measurementGate.Wait();
            return spaceMode switch
        {
            1 => new TaskbarFreeRegionLocator.FreeRegionResult(true, bar.Left + 40, new(bar.Left + 8, bar.Left + 8), new(bar.Right - 20, bar.Right - 20), "fixture-no-space"),
            2 => new TaskbarFreeRegionLocator.FreeRegionResult(true, bar.Left + 40, new(bar.Left + 8, bar.Left + 8), new(bar.Right - 1200, bar.Right - 200), "fixture-right-space"),
            3 => new TaskbarFreeRegionLocator.FreeRegionResult(true, bar.Left + 1400, new(bar.Left + 8, bar.Left + 1208), new(bar.Right - 1200, bar.Right - 200), "fixture-left-recovered"),
            _ => TaskbarFreeRegionLocator.Measure(bar, left, right)
            };
        }, () => layoutNow, (_, _) => fixtureFullscreen);
        async Task SettleLayout()
        {
            for (var i = 0; i < 3; i++)
            {
                layoutNow += TaskbarLayoutStability.SettleMs;
                await overlay.ForceRelayoutForDiagnosticsAsync();
            }
        }
        string ShellFixtureState()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            string State(string field) => $"{field}={typeof(TaskbarLyrics.App.MainWindow).GetField(field, flags)?.GetValue(overlay)}";
            var diagnostic = OverlayLayoutDiagnostics.Get();
            return $"visible={overlay.IsVisible}, alignment={((KaraokeTextControl)overlay.FindName("Karaoke")).PlacementAlignment}, "
                + $"mode={diagnostic.Mode}, width={diagnostic.FinalWidthPx}, source={diagnostic.Source}, "
                + $"clock={layoutNow}, fixtureMode={spaceMode}, shellFullscreen={TaskbarLyrics.Windows.FullscreenDetector.IsShellFullscreenState()}, "
                + string.Join(", ", new[] { "_overlayVisible", "_fullscreenSuppressed", "_spaceSuppressed", "_placementPhase", "_pendingPlacement", "_layoutRevision", "_layoutReadPending" }.Select(State));
        }
        void CompleteMotion()
        {
            var tick = typeof(TaskbarLyrics.App.MainWindow).GetMethod("TickPlacement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            layoutNow += OverlayPlacementMotion.ExitMs + 1; tick.Invoke(overlay, null);
            layoutNow += OverlayPlacementMotion.EnterMs + 1; tick.Invoke(overlay, null);
        }
        try
        {
            overlay.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var hwnd=new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            var style=GetWindowLong(hwnd,-20);
            Check((style & 0x20)!=0,"Real overlay WS_EX_TRANSPARENT");
            Check((style & 0x08000000)!=0,"Real overlay WS_EX_NOACTIVATE");
            Check((style & 0x80)!=0 && !overlay.ShowInTaskbar,"Real overlay tool window hidden from taskbar");
            var unchanged=GetForegroundWindow()==before;
            overlay.SetOverlayVisible(false); Check(!overlay.IsVisible,"Real overlay hide");
            overlay.SetOverlayVisible(true); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            spaceMode = 1; await SettleLayout();
            Check(!overlay.IsVisible && OverlayLayoutDiagnostics.Get().FinalWidthPx == 0 && !OverlayLayoutDiagnostics.Get().UsesFallbackWidth,
                "Measured no-space hides the real overlay without entering manual fallback");
            overlay.SetOverlayVisible(true);
            Check(!overlay.IsVisible, "Explicit show and topmost recovery cannot resurrect a no-space overlay");
            spaceMode = 2; await SettleLayout(); CompleteMotion();
            var actualKaraoke = (KaraokeTextControl)overlay.FindName("Karaoke");
            Check(overlay.IsVisible && actualKaraoke.PlacementAlignment == "Right" && OverlayLayoutDiagnostics.Get().Mode == "右侧空白",
                "Real hidden overlay recovers into right space with right-aligned rendering; " + ShellFixtureState());
            var fixedWidth = overlay.Width;
            spaceMode = 3; await overlay.ForceRelayoutForDiagnosticsAsync();
            Check(overlay.Width == fixedWidth && actualKaraoke.PlacementAlignment == "Right", "Unconfirmed left geometry cannot resize or realign the displayed right lyrics");
            spaceMode = 1; layoutNow += 50; await overlay.ForceRelayoutForDiagnosticsAsync();
            Check(overlay.IsVisible && overlay.Width == fixedWidth, "Intermediate no-space cannot hide the host or change its geometry");
            spaceMode = 2; layoutNow += 50; await overlay.ForceRelayoutForDiagnosticsAsync();
            spaceMode = 3; layoutNow += 50; await overlay.ForceRelayoutForDiagnosticsAsync();
            await SettleLayout(); CompleteMotion();
            Check(overlay.IsVisible && actualKaraoke.PlacementAlignment is null && OverlayLayoutDiagnostics.Get().Mode == "左侧空白",
                "Recovered left space restores saved alignment without changing configuration");
            Check(!measuredOnUi, "Taskbar measurement never executes on the WPF dispatcher");
            var finalWidth = overlay.Width;
            await overlay.ForceRelayoutForDiagnosticsAsync(); CompleteMotion();
            Check(overlay.Width == finalWidth && actualKaraoke.Opacity == 1
                && actualKaraoke.RenderTransform is TranslateTransform { X: 0 }, "Settled geometry and lyric transform do not adjust a second time");
            // A deliberately stalled worker must neither block the dispatcher nor undo a later Hide.
            measurementGate.Reset();
            readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedRead = overlay.ForceRelayoutForDiagnosticsAsync();
            await readStarted.Task;
            await Dispatcher.Yield(DispatcherPriority.Render);
            Check(!blockedRead.IsCompleted, "Dispatcher remains responsive while UIA read is blocked");
            overlay.SetOverlayVisible(false);
            measurementGate.Set();
            await blockedRead;
            Check(!overlay.IsVisible, "A stale background measurement cannot reshow the user-hidden overlay");
            readStarted = null;
            overlay.SetOverlayVisible(true);
            await SettleLayout(); CompleteMotion();
            fixtureFullscreen = true;
            overlay.SetOverlayVisible(true);
            Check(!overlay.IsVisible, "Injected fullscreen state suppresses the real overlay independently of available space");
            fixtureFullscreen = false;
            overlay.SetOverlayVisible(true);
            await SettleLayout(); CompleteMotion();
            Check(overlay.IsVisible, "Ending injected fullscreen restores the real overlay after safe geometry is confirmed");
            spaceMode = 0; await SettleLayout(); CompleteMotion();
            if(outDir is not null) File.WriteAllText(Path.Combine(outDir,"shell-check.txt"),$"Windows: {Environment.OSVersion}\nExtended styles: 0x{style:X}\nForeground unchanged on show: {unchanged}\nShow/hide smoke completed.\n\nNot tested: physical multi-monitor/DPI moves, QQ playback, fullscreen recovery, Explorer restart, tray overflow outside-click.\n"+OverlayLayoutDiagnostics.Get().ToDisplayText());
        }
        finally { measurementGate.Set(); overlay.Close(); }
    }
    private static void TestSidebarInkCenters(CheckBox sidebar, FrameworkElement label, FrameworkElement track, FrameworkElement icon)
    {
        // Render each actual visual separately on transparency, at its position in the same
        // sidebar coordinate system. This measures glyph ink, not the font's line box.
        var samples = new List<(double Scale, double TextDelta, double IconDelta)>();
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            var textCenter = InkCenter(label, sidebar, scale);
            var trackCenter = InkCenter(track, sidebar, scale);
            var iconCenter = InkCenter(icon, sidebar, scale);
            var correction = label.RenderTransform;
            double uncorrectedCenter;
            try
            {
                label.SetCurrentValue(UIElement.RenderTransformProperty, Transform.Identity);
                label.InvalidateArrange(); sidebar.UpdateLayout();
                uncorrectedCenter = InkCenter(label, sidebar, scale);
            }
            finally { label.SetCurrentValue(UIElement.RenderTransformProperty, correction); label.InvalidateArrange(); sidebar.UpdateLayout(); }
            samples.Add((scale, textCenter - trackCenter, iconCenter - trackCenter));
            Console.WriteLine($"Sidebar ink: theme={ThemeManager.CurrentPreference}, highContrast={ThemeManager.IsHighContrast}, density={scale * 100:0}%, textY={textCenter:F3}, trackY={trackCenter:F3}, iconY={iconCenter:F3}, uncorrected text-track={uncorrectedCenter-trackCenter:+0.000;-0.000;0.000} DIP, corrected text-track={textCenter-trackCenter:+0.000;-0.000;0.000} DIP, icon-track={iconCenter-trackCenter:+0.000;-0.000;0.000} DIP");
        }
        // Emit every measurement before asserting. Half a raster pixel accounts for
        // coverage rounding; the remaining 0.25 DIP is the optical alignment budget.
        foreach (var sample in samples)
        {
            var tolerance = .25 + .5 / sample.Scale;
            Check(Math.Abs(sample.TextDelta) <= tolerance && Math.Abs(sample.TextDelta - sample.IconDelta) <= tolerance,
                $"Sidebar glyph ink must align with track and icon at {sample.Scale * 100:0}%: text-track={sample.TextDelta:F3} DIP, text-icon={sample.TextDelta-sample.IconDelta:F3} DIP, tolerance={tolerance:F3} DIP");
        }
    }
    private static double InkCenter(FrameworkElement element, FrameworkElement ancestor, double scale)
    {
        var bitmap = RenderElementRaster(element, ancestor, scale);
        var width = bitmap.PixelWidth; var height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var top = height; var bottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] >= 16)
                { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        Check(bottom >= top, $"Sidebar {element.Name} has visible raster ink at {scale * 100:0}%");
        return (top + bottom + 1) / (2 * scale);
    }
    private static RenderTargetBitmap RenderElementRaster(FrameworkElement element, FrameworkElement ancestor, double scale)
    {
        var origin = ReferenceEquals(element, ancestor) ? new Point() : element.TransformToAncestor(ancestor).Transform(new Point());
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var brush = new VisualBrush(element)
            {
                AutoLayoutContent = false,
                ViewboxUnits = BrushMappingMode.Absolute,
                // VisualBrush includes the source visual's offset and render transform.
                // Crop that parent-space box, then place it once in ancestor coordinates.
                Viewbox = element.TransformToAncestor((Visual)VisualTreeHelper.GetParent(element))
                    .TransformBounds(new Rect(new Size(element.ActualWidth, element.ActualHeight))),
                Stretch = Stretch.Fill
            };
            drawing.DrawRectangle(brush, null, new Rect(origin, new Size(element.ActualWidth, element.ActualHeight)));
        }
        var width = (int)Math.Ceiling(ancestor.ActualWidth * scale);
        var height = (int)Math.Ceiling(ancestor.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    { for(var i=0;i<VisualTreeHelper.GetChildrenCount(d);i++) { var child=VisualTreeHelper.GetChild(d,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; } }
    private static void Capture(Window w,string file,double scale)
    {
        w.UpdateLayout(); CaptureElement((FrameworkElement)w.Content,file,scale);
    }
    private static void CaptureElement(FrameworkElement root,string file,double scale)
    {
        root.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling((root.ActualWidth+root.Margin.Left+root.Margin.Right)*scale),(int)Math.Ceiling((root.ActualHeight+root.Margin.Top+root.Margin.Bottom)*scale),96*scale,96*scale,PixelFormats.Pbgra32);
        bitmap.Render(root); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output=File.Create(file); png.Save(output);
    }
    private sealed class BindingListener : TraceListener { public override void Write(string? m) { } public override void WriteLine(string? m) { if(m is not null) BindingErrors.Add(m); } }
}
internal sealed partial class FakeSettings : ISettingsBackend
{
    public OverlayLayoutSnapshot Layout { get; set; } = OverlayLayoutSnapshot.Empty;
    public OverlayUiState State = Program.Fixture();
    public event Action? StateChanged;
    public bool FailOffset;
    public Func<long>? GlobalOffset;
    public int Operations;
    public TaskCompletionSource? PendingOperation;
    public bool StartupEnabled { get; private set; }
    public string DiagnosticDetails => "隔离验收数据 · 不访问真实任务栏或用户缓存";
    public OverlayUiState ReadState() => GlobalOffset is null ? State : State with { GlobalOffsetMs=GlobalOffset(), EffectiveOffsetMs=GlobalOffset()+State.TrackOffsetMs };
    public bool SetTrackOffset(long value,long generation,string? key)
    {
        if(FailOffset || !OffsetAdjustment.Matches(generation,key,State.Generation,State.CacheKey)) return false;
        State=State with { TrackOffsetMs=value,EffectiveOffsetMs=State.GlobalOffsetMs+value }; StateChanged?.Invoke(); return true;
    }
    public Task ExecuteAsync(SettingsOperation op,CancellationToken token,long? expectedGeneration = null) { Operations++; return PendingOperation?.Task ?? Task.CompletedTask; }
    public Task<CacheStatistics> StatisticsAsync(CancellationToken token) => Task.FromResult(new CacheStatistics { LyricsCacheEntryCount=128,LyricsCacheTotalBytes=48234496,ManualMatchCount=6,TrackSettingsCount=12,CacheRoot="[isolated fixture]/Cache" });
    public void SetStartup(bool value) => StartupEnabled=value;
    public void OpenFolder(string kind) { }
}
internal sealed class FakeRematch : IRematchBackend
{
    public event Action? StateChanged;
    public (TrackIdentity? Track,long Generation) Current { get;private set; }=(Program.Fixture().Track,7);
    public List<(int Page,TaskCompletionSource<(IReadOnlyList<QQSongCandidate> Items,bool HasMore,string? Error)> Source)> Requests = new();
    public int ApplyCount;
    public TaskCompletionSource<bool> ApplyCompletion = new();
    public Task<(IReadOnlyList<QQSongCandidate> Items,bool HasMore,string? Error)> SearchAsync(string query,int page,TrackIdentity track,CancellationToken token)
    { var t=new TaskCompletionSource<(IReadOnlyList<QQSongCandidate>,bool,string?)>(); Requests.Add((page,t)); return t.Task; }
    public Task<bool> ApplyAsync(QQSongCandidate candidate,long generation) { ApplyCount++; return ApplyCompletion.Task; }
    public void SwitchTrack() { Current=(new TrackIdentity("下一首","新歌手",TimeSpan.FromSeconds(200)),Current.Generation+1);StateChanged?.Invoke(); }
}

internal sealed class SilentLogger : ICacheLogger
{
 public void Info(string message) { }
 public void Warn(string message) { }
 public void Error(string message) { }
 public void Error(string stage,Exception ex) { }
}
