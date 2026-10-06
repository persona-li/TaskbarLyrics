// QRC decryption pipeline based on
// WXRIW/Lyricify-Lyrics-Helper and Widdit/now-playing-service.
// Original implementation licensed under Apache-2.0.
//
// Pipeline:
//   Hex string -> byte[] -> Triple DES (custom DESHelper) -> zlib -> UTF-8

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TaskbarLyrics.QQMusic.Qrc;

public sealed class QrcDecryptResult
{
    public bool Success { get; set; }
    public bool HexValid { get; set; }
    public bool HexDecodeSuccess { get; set; }
    public bool DecryptSuccess { get; set; }
    public bool ZlibSuccess { get; set; }
    public bool Utf8Success { get; set; }
    public int EncryptedCharCount { get; set; }
    public int EncryptedByteCount { get; set; }
    public int DecryptedByteCount { get; set; }
    public string? Text { get; set; }
    public string? StageFailed { get; set; }
    public string? Error { get; set; }
    public string DecryptMethod { get; set; } = "DESHelper (Lyricify-compatible Triple DES ECB)";
}

public static class QrcDecrypter
{
    /// <summary>
    /// Fixed 24-byte ASCII key used by QQ Music QRC.
    /// </summary>
    public static readonly byte[] QQKey = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");

    private static readonly Regex HexRegex = new("^[0-9A-Fa-f]+$", RegexOptions.Compiled);

    /// <summary>
    /// Decrypt a QQ QRC encrypted hex payload.
    /// </summary>
    public static QrcDecryptResult DecryptLyrics(string? encryptedLyrics)
    {
        var result = new QrcDecryptResult();

        if (string.IsNullOrWhiteSpace(encryptedLyrics))
        {
            result.StageFailed = "payload-empty";
            result.Error = "QRC encrypted payload is empty.";
            return result;
        }

        var hex = encryptedLyrics.Trim();
        // Remove whitespace/newlines sometimes present in XML text nodes.
        hex = Regex.Replace(hex, @"\s+", string.Empty);
        result.EncryptedCharCount = hex.Length;

        if (hex.Length == 0 || (hex.Length % 2) != 0 || !HexRegex.IsMatch(hex))
        {
            result.StageFailed = "hex-invalid";
            result.Error = "QRC encrypted payload format invalid.";
            result.HexValid = false;
            return result;
        }

        result.HexValid = true;

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = HexStringToByteArray(hex);
            result.HexDecodeSuccess = true;
            result.EncryptedByteCount = encryptedBytes.Length;
        }
        catch (Exception ex)
        {
            result.StageFailed = "hex-decode";
            result.Error = ex.Message;
            return result;
        }

        if (encryptedBytes.Length == 0 || (encryptedBytes.Length % 8) != 0)
        {
            result.StageFailed = "block-size";
            result.Error = "QRC encrypted payload format invalid. Byte length must be a multiple of 8.";
            return result;
        }

        byte[] decryptedBytes;
        try
        {
            decryptedBytes = TripleDesDecrypt(encryptedBytes);
            result.DecryptSuccess = true;
        }
        catch (Exception ex)
        {
            result.StageFailed = "3des";
            result.Error = ex.Message;
            return result;
        }

        byte[] unzipped;
        try
        {
            unzipped = ZlibDecompress(decryptedBytes);
            result.ZlibSuccess = true;
            result.DecryptedByteCount = unzipped.Length;
        }
        catch (Exception ex)
        {
            // Try raw DEFLATE as secondary diagnostic path (not primary).
            try
            {
                unzipped = RawDeflateDecompress(decryptedBytes);
                result.ZlibSuccess = true;
                result.DecryptedByteCount = unzipped.Length;
                result.DecryptMethod += " + raw-DEFLATE-fallback";
            }
            catch
            {
                result.StageFailed = "zlib";
                result.Error = ex.Message;
                return result;
            }
        }

        try
        {
            var span = unzipped.AsSpan();
            var bom = Encoding.UTF8.GetPreamble();
            if (span.Length >= bom.Length && span[..bom.Length].SequenceEqual(bom))
            {
                span = span[bom.Length..];
            }

            var text = Encoding.UTF8.GetString(span);
            result.Utf8Success = true;
            result.Text = text;
            result.Success = !string.IsNullOrWhiteSpace(text);
            if (!result.Success)
            {
                result.StageFailed = "empty-text";
                result.Error = "Decrypted QRC text is empty.";
            }
        }
        catch (Exception ex)
        {
            result.StageFailed = "utf8";
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Attempt to resolve a payload that may be hex-encrypted QRC or plain lyrics text.
    /// </summary>
    public static QrcDecryptResult DecryptOrPassthrough(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new QrcDecryptResult
            {
                StageFailed = "payload-empty",
                Error = "Payload is empty."
            };
        }

        var trimmed = payload.Trim();
        var compact = Regex.Replace(trimmed, @"\s+", string.Empty);

        // Prefer decrypt when it looks like hex.
        if (compact.Length >= 16 && compact.Length % 2 == 0 && HexRegex.IsMatch(compact))
        {
            var decrypted = DecryptLyrics(compact);
            if (decrypted.Success)
            {
                return decrypted;
            }

            // Fall through to plain text if it already looks like lyrics.
            if (LooksLikeLyricsText(trimmed))
            {
                return new QrcDecryptResult
                {
                    Success = true,
                    HexValid = false,
                    Text = trimmed.Replace("//", string.Empty),
                    DecryptMethod = "passthrough-plain-lyrics (hex decrypt failed)"
                };
            }

            return decrypted;
        }

        if (LooksLikeLyricsText(trimmed))
        {
            return new QrcDecryptResult
            {
                Success = true,
                HexValid = false,
                Text = trimmed.Replace("//", string.Empty),
                DecryptMethod = "passthrough-plain-lyrics"
            };
        }

        return new QrcDecryptResult
        {
            StageFailed = "unrecognized",
            Error = "Payload is neither valid hex-encrypted QRC nor plain lyrics text."
        };
    }

    public static bool LooksLikeLyricsText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains('[')
               || text.Contains('(')
               || text.Contains("<?xml", StringComparison.OrdinalIgnoreCase)
               || text.Contains("LyricContent", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(text, @"\[\d+,\d+\]")
               || Regex.IsMatch(text, @"\[\d{1,2}:\d{2}");
    }

    private static byte[] TripleDesDecrypt(byte[] encryptedTextByte)
    {
        var data = new byte[encryptedTextByte.Length];
        var schedule = DESHelper.CreateSchedule();
        DESHelper.TripleDESKeySetup(QQKey, schedule, DESHelper.DECRYPT);

        for (int i = 0; i < encryptedTextByte.Length; i += 8)
        {
            var inputBlock = new byte[8];
            Buffer.BlockCopy(encryptedTextByte, i, inputBlock, 0, 8);
            var temp = new byte[8];
            DESHelper.TripleDESCrypt(inputBlock, temp, schedule);
            Buffer.BlockCopy(temp, 0, data, i, 8);
        }

        return data;
    }

    /// <summary>
    /// Decompress zlib-wrapped data (matches Java Inflater / SharpZipLib InflaterInputStream).
    /// </summary>
    private static byte[] ZlibDecompress(byte[] data)
    {
        using var compressed = new MemoryStream(data);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);
        return decompressed.ToArray();
    }

    private static byte[] RawDeflateDecompress(byte[] data)
    {
        using var compressed = new MemoryStream(data);
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        using var decompressed = new MemoryStream();
        deflate.CopyTo(decompressed);
        return decompressed.ToArray();
    }

    private static byte[] HexStringToByteArray(string hexString)
    {
        int length = hexString.Length;
        byte[] bytes = new byte[length / 2];
        for (int i = 0; i < length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(hexString.Substring(i, 2), 16);
        }

        return bytes;
    }
}
