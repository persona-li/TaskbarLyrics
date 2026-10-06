namespace TaskbarLyrics.App.Config;

/// <summary>Committed colors only, newest first, stored independently per color editor.</summary>
public static class RecentColorHistory
{
    public const int Capacity = 6;

    public static string? Canonicalize(string? color)
    {
        var text = color?.Trim();
        if (text is null || text.Length is not (7 or 9) || text[0] != '#') return null;
        for (var i = 1; i < text.Length; i++)
            if (!Uri.IsHexDigit(text[i])) return null;
        return (text.Length == 7 ? "#FF" + text[1..] : text).ToUpperInvariant();
    }

    public static List<string> Normalize(IEnumerable<string?>? colors)
    {
        var result = new List<string>(Capacity);
        if (colors is null) return result;
        foreach (var color in colors)
        {
            var canonical = Canonicalize(color);
            if (canonical is null || result.Contains(canonical)) continue;
            result.Add(canonical);
            if (result.Count == Capacity) break;
        }
        return result;
    }

    public static List<string> Remember(IEnumerable<string?>? colors, string? color)
    {
        var canonical = Canonicalize(color);
        return canonical is null ? Normalize(colors) : Normalize(new[] { canonical }.Concat(colors ?? []));
    }
}
