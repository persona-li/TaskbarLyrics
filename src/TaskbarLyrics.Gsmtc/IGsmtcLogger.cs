namespace TaskbarLyrics.Gsmtc;

public interface IGsmtcLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string stage, Exception ex);
}
