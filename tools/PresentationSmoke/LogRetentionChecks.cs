using System.IO;
using TaskbarLyrics.App.Services;

internal static class LogRetentionChecks
{
    public static void Run(Action<bool, string> check, string root)
    {
        var directory = Path.Combine(root, "log-retention");
        Directory.CreateDirectory(directory);
        var now = new DateTimeOffset(2026, 10, 7, 23, 59, 0, TimeSpan.FromHours(8));
        var expired = Path.Combine(directory, "taskbar-lyrics-20260930.log");
        var retained = Path.Combine(directory, "taskbar-lyrics-20261001.log");
        var unrelated = Path.Combine(directory, "notes.log");
        File.WriteAllText(expired, "old");
        File.WriteAllText(retained, "keep");
        File.WriteAllText(unrelated, "untouched");
        using (var logger = new AppLogger(directory, () => now))
        {
            check(!File.Exists(expired), "Logs older than seven calendar days removed");
            check(File.Exists(retained), "Seventh calendar day retained");
            var first = logger.LogFilePath;
            now = now.AddMinutes(2);
            logger.Info("after midnight");
            check(logger.LogFilePath != first, "Long-running logger rotates at midnight");
            check(!File.Exists(retained), "Midnight rotation prunes expired files");
            for (var i = 0; i < 1600; i++) logger.Info(new string('中', 8000));
            var logs = new DirectoryInfo(directory).GetFiles("taskbar-lyrics-*.log");
            check(logs.Sum(f => f.Length) <= AppLogger.MaximumTotalBytes, "Continuous logging stays within 20 MiB");
            check(logs.All(f => f.Length <= 2 * 1024 * 1024), "Segments stay within 2 MiB");
            logger.Info(new string('中', 100000));
            using var reader = new StreamReader(new FileStream(logger.LogFilePath,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            check(reader.ReadToEnd().Contains("[truncated]"), "Oversized entries bounded");
        }
        check(File.ReadAllText(unrelated) == "untouched", "Other files preserved");
        var oversized = Path.Combine(directory, "taskbar-lyrics-20261008.log");
        using (var file = File.Create(oversized)) file.SetLength(25 * 1024 * 1024);
        using (var logger = new AppLogger(directory, () => now))
            check(new DirectoryInfo(directory).GetFiles("taskbar-lyrics-*.log").Sum(f => f.Length)
                <= AppLogger.MaximumTotalBytes, "Oversized legacy logs cleaned on startup");
    }
}
