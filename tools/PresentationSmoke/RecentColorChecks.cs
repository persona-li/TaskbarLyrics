using System.IO;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;

internal static class RecentColorChecks
{
    public static void Run(Action<bool, string> check, string tempRoot)
    {
        check(RecentColorHistory.Canonicalize(" #496dbf ") == "#FF496DBF",
            "Recent colors normalize RGB to uppercase opaque ARGB");
        check(RecentColorHistory.Canonicalize("#80496dbf") == "#80496DBF",
            "Recent colors preserve alpha");
        check(RecentColorHistory.Canonicalize("#00496dbf") == "#00496DBF",
            "Recent colors preserve fully transparent colors");
        check(RecentColorHistory.Normalize([null, "", "#abc", "#GG496DBF", "red", "#FFFFFFFFF"]).Count == 0,
            "Recent colors discard invalid values without adding defaults");
        check(RecentColorHistory.Normalize(null).Count == 0,
            "Missing recent colors migrate to an empty history");
        var colors = RecentColorHistory.Normalize(["#496dbf", "#FF496DBF", "#80496DBF", "#ffffff"]);
        check(colors.SequenceEqual(["#FF496DBF", "#80496DBF", "#FFFFFFFF"]),
            "Recent colors deduplicate equivalent RGB/ARGB while keeping distinct alpha");
        colors = RecentColorHistory.Remember(colors, "#ffffff");
        check(colors.SequenceEqual(["#FFFFFFFF", "#FF496DBF", "#80496DBF"]),
            "Reusing a recent color moves it to the front");
        check(RecentColorHistory.Remember(colors, "invalid").SequenceEqual(colors),
            "Invalid commit does not displace valid history");
        colors = [];
        for (var i = 0; i < 8; i++) colors = RecentColorHistory.Remember(colors, $"#FF0000{i:X2}");
        check(colors.Count == 6 && colors[0] == "#FF000007" && colors[^1] == "#FF000002",
            "Recent history retains only the six latest distinct colors");

        var root = Path.Combine(tempRoot, "recent-colors");
        using (var config = Config(root))
        using (var vm = new SettingsViewModel(config, new FakeSettings()))
        {
            var notified = false;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.RecentNormalColors)) notified = true; };
            vm.NormalColor = "#FF123456";
            vm.HighlightColor = "#FF234567";
            check(vm.RecentNormalColors.Count == 0, "Live color preview does not flood recent history");
            vm.RememberNormalColorCommand.Execute("#123456");
            vm.RememberNormalColorCommand.Execute("#80234567");
            vm.RememberNormalColorCommand.Execute("#FF123456");
            check(notified && vm.RecentNormalColors.SequenceEqual(["#FF123456", "#80234567"]),
                "Explicit color commits update the correct notified history");
            var revision = config.SaveState.Revision;
            vm.RememberNormalColorCommand.Execute("#123456");
            vm.RememberNormalColorCommand.Execute("invalid");
            check(config.SaveState.Revision == revision,
                "Repeated current color and invalid commits do not trigger redundant saves");
            vm.RememberHighlightColorCommand.Execute("#FFAABBCC");
            vm.RememberShadowColorCommand.Execute("#80112233");
            var persisted = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config.ConfigPath));
            using (persisted)
            {
                var general = persisted.RootElement.GetProperty("general");
                check(general.GetProperty("recentNormalColors")[0].GetString() == "#FF123456"
                    && general.GetProperty("recentHighlightColors")[0].GetString() == "#FFAABBCC"
                    && general.GetProperty("recentShadowColors")[0].GetString() == "#80112233",
                    "Every committed color is already on disk before debounce, disposal or graceful shutdown");
            }
            check(vm.RecentNormalColors.SequenceEqual(["#FF123456", "#80234567"])
                && vm.RecentHighlightColors.SequenceEqual(["#FFAABBCC"])
                && vm.RecentShadowColors.SequenceEqual(["#80112233"]),
                "Normal, highlight and shadow commits cannot leak across histories");
            config.ResetColorsToDefaults();
            config.ResetAppearanceToDefaults();
            check(vm.RecentNormalColors.SequenceEqual(["#FF123456", "#80234567"]),
                "Resetting colors or appearance retains recent color history");
            config.SaveNow();
        }
        using (var reloaded = Config(root))
        {
            check(reloaded.Current.General.RecentNormalColors.SequenceEqual(["#FF123456", "#80234567"]),
                "Recent colors survive config save and restart in an isolated directory");
            check(reloaded.Current.General.RecentHighlightColors.SequenceEqual(["#FFAABBCC"])
                && reloaded.Current.General.RecentShadowColors.SequenceEqual(["#80112233"]),
                "Independent histories survive resets, save and restart including alpha");
        }

        var dirtyRoot = Path.Combine(tempRoot, "recent-colors-migration");
        Directory.CreateDirectory(dirtyRoot);
        File.WriteAllText(Path.Combine(dirtyRoot, "config.json"),
            """{"general":{"recentColors":["#123456","#FF123456",null,"bad","#80123456","#000001","#000002","#000003","#000004","#000005"]}}""");
        using (var migrated = Config(dirtyRoot))
        {
            check(migrated.Current.General.RecentColors.SequenceEqual(
                ["#FF123456", "#80123456", "#FF000001", "#FF000002", "#FF000003", "#FF000004"]),
                "Config load repairs invalid, duplicate and over-capacity history");
            check(migrated.Current.General.RecentNormalColors.Count == 0
                && migrated.Current.General.RecentHighlightColors.Count == 0
                && migrated.Current.General.RecentShadowColors.Count == 0,
                "Legacy mixed history is preserved without assigning unknown origins to editors");
        }
    }

    private static ConfigService Config(string root) => new(_ => { }, _ => { }, (_, _) => { },
        root, watchFile: false, fontExists: _ => true);
}
