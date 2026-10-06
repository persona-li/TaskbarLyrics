using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.App.Services;

internal static class LayoutSettingsChecks
{
    public static void Run(Action<bool, string> check, string root)
    {
        using var config = new ConfigService(_ => { }, _ => { }, (_, _) => { },
            System.IO.Path.Combine(root, "layout-settings"), watchFile: false, fontExists: _ => true);
        var saved = new OverlayConfig { MinWidthPx = 750, MaxWidthPx = 1350 };
        var fullHd = TaskbarRegionLayout.Resolve(saved, 35, 1, 1920);
        var uhd = TaskbarRegionLayout.Resolve(saved, 35, 2, 3840);
        check(fullHd.MinWidthPx == 245 && uhd.MinWidthPx == 490, "Automatic minimum follows seven glyph widths and DPI");
        check(uhd.LeftMarginPx == fullHd.LeftMarginPx * 2 && uhd.MaxWidthPx == fullHd.MaxWidthPx * 2,
            "Equivalent logical screens use proportional physical margins and widths");
        check(TaskbarRegionLayout.Select(new(8,521), new(1367,1534), fullHd, TaskbarLyricSide.Left,24).Visible,
            "Recorded 1080p free space remains usable with the saved 750px manual minimum");
        check(!TaskbarRegionLayout.Select(new(8,80), new(1367,1400), fullHd, TaskbarLyricSide.Left,24).Visible,
            "Automatic layout still hides when neither side fits");
        check(saved.MinWidthPx == 750 && saved.MaxWidthPx == 1350, "Automatic calculations preserve manual values");
        saved.AutoFit = false;
        check(ReferenceEquals(saved, TaskbarRegionLayout.Resolve(saved,35,2,3840)), "Manual layout uses saved parameters unchanged");
        var backend = new FakeSettings();
        using var vm = new SettingsViewModel(config, backend);
        check(vm.AutoFit && !vm.ShowManualLayout, "Automatic fit is the default and hides manual layout controls");
        vm.AutoFit = false;
        check(vm.ShowManualLayout, "Disabling automatic fit reveals manual parameters");
        vm.AutoFit = true;
        check(!vm.ShowFallbackWidth, "Unknown measurement does not display inactive fallback controls");
        var notifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.ShowFallbackWidth)) notifications++; };
        foreach (int? free in new int?[] { 800, null, 79, 80, 600 })
        {
            backend.Layout = OverlayLayoutSnapshot.Empty with { TaskbarWidthPx = 1920, FreeBandPx = free };
            vm.Refresh();
            check(vm.ShowFallbackWidth == (free is null),
                "Fallback visibility follows actual width algorithm distinguishing measured narrow/empty space from unknown measurements");
        }
        check(notifications == 5, "Measurement updates notify the open settings page");
        vm.FallbackWidthPercent = 31;
        check(Math.Abs(config.Current.Overlay.WidthRatio - .31) < .0001
            && Math.Abs(vm.FallbackWidthPercent - 31) < .0001,
            "Percentage editor preserves the stored ratio and round trip");
        config.SaveNow();
    }
}
