using System.IO;
using Microsoft.Win32;

namespace TaskbarLyrics.App.Services;

/// <summary>
/// User-level HKCU Run startup entry (no admin required).
/// </summary>
public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarLyrics";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var v = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(v);
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null)
            {
                throw new InvalidOperationException("Cannot open HKCU Run key");
            }

            if (enabled)
            {
                var path = Environment.ProcessPath
                           ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                           ?? throw new InvalidOperationException("Process path unavailable");
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or System.Security.SecurityException
                or IOException
                or InvalidOperationException)
        {
            throw new InvalidOperationException("无法修改开机启动项: " + ex.Message, ex);
        }
    }
}
