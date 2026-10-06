using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TaskbarLyrics.Core.Models;

/// <summary>
/// Source: LyricsSyncProbe TrackIdentity (track-change key).
/// </summary>
public sealed record TrackIdentity(
    string Title,
    string Artist,
    TimeSpan? Duration,
    string? Album = null)
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} - {Artist}";

    public string TrackKey
    {
        get
        {
            var t = Normalize(Title);
            var a = Normalize(Artist);
            var d = Duration.HasValue
                ? ((int)Math.Round(Duration.Value.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
                : "na";
            return $"{t}|{a}|{d}";
        }
    }

    public int? DurationSeconds =>
        Duration.HasValue ? (int)Math.Round(Duration.Value.TotalSeconds) : null;

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var s = value.Trim().ToLowerInvariant();
        s = s.Replace('\u3000', ' ').Replace('\u00A0', ' ');
        try
        {
            s = s.Normalize(NormalizationForm.FormKC);
        }
        catch
        {
            // keep original
        }

        return MultiSpace.Replace(s, " ").Trim();
    }

    public bool SameAs(TrackIdentity? other) =>
        other is not null && string.Equals(TrackKey, other.TrackKey, StringComparison.Ordinal);
}
