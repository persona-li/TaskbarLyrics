using TaskbarLyrics.QQMusic.Models;

namespace TaskbarLyrics.QQMusic.Logging;

/// <summary>
/// Logger surface used by QQMusic services (from QQMusicLyricsProbe SimpleLogger).
/// </summary>
public interface IQqMusicLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string stage, Exception ex);
    void Http(string stage, HttpDiagnostics d);
}
