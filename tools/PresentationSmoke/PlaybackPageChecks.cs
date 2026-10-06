using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Pages;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.Core.Models;
using WpfPath = System.Windows.Shapes.Path;

internal static class PlaybackPageChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var label = new NavigationPlaybackLabel();
        var playing = PlaybackPresentation.From(Program.Fixture());
        var paused = PlaybackPresentation.From(playing.Raw with { Status = PlaybackStatus.Paused });
        check(label.Read(paused, 0) == "已暂停", "Ordinary pause appears immediately");
        label.Begin(playing, 100);
        check(label.Read(paused, 101) == "正在播放", "Skip transient pause preserves the playing heading");
        check(label.Read(paused, 100 + NavigationPlaybackLabel.GraceMilliseconds) == "已暂停", "Persistent pause is exposed at the bounded skip deadline");
        label.Begin(playing, 2000);
        var nextSong = PlaybackPresentation.From(playing.Raw with { Track = playing.Raw.Track! with { Title = "New song" } });
        check(label.Read(nextSong, 2100) == "正在播放", "Playing new song finishes the skip guard");
        check(label.Read(PlaybackPresentation.From(nextSong.Raw with { Status = PlaybackStatus.Paused }), 2101) == "正在播放", "An isolated playing sample does not prematurely end navigation stabilization");
        label.Read(nextSong, 2200); label.Read(nextSong, 2400);
        check(label.Read(PlaybackPresentation.From(nextSong.Raw with { Status = PlaybackStatus.Paused }), 2401) == "已暂停", "Pause after settled new playback is immediate");
        label.Begin(playing, 3000); label.Clear();
        check(label.Read(paused, 3001) == "已暂停", "Failed or canceled skip clears suppression");
        label.Begin(paused, 4000);
        check(label.Read(paused, 4001) == "已暂停", "Skipping from a paused song never invents playback");
        // Observed QQ sequence: Stopped at 0 ms, Changing at 47 ms, Playing at 185 ms.
        // The first captured skip began paused; the second began playing.
        foreach (var origin in new[] { paused, playing })
        {
            label.Begin(origin, 5000);
            foreach (var (status, elapsed) in new[] { (PlaybackStatus.Stopped, 0), (PlaybackStatus.Changing, 47) })
            {
                var transient = PlaybackPresentation.From(origin.Raw with { Status = status });
                check(label.Read(transient, 5000 + elapsed) == origin.PlaybackLabel && label.IsPlaying(transient, 5000 + elapsed) == origin.IsPlaying,
                    "Captured QQ stopped/changing sequence preserves both heading and icon from paused or playing origins");
            }
            check(label.Read(nextSong, 5185) == "正在播放" && label.IsPlaying(nextSong, 5185), "Actual new playback updates the stabilized heading and icon together");
            label.Clear();
            check(label.Read(paused, 5186) == "已暂停" && !label.IsPlaying(paused, 5186), "Explicit pause bypasses skip stabilization immediately");
        }
        label.Begin(playing, 6000);
        var stopped = PlaybackPresentation.From(playing.Raw with { Status = PlaybackStatus.Stopped });
        check(label.Read(stopped, 6000 + NavigationPlaybackLabel.GraceMilliseconds) == "已停止", "A persistent stop is exposed when the skip window expires");
        using var config = new ConfigService(_ => { }, _ => { }, (_, _) => { },
            System.IO.Path.Combine(root, "playback-pages"), watchFile: false, fontExists: _ => true);
        var backend = new FakeSettings();
        using var vm = new SettingsViewModel(config, backend);
        var window = new Window
        {
            Width = 800, Height = 700, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false
        };
        Motion.SetReduce(window, true);
        async Task Layout()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        try
        {
            window.Show();
            foreach (var overview in new[] { true, false })
            {
                UserControl page = overview ? new OverviewPage() : new SynchronizationPage();
                page.DataContext = vm;
                window.Content = page;
                await Layout();
                if (overview)
                {
                    var heading = (TextBlock)page.FindName("PageTitle");
                    var song = (FrameworkElement)page.FindName("SongDetails");
                    var lyric = (FrameworkElement)page.FindName("LyricFit");
                    var initial = backend.State;
                    var visualKey = vm.State.TrackVisualKey;
                    foreach (var update in new[] {
                        initial with { Status = PlaybackStatus.Paused },
                        initial with { CacheKey = "resolved-lyrics", Generation = initial.Generation + 1 },
                        initial with { Track = initial.Track! with { Album = "Late album metadata" } },
                        initial with { Track = initial.Track! with { Duration = TimeSpan.FromSeconds(240) } } })
                    {
                        backend.State = update; vm.Refresh(); await Layout();
                        check(heading.Text == vm.State.PlaybackLabel, "Overview heading reflects real pause state outside a navigation transition");
                        check(Equals(Motion.GetChangeKey(song), visualKey) && Equals(Motion.GetChangeKey(lyric), visualKey),
                            "Pause, lyric resolution, reload and duration updates do not restart song visuals");
                    }
                    backend.State = initial with { Track = initial.Track! with { Title = "Next track" } };
                    vm.Refresh(); await Layout();
                    check(!Equals(Motion.GetChangeKey(song), visualKey) && Equals(Motion.GetChangeKey(song), Motion.GetChangeKey(lyric)),
                        "New song identity updates both visual transition keys together");
                    backend.State = initial; vm.Refresh(); await Layout();
                }
                var previous = (Button)page.FindName(overview ? "PreviousTrackButton" : "AuditionPreviousButton");
                var play = (Button)page.FindName(overview ? "PlaybackToggleButton" : "AuditionPlayButton");
                var next = (Button)page.FindName(overview ? "NextTrackButton" : "AuditionNextButton");
                var progress = (PlaybackSeekSlider)page.FindName(overview ? "SongProgress" : "AuditionProgress");
                var transportButtons = new[] { previous, play, next };
                var iconCanvases = transportButtons.Select(IconCanvas).ToArray();
                var iconSizes = iconCanvases.Select(c => c.RenderSize).ToArray();
                var pauseShape = Icon(play).Data.ToString();
                check(transportButtons.All(b => Icon(b).Fill?.ToString() == b.Foreground?.ToString())
                    && iconSizes.All(s => s.Width == 18 && s.Height == 18),
                    "Both pages use equally sized neutral transport icon canvases");
                check(play.Content is true && Icon(play).Data.GetFlattenedPathGeometry().Figures.Count == 2,
                    "Playing state renders the two-bar pause geometry");
                var origins = new[] { previous, play, next }.Select(b => b.TranslatePoint(new Point(), page)).ToArray();
                check(ReferenceEquals(previous.Command, vm.PreviousTrackCommand) && ReferenceEquals(play.Command, vm.TogglePlaybackCommand)
                    && ReferenceEquals(next.Command, vm.NextTrackCommand), "Both playback pages use the guarded transport commands");
                check(previous.IsEnabled && play.IsEnabled && next.IsEnabled && origins[0].X < origins[1].X && origins[1].X < origins[2].X
                    && origins.All(p => Math.Abs(p.Y - origins[0].Y) < .1), "Previous, play and next are adjacent and vertically aligned");
                check(ReferenceEquals(progress.SeekCommand, vm.SeekPlaybackCommand) && progress.DurationSeconds > 0,
                    "Each progress control is bound to the live duration and guarded seek command");
                var prior = backend.State;
                backend.PendingPlaybackControl = new TaskCompletionSource<bool>();
                var skipping = vm.NextTrackAsync();
                backend.State = prior with { Status = PlaybackStatus.Paused };
                vm.Refresh(); await Layout();
                check(play.Content is true && vm.PlaybackButtonLabel == "暂停" && vm.OverviewPlaybackLabel == "正在播放",
                    "Both transport icons and heading suppress the same transient skip pause");
                check(!play.IsEnabled && ((Border)play.Template.FindName("Root", play)).Opacity == 1,
                    "Pending transport request blocks repeated clicks without flashing disabled opacity");
                check(vm.State.Raw.Status == PlaybackStatus.Paused, "Visual stabilization does not overwrite actual player status");
                foreach (var transientStatus in new[] { PlaybackStatus.Stopped, PlaybackStatus.Changing })
                {
                    backend.State = prior with { Status = transientStatus }; vm.Refresh(); await Layout();
                    check(play.Content is true && vm.OverviewPlaybackLabel == "正在播放", "Real button bindings stay stable through recorded Stopped and Changing samples");
                }
                backend.State = prior with { Status = PlaybackStatus.Paused }; vm.Refresh(); await Layout();
                backend.PendingPlaybackControl.SetResult(false);
                await skipping; await Layout();
                check(play.Content is false && vm.PlaybackButtonLabel == "播放", "Rejected skip restores the actual paused icon immediately");
                backend.PendingPlaybackControl = null;
                backend.State = prior; vm.Refresh(); await Layout();
                var before = backend.PlaybackCalls.Count;
                previous.Command.Execute(null);
                await Layout();
                next.Command.Execute(null);
                await Layout();
                play.Command.Execute(null);
                await Layout();
                check(backend.PlaybackCalls.Skip(before).Select(c => c.Action).SequenceEqual(new[]
                    { PlaybackControlAction.Previous, PlaybackControlAction.Next, PlaybackControlAction.Pause })
                    && vm.State.Raw.Status == PlaybackStatus.Paused,
                    "Page command bindings dispatch navigation and pause to the selected session");
                check(play.Content is false && vm.PlaybackButtonLabel == "播放"
                    && Icon(play).Data.ToString() != pauseShape
                    && Icon(play).Data.GetFlattenedPathGeometry().Figures.Count == 1,
                    "Paused state replaces the pause bars with the play triangle");
                check(transportButtons.Select(b => IconCanvas(b).RenderSize).SequenceEqual(iconSizes)
                    && transportButtons.Select(b => b.TranslatePoint(new Point(), page)).SequenceEqual(origins),
                    "Changing play state preserves icon canvas size and button alignment");
                backend.PlaybackCapabilities = new(true, true, false, false, false);
                vm.Refresh();
                await Layout();
                check(((Border)previous.Template.FindName("Root", previous)).Opacity == .4,
                    $"Unsupported transport actions still show disabled opacity outside a request: opacity={((Border)previous.Template.FindName("Root", previous)).Opacity}, enabled={previous.IsEnabled}, busy={vm.IsPlaybackControlBusy}");
                check(!previous.IsEnabled && !next.IsEnabled && play.IsEnabled && !progress.CanSeek,
                    "Unsupported seeking/navigation are unavailable while play remains usable");
                check(new[] { previous, play, next }.Select(b => b.TranslatePoint(new Point(), page)).SequenceEqual(origins),
                    "Player capability changes do not shift the transport buttons");
                backend.State = backend.State with { Clock = backend.State.Clock with { EstimatedPosition = TimeSpan.FromSeconds(31) } };
                vm.Refresh();
                await Layout();
                check(Math.Abs(progress.Value - 31) < .01, "Progress continues to follow playback when seeking is unavailable");
                backend.PlaybackCapabilities = new(true, true, true, true, true);
                backend.State = Program.Fixture();
                vm.Refresh();
                await Layout();
                check(play.Content is true && Icon(play).Data.ToString() == pauseShape
                    && transportButtons.Select(b => IconCanvas(b).RenderSize).SequenceEqual(iconSizes),
                    "Returning to playback restores pause geometry without resizing the canvases");
            }
        }
        finally { window.Close(); }
    }

    private static WpfPath Icon(Button button) => Descendants(button).OfType<WpfPath>().Single(p => p.Name == "TransportIcon");

    private static FrameworkElement IconCanvas(Button button)
    {
        var presenter = Descendants(button).OfType<ContentPresenter>()
            .First(p => ReferenceEquals(p.ContentTemplate, button.ContentTemplate));
        return (FrameworkElement)VisualTreeHelper.GetChild(presenter, 0);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
