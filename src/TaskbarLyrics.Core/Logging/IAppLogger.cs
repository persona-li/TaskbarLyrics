namespace TaskbarLyrics.Core.Logging;

/// <summary>
/// Minimal logger abstraction so Core has no file/console dependency.
/// </summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string stage, Exception ex);
}
