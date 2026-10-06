// XML utilities for QQ QRC responses, based on
// WXRIW/Lyricify-Lyrics-Helper XmlUtils and Widdit/now-playing-service Decrypter.
// Original implementation licensed under Apache-2.0.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TaskbarLyrics.QQMusic.Qrc;

public sealed class QrcXmlParseResult
{
    public bool Success { get; set; }
    public bool UsedRepair { get; set; }
    public string? Error { get; set; }
    public string? OrigEncrypted { get; set; }
    public string? TransEncryptedOrRaw { get; set; }
    public string? RomaEncryptedOrRaw { get; set; }
    public string? Lyric1EncryptedOrRaw { get; set; }
    public Dictionary<string, string> FoundNodes { get; set; } = new();
}

public static class QrcXmlParser
{
    private static readonly Dictionary<string, string> VerbatimXmlMappingDict = new(StringComparer.Ordinal)
    {
        { "content", "orig" },
        { "contentts", "ts" },
        { "contentroma", "roma" },
        { "Lyric_1", "lyric" },
    };

    private static readonly Regex AmpRegex = new("&(?![a-zA-Z]{2,6};|#[0-9]{2,4};)", RegexOptions.Compiled);

    private static readonly Regex QuotRegex = new(
        "(\\s+[\\w:.-]+\\s*=\\s*\")(([^\"]*)((\")((?!\\s+[\\w:.-]+\\s*=\\s*\"|\\s*(?:/?|\\?)>))[^\"]*)*)\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Strip outer HTML comments and parse QQ lyric_download XML response.
    /// </summary>
    public static QrcXmlParseResult ParseResponse(string rawResponse)
    {
        var result = new QrcXmlParseResult();

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            result.Error = "QRC response body is empty.";
            return result;
        }

        var content = rawResponse.Trim();
        // Strip UTF-8 BOM if present as text.
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content[1..];
        }

        content = content.Replace("<!--", string.Empty).Replace("-->", string.Empty).Trim();

        try
        {
            var doc = CreateXmlDocument(content, out var usedRepair);
            result.UsedRepair = usedRepair;
            FillFromDocument(doc, result);
            result.Success = true;
        }
        catch (Exception firstEx)
        {
            try
            {
                var repaired = RepairXml(content);
                var doc = CreateXmlDocument(repaired, out _);
                result.UsedRepair = true;
                FillFromDocument(doc, result);
                result.Success = true;
            }
            catch (Exception secondEx)
            {
                result.Error =
                    $"XML parse failed. First: {firstEx.GetType().Name}: {firstEx.Message}; " +
                    $"After repair: {secondEx.GetType().Name}: {secondEx.Message}";
            }
        }

        return result;
    }

    /// <summary>
    /// If decrypted content is still XML with Lyric_1/@LyricContent, extract it.
    /// </summary>
    public static string ExtractLyricContentIfXml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (!text.Contains("<?xml", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Lyric_1", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("LyricContent", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        try
        {
            var doc = CreateXmlDocument(text, out _);
            var map = new Dictionary<string, XmlNode>(StringComparer.Ordinal);
            RecursionFindElement(doc.DocumentElement!, VerbatimXmlMappingDict, map);

            if (map.TryGetValue("lyric", out var node))
            {
                var attr = node.Attributes?["LyricContent"]?.InnerText;
                if (!string.IsNullOrWhiteSpace(attr))
                {
                    return attr;
                }

                if (!string.IsNullOrWhiteSpace(node.InnerText))
                {
                    return node.InnerText;
                }
            }
        }
        catch
        {
            // Keep original text when nested XML extract fails.
        }

        return text;
    }

    private static void FillFromDocument(XmlDocument doc, QrcXmlParseResult result)
    {
        var map = new Dictionary<string, XmlNode>(StringComparer.Ordinal);
        if (doc.DocumentElement is not null)
        {
            RecursionFindElement(doc.DocumentElement, VerbatimXmlMappingDict, map);
        }

        foreach (var pair in map)
        {
            var text = GetNodeText(pair.Value);
            result.FoundNodes[pair.Key] = text is null
                ? string.Empty
                : (text.Length > 80 ? text[..80] + "..." : text);

            switch (pair.Key)
            {
                case "orig":
                    result.OrigEncrypted = text;
                    break;
                case "ts":
                    result.TransEncryptedOrRaw = text;
                    break;
                case "roma":
                    result.RomaEncryptedOrRaw = text;
                    break;
                case "lyric":
                    result.Lyric1EncryptedOrRaw = text;
                    var attr = pair.Value.Attributes?["LyricContent"]?.InnerText;
                    if (!string.IsNullOrWhiteSpace(attr) && string.IsNullOrWhiteSpace(result.OrigEncrypted))
                    {
                        result.OrigEncrypted = attr;
                    }
                    break;
            }
        }
    }

    public static XmlDocument CreateXmlDocument(string content, out bool usedRepairPath)
    {
        usedRepairPath = false;
        content = RemoveIllegalContent(content);
        content = ReplaceAmp(content);
        var fixedContent = ReplaceQuot(content);

        var doc = new XmlDocument
        {
            XmlResolver = null
        };

        try
        {
            doc.LoadXml(fixedContent);
            usedRepairPath = !ReferenceEquals(fixedContent, content) && fixedContent != content;
            return doc;
        }
        catch
        {
            doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(content);
            usedRepairPath = true;
            return doc;
        }
    }

    private static string RepairXml(string content)
    {
        content = content.Trim();
        content = ReplaceAmp(content);
        content = ReplaceQuot(content);
        content = RemoveIllegalContent(content);

        // Escape bare ampersands again after other mutations.
        content = ReplaceAmp(content);

        // Wrap fragment if no root.
        if (!content.TrimStart().StartsWith('<'))
        {
            content = "<root>" + content + "</root>";
        }

        return content;
    }

    private static string ReplaceAmp(string content) => AmpRegex.Replace(content, "&amp;");

    private static string ReplaceQuot(string content)
    {
        var sb = new StringBuilder();
        int currentPos = 0;
        foreach (Match match in QuotRegex.Matches(content))
        {
            sb.Append(content.AsSpan(currentPos, match.Index - currentPos));

            var f = match.Groups[1].Value + match.Groups[2].Value
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;") + "\"";

            sb.Append(f);
            currentPos = match.Index + match.Length;
        }

        sb.Append(content.AsSpan(currentPos));
        return sb.ToString();
    }

    private static string RemoveIllegalContent(string content)
    {
        int left = 0, i = 0;
        while (i < content.Length)
        {
            if (content[i] == '<')
            {
                left = i;
            }

            if (i > 0 && content[i] == '>' && content[i - 1] == '/')
            {
                var part = content.Substring(left, i - left + 1);

                if (part.Contains('=') && part.IndexOf('=') == part.LastIndexOf('='))
                {
                    var part1 = content.Substring(left, part.IndexOf('='));
                    if (!part1.Trim().Contains(' '))
                    {
                        content = content[..left] + content[(i + 1)..];
                        i = 0;
                        continue;
                    }
                }
            }

            i++;
        }

        return content.Trim();
    }

    public static void RecursionFindElement(
        XmlNode xmlNode,
        Dictionary<string, string> mappingDict,
        Dictionary<string, XmlNode> resDict)
    {
        if (mappingDict.TryGetValue(xmlNode.Name, out var value))
        {
            resDict[value] = xmlNode;
        }

        if (!xmlNode.HasChildNodes)
        {
            return;
        }

        for (var i = 0; i < xmlNode.ChildNodes.Count; i++)
        {
            var child = xmlNode.ChildNodes.Item(i);
            if (child is not null)
            {
                RecursionFindElement(child, mappingDict, resDict);
            }
        }
    }

    private static string? GetNodeText(XmlNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node.InnerText;
    }
}
