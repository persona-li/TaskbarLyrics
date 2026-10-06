using System.Text.Json;
using System.Text.RegularExpressions;
using TaskbarLyrics.QQMusic.Logging;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.QQMusic.Qrc;

namespace TaskbarLyrics.QQMusic.Services;

/// <summary>
/// Fetches ordinary LRC + QRC (encrypted) lyrics for a QQ Music song.
/// Core logic has no Console dependency for future GSMTC integration.
/// </summary>
public sealed class QQMusicLyricsService
{
    private readonly QQMusicHttpClient _http;
    private readonly IQqMusicLogger _logger;

    private static readonly Regex JsonpRegex = new(
        @"^[^(]+\((.*)\)\s*;?\s*$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public QQMusicLyricsService(QQMusicHttpClient http, IQqMusicLogger logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<QQMusicLyricsResult> GetLyricsAsync(
        QQSongCandidate song,
        CancellationToken cancellationToken = default)
    {
        var result = new QQMusicLyricsResult
        {
            Song = song
        };

        // LRC and QRC are independent — continue even if one fails.
        await FetchLrcAsync(song, result, cancellationToken);
        await FetchQrcAsync(song, result, cancellationToken);

        // Translation priority: QRC contentts > LRC trans
        if (!string.IsNullOrWhiteSpace(result.Translation))
        {
            // already set from QRC
            if (result.TranslationSource == "None" || string.IsNullOrEmpty(result.TranslationSource))
            {
                result.TranslationSource = "QRC";
            }
        }
        else if (!string.IsNullOrWhiteSpace(result.LrcDetail.RawResponse)
                 && result.LrcDetail.HasTranslation
                 && !string.IsNullOrWhiteSpace(GetLrcTranslationFromDetail(result)))
        {
            // Already applied in FetchLrc if no QRC trans; ensure source.
            if (string.IsNullOrWhiteSpace(result.Translation))
            {
                // handled below via stored fields
            }
        }

        if (string.IsNullOrWhiteSpace(result.Translation) && result.LrcDetail.HasTranslation)
        {
            // Translation text is kept in result if FetchLrc set a temporary holder.
        }

        if (string.IsNullOrWhiteSpace(result.Translation))
        {
            result.TranslationSource = "None";
        }

        return result;
    }

    private static string? GetLrcTranslationFromDetail(QQMusicLyricsResult result)
    {
        return result.TranslationSource == "LRC" ? result.Translation : null;
    }

    private async Task FetchLrcAsync(
        QQSongCandidate song,
        QQMusicLyricsResult result,
        CancellationToken cancellationToken)
    {
        var detail = result.LrcDetail;
        try
        {
            var query = QQMusicEndpoints.BuildLrcParams(song.SongId);
            var http = await _http.GetAsync("lrc", QQMusicEndpoints.LrcUrl, query, cancellationToken);
            detail.HttpStatusCode = http.StatusCode;
            detail.RawResponse = http.Body;
            detail.ResponsePreview = http.BodyPreview();

            if (!http.Success)
            {
                detail.RequestSuccess = false;
                detail.Error = $"LRC HTTP failed: {http.StatusCode} {http.ReasonPhrase}";
                _logger.Error(detail.Error);
                return;
            }

            detail.RequestSuccess = true;
            var jsonText = UnwrapJsonp(http.Body);

            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var codeEl))
            {
                detail.Error = "LRC JSON missing code.";
                _logger.Error(detail.Error + " Preview: " + detail.ResponsePreview);
                return;
            }

            var code = codeEl.GetInt32();
            detail.ApiCode = code;

            // -1901 = no lyrics (not a network error)
            if (code != 0 && code != -1901)
            {
                detail.Error = $"LRC API code={code}";
                _logger.Error(detail.Error);
                return;
            }

            if (code == -1901)
            {
                _logger.Info("LRC API code=-1901 (no lyrics for this song).");
                return;
            }

            string? lyric = null;
            string? trans = null;

            if (root.TryGetProperty("lyric", out var lyricEl) && lyricEl.ValueKind == JsonValueKind.String)
            {
                lyric = lyricEl.GetString();
            }

            if (root.TryGetProperty("trans", out var transEl) && transEl.ValueKind == JsonValueKind.String)
            {
                trans = transEl.GetString();
            }

            if (!string.IsNullOrWhiteSpace(lyric))
            {
                if (lyric.Contains("此歌曲为没有填词的纯音乐", StringComparison.Ordinal))
                {
                    detail.IsInstrumental = true;
                    result.IsInstrumental = true;
                    _logger.Info("LRC marked as instrumental pure music.");
                }
                else if (lyric.Contains("00") || lyric.Contains('['))
                {
                    detail.HasLyric = true;
                    detail.LyricLength = lyric.Length;
                    result.Lrc = lyric;
                }
                else
                {
                    _logger.Warn("LRC text present but does not look like timed lyrics.");
                    detail.HasLyric = true;
                    detail.LyricLength = lyric.Length;
                    result.Lrc = lyric;
                }
            }

            if (!string.IsNullOrWhiteSpace(trans))
            {
                detail.HasTranslation = true;
                detail.TranslationLength = trans.Length;
                // Only set as translation if QRC has not provided one yet.
                if (string.IsNullOrWhiteSpace(result.Translation))
                {
                    result.Translation = trans;
                    result.TranslationSource = "LRC";
                }
            }

            _logger.Info(
                $"LRC code={code} hasLyric={detail.HasLyric} len={detail.LyricLength} " +
                $"hasTrans={detail.HasTranslation} instrumental={detail.IsInstrumental}");
        }
        catch (Exception ex)
        {
            detail.Error = ex.Message;
            _logger.Error("lrc", ex);
        }
    }

    private async Task FetchQrcAsync(
        QQSongCandidate song,
        QQMusicLyricsResult result,
        CancellationToken cancellationToken)
    {
        var detail = result.QrcDetail;
        try
        {
            var query = QQMusicEndpoints.BuildQrcParams(song.SongId);
            var http = await _http.GetAsync("qrc", QQMusicEndpoints.QrcUrl, query, cancellationToken);
            detail.HttpStatusCode = http.StatusCode;
            detail.RawResponse = http.Body;
            detail.ResponsePreview = http.BodyPreview();

            if (!http.Success)
            {
                detail.RequestSuccess = false;
                detail.StageFailed = "http";
                detail.Error = $"QRC HTTP failed: {http.StatusCode} {http.ReasonPhrase}";
                _logger.Error(detail.Error);
                return;
            }

            detail.RequestSuccess = true;

            if (string.IsNullOrWhiteSpace(http.Body))
            {
                detail.StageFailed = "empty-body";
                detail.Error = "QRC response body is empty.";
                return;
            }

            var xml = QrcXmlParser.ParseResponse(http.Body);
            if (!xml.Success)
            {
                detail.XmlParsed = false;
                detail.StageFailed = "xml";
                detail.Error = xml.Error ?? "XML parse failed.";
                _logger.Error($"QRC XML parse failed: {detail.Error}");
                return;
            }

            detail.XmlParsed = true;
            _logger.Info($"QRC XML OK. Nodes: {string.Join(", ", xml.FoundNodes.Keys)} repair={xml.UsedRepair}");

            detail.EncryptedPayloadFound = !string.IsNullOrWhiteSpace(xml.OrigEncrypted);
            detail.TranslationPayloadFound = !string.IsNullOrWhiteSpace(xml.TransEncryptedOrRaw);
            detail.RomanizationPayloadFound = !string.IsNullOrWhiteSpace(xml.RomaEncryptedOrRaw);
            detail.EncryptedPayload = xml.OrigEncrypted;
            detail.TranslationEncryptedOrRaw = xml.TransEncryptedOrRaw;
            detail.RomanizationEncryptedOrRaw = xml.RomaEncryptedOrRaw;

            if (!detail.EncryptedPayloadFound && !string.IsNullOrWhiteSpace(xml.Lyric1EncryptedOrRaw))
            {
                detail.EncryptedPayload = xml.Lyric1EncryptedOrRaw;
                detail.EncryptedPayloadFound = true;
            }

            if (!detail.EncryptedPayloadFound)
            {
                detail.StageFailed = "payload-missing";
                detail.Error = "QRC payload 不存在 (no content/orig node).";
                _logger.Warn(detail.Error);
            }
            else
            {
                detail.EncryptedCharCount = detail.EncryptedPayload!.Trim().Length;
                var dec = QrcDecrypter.DecryptOrPassthrough(detail.EncryptedPayload);
                detail.HexDecodeSuccess = dec.HexDecodeSuccess;
                detail.DecryptSuccess = dec.DecryptSuccess || (dec.Success && dec.DecryptMethod.Contains("passthrough", StringComparison.OrdinalIgnoreCase));
                detail.ZlibSuccess = dec.ZlibSuccess || (dec.Success && dec.DecryptMethod.Contains("passthrough", StringComparison.OrdinalIgnoreCase));
                detail.Utf8Success = dec.Utf8Success || dec.Success;
                detail.EncryptedByteCount = dec.EncryptedByteCount;
                detail.DecryptedLength = dec.Text?.Length ?? 0;

                if (!dec.Success)
                {
                    detail.StageFailed = dec.StageFailed ?? "decrypt";
                    detail.Error = dec.Error ?? "QRC decrypt failed.";
                    _logger.Error($"QRC decrypt failed at {detail.StageFailed}: {detail.Error}");
                }
                else
                {
                    var text = QrcXmlParser.ExtractLyricContentIfXml(dec.Text!);
                    detail.DecryptedText = text;
                    result.Qrc = text;
                    _logger.Info(
                        $"QRC decrypt OK via {dec.DecryptMethod}. " +
                        $"hexChars={dec.EncryptedCharCount} bytes={dec.EncryptedByteCount} textLen={text.Length}");

                    var parsed = QrcParser.Parse(text);
                    detail.ParseSuccess = parsed.Success;
                    detail.ParserUnsupported = parsed.Unsupported;
                    detail.LineCount = parsed.Lines.Count;
                    detail.WordCount = parsed.WordCount;
                    result.ParsedQrcLines = parsed.Lines;

                    if (parsed.Unsupported)
                    {
                        _logger.Warn($"QRC Parser Unsupported (decrypt still success): {parsed.Error}");
                    }
                    else
                    {
                        _logger.Info($"QRC parse OK: lines={parsed.Lines.Count} words={parsed.WordCount}");
                    }
                }
            }

            // Translation from contentts (priority over LRC)
            if (!string.IsNullOrWhiteSpace(xml.TransEncryptedOrRaw))
            {
                var tdec = QrcDecrypter.DecryptOrPassthrough(xml.TransEncryptedOrRaw);
                if (tdec.Success && !string.IsNullOrWhiteSpace(tdec.Text))
                {
                    var ttext = QrcXmlParser.ExtractLyricContentIfXml(tdec.Text);
                    ttext = ttext.Replace("//", string.Empty);
                    result.Translation = ttext;
                    result.TranslationSource = "QRC";
                    _logger.Info($"Translation from QRC contentts, length={ttext.Length}");
                }
                else
                {
                    // Widdit sometimes uses raw contentts as translation.
                    var raw = xml.TransEncryptedOrRaw.Replace("//", string.Empty).Trim();
                    if (QrcDecrypter.LooksLikeLyricsText(raw))
                    {
                        result.Translation = raw;
                        result.TranslationSource = "QRC";
                        _logger.Info($"Translation from QRC contentts (raw), length={raw.Length}");
                    }
                    else
                    {
                        _logger.Warn($"QRC contentts present but decrypt failed: {tdec.Error}");
                    }
                }
            }

            // Romanization
            if (!string.IsNullOrWhiteSpace(xml.RomaEncryptedOrRaw))
            {
                var rdec = QrcDecrypter.DecryptOrPassthrough(xml.RomaEncryptedOrRaw);
                if (rdec.Success && !string.IsNullOrWhiteSpace(rdec.Text))
                {
                    result.Romanization = QrcXmlParser.ExtractLyricContentIfXml(rdec.Text);
                    _logger.Info($"Romanization length={result.Romanization.Length}");
                }
            }
        }
        catch (Exception ex)
        {
            detail.Error = ex.Message;
            detail.StageFailed = detail.StageFailed ?? "exception";
            _logger.Error("qrc", ex);
        }
    }

    private static string UnwrapJsonp(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        var m = JsonpRegex.Match(trimmed);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        // Fallback: find first { ... last }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }
}
