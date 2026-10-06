using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.App.Controls;
using TaskbarLyrics.Windows;
internal static class TaskbarRegionChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var side in new[] { TaskbarLyricSide.Left, TaskbarLyricSide.Right })
        {
            var exit = OverlayPlacementMotion.SideFrame(side, false, .5);
            var enter = OverlayPlacementMotion.SideFrame(side, true, .5);
            check(side == TaskbarLyricSide.Right ? exit.Offset < 0 && enter.Offset > 0 : exit.Offset > 0 && enter.Offset < 0,
                "Side changes exit the old outer edge and enter the opposite outer edge");
            check(OverlayPlacementMotion.SideFrame(side, true, 1) == (0d, 1d), "Side entry settles exactly without residual transform or opacity");
            check(OverlayPlacementMotion.SideFrame(side, false, 1).Opacity == 0, "Window relocates only after the exit is invisible");
        }
        var stable = new TaskbarLayoutStability();
        var bar = new NativeMethods.RECT { Left = 0, Top = 2064, Right = 3840, Bottom = 2160 };
        TaskbarFreeRegionLocator.FreeRegionResult Sample(int start, int leftWidth, int rightWidth) =>
            new(true, start, new(8, 8 + leftWidth), new(3172 - rightWidth, 3172), "recorded");
        // Replay actual 01:14:31 geometry: right -> left -> right -> insufficient -> right.
        var burst = new[] { (0L, Sample(1163,510,1400)), (59L, Sample(1069,862,900)),
            (116L, Sample(323,275,844)), (153L, Sample(170,122,669)), (307L, Sample(0,0,1638)) };
        foreach (var (time, sample) in burst)
            check(!stable.Observe(bar, sample, time), "Moving shell frames never commit a new side or temporary no-space state");
        check(!stable.Observe(bar, burst[^1].Item2, 400), "Final geometry waits for confirmation");
        check(stable.Observe(bar, burst[^1].Item2, 447), "Final geometry commits at the stability boundary");
        // The side alone being stable is insufficient: width 932 is still an intermediate frame.
        stable.Reset();
        check(!stable.Observe(bar, Sample(980,932,600), 0), "Intermediate width is pending");
        check(!stable.Observe(bar, Sample(1167,1119,450), 70), "Width change restarts confirmation");
        check(!stable.Observe(bar, Sample(1167,1119,450), 140), "Window cannot enter using the earlier 932px width");
        check(stable.Observe(bar, Sample(1167,1119,450), 210), "Window enters once with the final width");
        stable.Reset();
        var unavailable = new TaskbarFreeRegionLocator.FreeRegionResult(false, 0, default, default, "temporary UIA failure");
        check(!stable.Observe(bar, unavailable, 0) && !stable.Observe(bar, unavailable, 140)
            && !stable.Observe(bar, unavailable, 399), "Transient UIA failures cannot launch manual-width fallback");
        check(stable.Observe(bar, unavailable, 400), "Persistently unavailable measurement still permits manual fallback");
        stable.Reset();
        var empty = Sample(0,0,0);
        check(!stable.Observe(bar, empty, 0) && !stable.Observe(bar, empty, 70) && stable.Observe(bar, empty, 140),
            "Stable no-space is confirmed and remains distinct from unavailable measurement");
        var clipped = OverlayPlacementMotion.VisibleBand(new(8,2064,1119,96), new(8,600));
        check(clipped == new TaskbarInterval(8,600), "Safety clips shrink without resizing the text container");
        var config = new OverlayConfig { MinWidthPx = 200, MaxWidthPx = 400 };
        var left = new TaskbarInterval(8, 608); var right = new TaskbarInterval(1000, 1500);
        var choice = TaskbarRegionLayout.Select(left, right, config, TaskbarLyricSide.Unknown, 40);
        check(choice.Side == TaskbarLyricSide.Left && choice.Left == 8 && choice.Width == 400, "Centered taskbar prefers fitting left space");
        choice = TaskbarRegionLayout.Select(new(8, 50), right, config, TaskbarLyricSide.Left, 40);
        check(choice.Side == TaskbarLyricSide.Right && choice.Left == 1100 && choice.Width == 400, "Left-aligned or crowded Start moves lyrics to the right band, anchored to its right edge");
        choice = TaskbarRegionLayout.Select(new(8, 228), right, config, TaskbarLyricSide.Right, 40);
        check(choice.Side == TaskbarLyricSide.Right, "Hysteresis keeps the right band until left has enough spare width");
        choice = TaskbarRegionLayout.Select(new(8, 248), right, config, TaskbarLyricSide.Right, 40);
        check(choice.Side == TaskbarLyricSide.Left, "A sufficiently recovered left band regains priority");
        choice = TaskbarRegionLayout.Select(new(8, 120), new(1000, 1100), config, TaskbarLyricSide.Left, 40);
        check(!choice.Visible && choice.Side == TaskbarLyricSide.Hidden && choice.Width == 0, "Measured insufficient space never uses ratio fallback or overlaps buttons");
        choice = TaskbarRegionLayout.Select(left, right, config, TaskbarLyricSide.Right, 40);
        check(choice.Visible, "Fresh measurements recover from space suppression");
        var gap = TaskbarRegionLayout.LargestGap(-1920, -800, new[] { new TaskbarInterval(-1920,-1800), new TaskbarInterval(-1700,-1600) }, 8, 40);
        check(gap == new TaskbarInterval(-1592, -840), "Left band excludes widgets and other controls on negative-coordinate monitors");
        check(TaskbarRegionLayout.LargestGap(0, 50, Array.Empty<TaskbarInterval>(), 8, 60).Width == 0, "Margins cannot create negative width");
        var renderer = new KaraokeTextControl(); renderer.ApplyConfig(AppConfig.CreateDefault()); renderer.PlacementAlignment = "Right";
        check(renderer.PlacementAlignment == "Right", "Placement can right-align actual lyrics without mutating saved appearance");
        renderer.PlacementAlignment = null;
        check(renderer.PlacementAlignment is null, "Returning left restores the saved alignment");
        check(!(OverlayLayoutSnapshot.Empty with { TaskbarWidthPx = 1920, FreeBandPx = 0 }).UsesFallbackWidth,
            "Measured zero space does not expose manual width controls");
    }
}
