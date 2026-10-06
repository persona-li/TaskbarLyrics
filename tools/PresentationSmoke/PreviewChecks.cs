using System.Windows;
using System.Windows.Controls;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Presentation;

internal static class PreviewChecks
{
    public static void Run(Action<bool, string> check)
    {
        var playing = PlaybackPresentation.From(Program.Fixture());
        var empty = PlaybackPresentation.From(Program.Fixture() with { Track = null, SessionConnected = false });
        var current = empty;
        var config = AppConfig.CreateDefault();
        config.General.ReduceMotion = true;
        var preview = new LyricsPreview { Config = config, StateReader = () => current };
        var source = (ComboBox)preview.FindName("SourceChoice");
        var hint = (TextBlock)preview.FindName("PreviewHint");
        // Use the same pause transition as the real button, independently of Windows'
        // animation preference so this regression cannot silently skip the pause path.
        preview.SetPaused(true);
        check(preview.IsPaused, "Preview can enter a frozen state independently of system motion settings");
        current = playing;
        source.SelectedIndex = 1;
        check(preview.IsPaused && hint.Visibility == Visibility.Collapsed, "Switching a paused demo to the current song captures the new song");
        current = empty;
        preview.RenderFrame(2000);
        check(hint.Visibility == Visibility.Collapsed, "Paused current-song preview keeps its selected snapshot");
        source.SelectedIndex = 0;
        source.SelectedIndex = 1;
        check(preview.IsPaused && hint.Visibility == Visibility.Visible, "Reselecting current song captures disconnection instead of replaying an old song");
        current = playing;
        preview.SetPaused(false);
        check(!preview.IsPaused && hint.Visibility == Visibility.Collapsed, "Resuming preview refreshes the current song immediately");
        check(!preview.IsPreviewRunning, "Hidden reduced-motion preview leaves no running timer");
        preview.SetPaused(true);
        var karaoke=(KaraokeTextControl)preview.FindName("Karaoke");
        var transform=karaoke.RenderTransform;
        var normal=config.Display.NormalColor;
        foreach(var light in new[]{true,false,true})
        {
            ((System.Windows.Controls.RadioButton)preview.FindName(light ? "LightBackgroundButton" : "DarkBackgroundButton")).IsChecked=true;
            var surface=(Border)preview.FindName("TaskbarSurface");
            var expected=(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#F0F0F2" : "#242426");
            check(((System.Windows.Media.SolidColorBrush)surface.Background).Color==expected,
                "Reduced-motion background changes directly to the requested color");
            check(((System.Windows.Media.TranslateTransform)preview.FindName("BackgroundIndicatorTranslate")).X==(light ? 0 : 48),
                "Preview background indicator reaches the selected segment");
            check(preview.IsPaused && ReferenceEquals(karaoke.RenderTransform,transform) && config.Display.NormalColor==normal,
                "Background switch preserves lyric position, pause state and configured palette");
        }
    }
}
