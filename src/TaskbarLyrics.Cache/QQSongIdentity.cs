namespace TaskbarLyrics.Cache;

/// <summary>
/// Stable QQ Music identity used as lyrics cache key.
/// Prefer SongMid, fallback SongId.
/// </summary>
public sealed record QQSongIdentity(
    string SongId,
    string SongMid,
    string Title,
    string Artists,
    string Album,
    int DurationSeconds)
{
    public string CacheKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SongMid))
            {
                return "qq-" + SongMid.Trim();
            }

            if (!string.IsNullOrWhiteSpace(SongId))
            {
                return "qq-id-" + SongId.Trim();
            }

            throw new InvalidOperationException("SongMid and SongId are both empty; cannot build cache key.");
        }
    }

    public string PreferredId =>
        !string.IsNullOrWhiteSpace(SongMid) ? SongMid : SongId;
}
