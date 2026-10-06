using System.IO;
using TaskbarLyrics.Cache;
using TaskbarLyrics.App.Services;

internal static class DistributionChecks
{
    public static void Run(Action<bool, string> check, string root)
    {
        var app = Path.Combine(root, "portable-app");
        var local = Path.Combine(root, "user-data");
        Directory.CreateDirectory(app);
        check(CachePaths.ResolveAppRoot(app, local) == Path.Combine(local, "TaskbarLyrics"),
            "Installed copy keeps existing user data location");
        File.WriteAllText(Path.Combine(app, "portable.flag"), "");
        check(CachePaths.ResolveAppRoot(app, local) == Path.Combine(app, "Data"),
            "Portable copy uses its own Data directory");
        check(!Directory.Exists(local), "Path resolution does not migrate or create user data");
        var exe = Path.Combine(app, "TaskbarLyrics.App.exe");
        check(SingleInstanceService.IsExitCommandFor("EXIT:" + exe, exe), "Shutdown targets exact copy");
        check(!SingleInstanceService.IsExitCommandFor("EXIT:" + exe, exe + ".other"), "Other copy cannot be shut down");
        check(!SingleInstanceService.IsExitCommandFor("SHOW_SETTINGS", exe), "Showing settings is not shutdown");
    }
}
