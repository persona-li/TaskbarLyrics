using System.IO;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Controls;

internal static class AlignmentChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var alignment in new[] { "Left", "Center", "Right" })
        {
            var expected = alignment switch { "Left" => 0d, "Right" => 120d, _ => 60d };
            check(KaraokeTextPosition.ComputeOriginX(200, 80, alignment, 25) == expected,
                $"Short lyric {alignment} ignores stale pan position");
            check(KaraokeTextPosition.ComputeOriginX(200, 200, alignment, 25) == 0,
                $"Exact-width lyric {alignment} starts at zero");
            check(KaraokeTextPosition.ComputeOriginX(200, 199.5, alignment, 25) == expected / 240,
                $"Fractional short lyric {alignment} preserves layout precision");
            check(KaraokeTextPosition.ComputeOriginX(200, 200.5, alignment, 0) == 0,
                $"Overflow lyric {alignment} starts at viewport origin without pan");
            check(KaraokeTextPosition.ComputeOriginX(200, 320, alignment, 25) == -25,
                $"Overflow lyric {alignment} preserves existing pan position");
        }

        var root = Path.Combine(Path.GetTempPath(), "TaskbarLyrics-alignment-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = CreateConfig(root);
            foreach (var (input, expected) in new (string?, string)[]
            {
                ("Left", "Left"), ("left", "Left"), ("CENTER", "Center"),
                ("Right", "Right"), ("rIgHt", "Right"), ("invalid", "Center"),
                ("", "Center"), (null, "Center")
            })
            {
                service.Update(c => c.Display.TextAlignment = input!, scheduleSave: false);
                check(service.Current.Display.TextAlignment == expected,
                    $"Alignment normalization: {input ?? "null"} -> {expected}");
                check(service.Current.Display.AlignCenter == (expected == "Center"),
                    "Legacy derived center flag agrees with normalized alignment");
                service.SaveNow();
                using var reloaded = CreateConfig(root);
                check(reloaded.Current.Display.TextAlignment == expected,
                    $"Alignment survives configuration roundtrip: {expected}");
            }
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("TaskbarLyrics-alignment-smoke-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe alignment smoke cleanup path");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }

    private static ConfigService CreateConfig(string root) =>
        new(_ => { }, _ => { }, (_, _) => { }, root, watchFile: false, fontExists: _ => true);
}
