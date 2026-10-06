namespace TaskbarLyrics.Windows;

/// <summary>
/// Static log sink set by App; Windows helpers call this without constructor injection.
/// </summary>
public static class WindowsLog
{
    private static IWindowsLogger? _logger;

    public static void SetLogger(IWindowsLogger logger) => _logger = logger;

    public static void Info(string message) => _logger?.Info(message);
    public static void Warn(string message) => _logger?.Warn(message);
    public static void Error(string message) => _logger?.Error(message);
    public static void Error(string stage, Exception ex) => _logger?.Error(stage, ex);
}
