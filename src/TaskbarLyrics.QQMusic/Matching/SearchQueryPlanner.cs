using System.Text;
using System.Text.RegularExpressions;
using TaskbarLyrics.QQMusic.Services;

namespace TaskbarLyrics.QQMusic.Matching;

public sealed class SearchQuerySpec
{
    public required SearchQueryPhase Phase { get; init; }
    public required string Keyword { get; init; }
    public required string Label { get; init; }
}

/// <summary>
/// Builds multi-phase QQ search keywords from track metadata + artist aliases.
/// General-purpose — no per-song hardcodes.
/// </summary>
public sealed class SearchQueryPlanner
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    private readonly ArtistAliasStore _aliases;

    public SearchQueryPlanner(ArtistAliasStore aliases)
    {
        _aliases = aliases;
    }

    public IReadOnlyList<SearchQuerySpec> Plan(string title, string? artist, string? album = null)
    {
        var list = new List<SearchQuerySpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(SearchQueryPhase phase, string keyword, string label)
        {
            keyword = MultiSpace.Replace(keyword.Trim(), " ").Trim();
            if (keyword.Length == 0 || !seen.Add(keyword))
            {
                return;
            }

            list.Add(new SearchQuerySpec
            {
                Phase = phase,
                Keyword = keyword,
                Label = label
            });
        }

        var t = (title ?? "").Trim();
        var a = (artist ?? "").Trim();
        var simplified = SimplifyTitle(t);

        // Phase 1 — Strict
        if (t.Length > 0 && a.Length > 0)
        {
            Add(SearchQueryPhase.Strict, $"{t} {a}", "Strict:title+artist");
        }
        else if (t.Length > 0)
        {
            Add(SearchQueryPhase.Strict, t, "Strict:title");
        }

        // Phase 2 — Relaxed
        if (t.Length > 0)
        {
            Add(SearchQueryPhase.Relaxed, t, "Relaxed:title-only");
        }

        if (simplified.Length > 0 && !string.Equals(simplified, t, StringComparison.OrdinalIgnoreCase))
        {
            if (a.Length > 0)
            {
                Add(SearchQueryPhase.Relaxed, $"{simplified} {a}", "Relaxed:simplified+artist");
            }

            Add(SearchQueryPhase.Relaxed, simplified, "Relaxed:simplified-title");
        }

        // Core without outer punctuation noise (keep version words if any)
        var core = StripOuterBrackets(simplified.Length > 0 ? simplified : t);
        if (core.Length > 0
            && !string.Equals(core, t, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(core, simplified, StringComparison.OrdinalIgnoreCase))
        {
            if (a.Length > 0)
            {
                Add(SearchQueryPhase.Relaxed, $"{core} {a}", "Relaxed:core+artist");
            }
        }

        // Phase 3 — Artist aliases
        if (t.Length > 0 && a.Length > 0)
        {
            foreach (var alias in _aliases.GetAliases(a))
            {
                Add(SearchQueryPhase.Alias, $"{t} {alias}", $"Alias:{alias}");
                if (simplified.Length > 0 && !string.Equals(simplified, t, StringComparison.OrdinalIgnoreCase))
                {
                    Add(SearchQueryPhase.Alias, $"{simplified} {alias}", $"AliasSimp:{alias}");
                }
            }
        }

        // Album rarely helps alone but can with title
        if (!string.IsNullOrWhiteSpace(album) && t.Length > 0)
        {
            Add(SearchQueryPhase.Relaxed, $"{t} {album.Trim()}", "Relaxed:title+album");
        }

        return list;
    }

    /// <summary>
    /// Soft title simplify for recall: punctuation/spacing only.
    /// Does NOT strip Live/Remix/Japanese version markers.
    /// </summary>
    public static string SimplifyTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var s = title.Trim();
        try
        {
            s = s.Normalize(NormalizationForm.FormKC);
        }
        catch
        {
            // keep
        }

        s = s.Replace('\u3000', ' ').Replace('\u00A0', ' ');
        // You(=I) → You I
        s = s.Replace("(=)", " ");
        s = s.Replace('=', ' ');
        s = s.Replace('（', ' ').Replace('）', ' ');
        s = s.Replace('(', ' ').Replace(')', ' ');
        s = s.Replace('【', ' ').Replace('】', ' ');
        s = s.Replace('[', ' ').Replace(']', ' ');
        s = s.Replace('「', ' ').Replace('」', ' ');
        s = s.Replace('『', ' ').Replace('』', ' ');
        s = s.Replace('：', ' ').Replace(':', ' ');
        s = s.Replace('；', ' ').Replace(';', ' ');
        s = s.Replace('，', ' ').Replace(',', ' ');
        s = s.Replace('。', ' ').Replace('.', ' ');
        s = s.Replace('！', ' ').Replace('!', ' ');
        s = s.Replace('？', ' ').Replace('?', ' ');
        s = s.Replace('／', ' ').Replace('/', ' ');
        s = s.Replace('_', ' ').Replace('-', ' ').Replace('—', ' ').Replace('–', ' ');
        s = MultiSpace.Replace(s, " ").Trim();
        return s;
    }

    private static string StripOuterBrackets(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        // Remove trailing parenthetical group only if short version-like — keep for simplify path.
        return MultiSpace.Replace(s, " ").Trim();
    }
}
