namespace TaskbarLyrics.Cache;

public interface ICacheLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string stage, Exception ex);
}
