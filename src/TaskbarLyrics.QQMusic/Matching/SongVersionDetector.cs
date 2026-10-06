using System.Text.RegularExpressions;

namespace TaskbarLyrics.QQMusic.Matching;

/// <summary>
/// Detects version markers in title/album without stripping them from match strings.
/// </summary>
public static class SongVersionDetector
{
    private static readonly (SongVersionFlags Flag, Regex Pattern)[] Rules =
    {
        (SongVersionFlags.Live, new Regex(
            @"\b(live|concert)\b|现场|ライブ|演唱會|演唱会",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.JapaneseVersion, new Regex(
            @"\b(japanese\s*ver(?:sion)?|japan\s*edition|jp\s*ver)\b|日文版|日本語|日本版|日语版",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.ChineseVersion, new Regex(
            @"\b(chinese\s*ver(?:sion)?|cn\s*ver|mandarin)\b|中文版|国语版|國語版|华语版",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.KoreanVersion, new Regex(
            @"\b(korean\s*ver(?:sion)?|kr\s*ver)\b|韩文版|韩语版|韓文版|韓語版|한국어\s*버전",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.EnglishVersion, new Regex(
            @"\b(english\s*ver(?:sion)?|en\s*ver)\b|英文版|英语版",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.Remix, new Regex(
            @"\b(remix|dj\b|mix\b|mashup)\b|混音|网友改编|網友改編",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.Cover, new Regex(
            @"\bcover\b|翻唱|カバー|カバー曲",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.Instrumental, new Regex(
            @"\b(instrumental|inst\.?|karaoke|off\s*vocal)\b|伴奏|纯音乐|純音樂|カラオケ",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.Acoustic, new Regex(
            @"\b(acoustic|unplugged)\b|原声|不插电|不插電",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.Remaster, new Regex(
            @"\b(remaster(?:ed)?)\b|重制|重製|数字修复",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.SpedUp, new Regex(
            @"\b(sped\s*up|speed\s*up|nightcore)\b|加速",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.Slowed, new Regex(
            @"\b(slowed|slowed\s*\+?\s*reverb)\b|减速|慢速",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.Demo, new Regex(
            @"\bdemo\b|小样|小樣",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (SongVersionFlags.ReRecorded, new Regex(
            @"\b(re[- ]?recorded|taylor'?s\s*version)\b|重新录制|重新錄製|重录",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    public static SongVersionFlags Detect(string? title, string? album = null)
    {
        var text = $"{title ?? ""} {album ?? ""}";
        if (string.IsNullOrWhiteSpace(text))
        {
            return SongVersionFlags.None;
        }

        var flags = SongVersionFlags.None;
        foreach (var (flag, pattern) in Rules)
        {
            if (pattern.IsMatch(text))
            {
                flags |= flag;
            }
        }

        return flags;
    }

    /// <summary>Flags present on candidate but not on input.</summary>
    public static SongVersionFlags ExtraFlags(SongVersionFlags input, SongVersionFlags candidate) =>
        candidate & ~input;

    public static SongVersionFlags LanguageDifference(SongVersionFlags input, SongVersionFlags candidate) =>
        (input ^ candidate) & (SongVersionFlags.JapaneseVersion | SongVersionFlags.ChineseVersion
            | SongVersionFlags.EnglishVersion | SongVersionFlags.KoreanVersion);

    public static double ComputePenalty(SongVersionFlags extra)
    {
        if (extra == SongVersionFlags.None)
        {
            return 0;
        }

        double penalty = 0;
        if (extra.HasFlag(SongVersionFlags.Live)) penalty += 25;
        if (extra.HasFlag(SongVersionFlags.JapaneseVersion)) penalty += 30;
        if (extra.HasFlag(SongVersionFlags.ChineseVersion)) penalty += 30;
        if (extra.HasFlag(SongVersionFlags.EnglishVersion)) penalty += 30;
        if (extra.HasFlag(SongVersionFlags.KoreanVersion)) penalty += 30;
        if (extra.HasFlag(SongVersionFlags.Remix)) penalty += 35;
        if (extra.HasFlag(SongVersionFlags.Cover)) penalty += 40;
        if (extra.HasFlag(SongVersionFlags.Instrumental)) penalty += 35;
        if (extra.HasFlag(SongVersionFlags.Acoustic)) penalty += 15;
        if (extra.HasFlag(SongVersionFlags.Remaster)) penalty += 10;
        if (extra.HasFlag(SongVersionFlags.SpedUp)) penalty += 40;
        if (extra.HasFlag(SongVersionFlags.Slowed)) penalty += 40;
        if (extra.HasFlag(SongVersionFlags.Demo)) penalty += 25;
        if (extra.HasFlag(SongVersionFlags.ReRecorded)) penalty += 25;
        return penalty;
    }

    public static string Describe(SongVersionFlags flags)
    {
        if (flags == SongVersionFlags.None)
        {
            return "None";
        }

        return flags.ToString();
    }
}
