namespace TaskbarLyrics.Windows;

public interface IWindowsLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string stage, Exception ex);
}
