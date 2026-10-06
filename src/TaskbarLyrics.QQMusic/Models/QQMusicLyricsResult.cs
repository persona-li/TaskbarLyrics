using TaskbarLyrics.Core.Models;
using System.Text.Json.Serialization;
namespace TaskbarLyrics.QQMusic.Models;

public sealed class QQMusicLyricsResult
{
    // Distinguish a confirmed empty response from a transport/parser failure without parsing error text.
    [JsonIgnore]
    public bool HasConfirmedNoLyrics => !IsInstrumental && LrcDetail.RequestSuccess
        && (LrcDetail.ApiCode is 0 or -1901) && !LrcDetail.HasLyric
        && QrcDetail.RequestSuccess && QrcDetail.XmlParsed && !QrcDetail.EncryptedPayloadFound;

    public QQSongCandidate? Song { get; set; }
    public string? Lrc { get; set; }
    public string? Qrc { get; set; }
    public string? Translation { get; set; }
    public string? Romanization { get; set; }
    public List<QrcLine> ParsedQrcLines { get; set; } = new();
    public string TranslationSource { get; set; } = "None";
    public bool IsInstrumental { get; set; }
    public LrcFetchDetail LrcDetail { get; set; } = new();
    public QrcFetchDetail QrcDetail { get; set; } = new();
}

public sealed class LrcFetchDetail
{
    public bool RequestSuccess { get; set; }
    public int? HttpStatusCode { get; set; }
    public int? ApiCode { get; set; }
    public bool HasLyric { get; set; }
    public int LyricLength { get; set; }
    public bool HasTranslation { get; set; }
    public int TranslationLength { get; set; }
    public bool IsInstrumental { get; set; }
    public string? Error { get; set; }
    public string? ResponsePreview { get; set; }
    public string? RawResponse { get; set; }
}

public sealed class QrcFetchDetail
{
    public bool RequestSuccess { get; set; }
    public int? HttpStatusCode { get; set; }
    public bool XmlParsed { get; set; }
    public bool EncryptedPayloadFound { get; set; }
    public bool TranslationPayloadFound { get; set; }
    public bool RomanizationPayloadFound { get; set; }
    public bool HexDecodeSuccess { get; set; }
    public bool DecryptSuccess { get; set; }
    public bool ZlibSuccess { get; set; }
    public bool Utf8Success { get; set; }
    public bool ParseSuccess { get; set; }
    public bool ParserUnsupported { get; set; }
    public int EncryptedCharCount { get; set; }
    public int EncryptedByteCount { get; set; }
    public int DecryptedLength { get; set; }
    public int LineCount { get; set; }
    public int WordCount { get; set; }
    public string? Error { get; set; }
    public string? StageFailed { get; set; }
    public string? ResponsePreview { get; set; }
    public string? RawResponse { get; set; }
    public string? EncryptedPayload { get; set; }
    public string? DecryptedText { get; set; }
    public string? TranslationEncryptedOrRaw { get; set; }
    public string? RomanizationEncryptedOrRaw { get; set; }
}

public sealed class ProbeSummary
{
    public ProbeInputSummary Input { get; set; } = new();
    public SelectedSongSummary? SelectedSong { get; set; }
    public SearchSummary Search { get; set; } = new();
    public LrcSummary Lrc { get; set; } = new();
    public QrcSummary Qrc { get; set; } = new();
    public string TranslationSource { get; set; } = "None";
    public int SuccessLevel { get; set; }
    public string Result { get; set; } = "FAIL";
    public DateTimeOffset Timestamp { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
}

public sealed class ProbeInputSummary
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
}

public sealed class SelectedSongSummary
{
    public string SongId { get; set; } = string.Empty;
    public string SongMid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public double MatchScore { get; set; }
}

public sealed class SearchSummary
{
    public bool Success { get; set; }
    public int CandidateCount { get; set; }
    public int? HttpStatusCode { get; set; }
    public int? ApiCode { get; set; }
    public string? Error { get; set; }
}

public sealed class LrcSummary
{
    public bool RequestSuccess { get; set; }
    public bool HasLyrics { get; set; }
    public bool HasTranslation { get; set; }
    public bool IsInstrumental { get; set; }
    public int? ApiCode { get; set; }
}

public sealed class QrcSummary
{
    public bool RequestSuccess { get; set; }
    public bool XmlParsed { get; set; }
    public bool EncryptedPayloadFound { get; set; }
    public bool DecryptSuccess { get; set; }
    public bool ZlibSuccess { get; set; }
    public bool ParseSuccess { get; set; }
    public int LineCount { get; set; }
    public int WordCount { get; set; }
}
