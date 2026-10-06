using System.Windows;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Presentation;

internal static class AttentionPulseChecks
{
    public static void Run(Action<bool, string> check)
    {
        var missing = Program.Fixture() with { HasLyrics = false, NeedsRematch = true, DisplayText = "" };
        var state = PlaybackPresentation.From(missing);
        check(state.NeedsAttention && !state.HasAttentionHint && state.LyricText == "", "Missing match uses a button cue without the choose-version instruction");
        foreach (var raw in new[] { missing with { LyricsLoading = true }, Program.Fixture(), missing with { SessionConnected = false }, missing with { Track = null } })
            check(!PlaybackPresentation.From(raw).NeedsAttention, "Loading, success and disconnected track stop the attention cue");
        var error = PlaybackPresentation.From(missing with { NeedsRematch = false, Error = "offline" });
        check(error.NeedsAttention && error.HasAttentionHint && error.StatusHint.Length > 0, "A real loading error keeps its explanation");
        var noLyrics = PlaybackPresentation.From(missing with { NeedsRematch = false, NoTimedLyrics = true });
        check(noLyrics.NeedsAttention && noLyrics.HasAttentionHint, "No-lyrics result retains its explanation and button cue");
        check(AttentionPulse.ShouldPulse(true, true, false, true, false), "Attention cue animates when motion is allowed");
        check(!AttentionPulse.ShouldPulse(true, true, true, true, false) && !AttentionPulse.ShouldPulse(true, true, false, false, false)
            && !AttentionPulse.ShouldPulse(true, true, false, true, true) && !AttentionPulse.ShouldPulse(false, true, false, true, false),
            "Reduced motion, system animations, high contrast and inactive state suppress breathing");
        var animation = AttentionPulse.CreateAnimation();
        check(animation.From == .3 && animation.To == .85 && animation.AutoReverse && animation.Duration.TimeSpan.TotalMilliseconds == 1400,
            "Attention animation stays within a gentle background opacity range");
        var layer = new AttentionPulse { IsActive = true, ReduceMotion = true };
        var host = new Window { Content = layer, Width = 120, Height = 80, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            host.Show(); host.UpdateLayout();
            check(layer.Opacity == .7 && !layer.IsPulsing && !layer.IsHitTestVisible, "Reduced-motion cue is static and cannot intercept button input");
            layer.IsActive = false;
            check(layer.Opacity == 0 && !layer.HasAnimatedProperties, "Clearing attention removes its animation and visible cue immediately");
            layer.IsActive = true;
            host.Hide();
            check(layer.Opacity == 0 && !layer.IsPulsing, "Hiding the page stops attention rendering");
        }
        finally { host.Close(); }
    }
}
