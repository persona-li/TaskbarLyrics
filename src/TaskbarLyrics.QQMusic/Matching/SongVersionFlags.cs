namespace TaskbarLyrics.QQMusic.Matching;

[Flags]
public enum SongVersionFlags
{
    None = 0,
    Live = 1 << 0,
    JapaneseVersion = 1 << 1,
    ChineseVersion = 1 << 2,
    EnglishVersion = 1 << 3,
    Remix = 1 << 4,
    Cover = 1 << 5,
    Instrumental = 1 << 6,
    Acoustic = 1 << 7,
    Remaster = 1 << 8,
    SpedUp = 1 << 9,
    Slowed = 1 << 10,
    Demo = 1 << 11,
    ReRecorded = 1 << 12,
    KoreanVersion = 1 << 13,
}
