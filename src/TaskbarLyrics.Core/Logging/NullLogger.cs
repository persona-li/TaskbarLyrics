namespace TaskbarLyrics.Core.Logging;

public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Error(string stage, Exception ex) { }
}
