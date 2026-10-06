using TaskbarLyrics.QQMusic.Matching;

namespace TaskbarLyrics.QQMusic.Services;

public interface IAutomaticSongSearch
{
    Task<AutoMatchDecision> AutoMatchAsync(
        string title,
        string? artist,
        int? durationSeconds,
        string? album = null,
        CancellationToken cancellationToken = default);
}
