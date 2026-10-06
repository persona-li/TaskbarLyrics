using System.Text;

namespace TaskbarLyrics.App.Controls;

public enum TextScript
{
    Han,
    Hiragana,
    Katakana,
    Hangul,
    Latin,
    Cyrillic,
    Arabic,
    Other
}

/// <summary>
/// Lightweight Unicode script classification via System.Text.Rune (no ICU).
/// </summary>
public static class UnicodeScriptDetector
{
    public static TextScript Detect(Rune rune)
    {
        var cp = rune.Value;

        // Combining marks / whitespace / punctuation -> Other (resolved by context later)
        if (Rune.IsWhiteSpace(rune) || IsPunctuationLike(cp))
        {
            return TextScript.Other;
        }

        // Hiragana
        if (cp is >= 0x3040 and <= 0x309F)
        {
            return TextScript.Hiragana;
        }

        // Katakana + halfwidth katakana
        if (cp is (>= 0x30A0 and <= 0x30FF) or (>= 0xFF65 and <= 0xFF9F))
        {
            return TextScript.Katakana;
        }

        // Hangul syllables + jamo
        if (cp is (>= 0xAC00 and <= 0xD7AF)
            or (>= 0x1100 and <= 0x11FF)
            or (>= 0x3130 and <= 0x318F)
            or (>= 0xA960 and <= 0xA97F)
            or (>= 0xD7B0 and <= 0xD7FF))
        {
            return TextScript.Hangul;
        }

        // CJK Unified Ideographs + extensions (common ranges)
        if (cp is (>= 0x4E00 and <= 0x9FFF)
            or (>= 0x3400 and <= 0x4DBF)
            or (>= 0xF900 and <= 0xFAFF)
            or (>= 0x20000 and <= 0x2A6DF)
            or (>= 0x2A700 and <= 0x2B73F)
            or (>= 0x2B740 and <= 0x2B81F)
            or (>= 0x2B820 and <= 0x2CEAF)
            or (>= 0x3000 and <= 0x303F)) // CJK symbols often treated as Han-context
        {
            // 0x3000-303F includes ideographic punctuation — keep Han for CJK punctuation blocks
            if (cp is >= 0x3000 and <= 0x303F && IsPunctuationLike(cp))
            {
                return TextScript.Other;
            }

            if (cp is >= 0x4E00 and <= 0x9FFF
                or (>= 0x3400 and <= 0x4DBF)
                or (>= 0xF900 and <= 0xFAFF)
                or (>= 0x20000 and <= 0x2CEAF))
            {
                return TextScript.Han;
            }
        }

        // Cyrillic
        if (cp is (>= 0x0400 and <= 0x04FF)
            or (>= 0x0500 and <= 0x052F)
            or (>= 0x2DE0 and <= 0x2DFF)
            or (>= 0xA640 and <= 0xA69F))
        {
            return TextScript.Cyrillic;
        }

        // Arabic
        if (cp is (>= 0x0600 and <= 0x06FF)
            or (>= 0x0750 and <= 0x077F)
            or (>= 0x08A0 and <= 0x08FF)
            or (>= 0xFB50 and <= 0xFDFF)
            or (>= 0xFE70 and <= 0xFEFF))
        {
            return TextScript.Arabic;
        }

        // Latin (basic + extended + digits)
        if (cp is (>= 0x0041 and <= 0x005A)
            or (>= 0x0061 and <= 0x007A)
            or (>= 0x00C0 and <= 0x024F)
            or (>= 0x1E00 and <= 0x1EFF)
            or (>= 0x0030 and <= 0x0039)
            or (>= 0xFF21 and <= 0xFF3A)
            or (>= 0xFF41 and <= 0xFF5A))
        {
            return TextScript.Latin;
        }

        // ASCII letters already covered; remaining BMP Latin-ish
        if (cp < 0x0250 && char.IsLetter((char)cp))
        {
            return TextScript.Latin;
        }

        return TextScript.Other;
    }

    public static TextScript DetectChar(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length)
        {
            return TextScript.Other;
        }

        return Detect(Rune.GetRuneAt(text, index));
    }

    private static bool IsPunctuationLike(int cp)
    {
        if (cp <= 0x7F)
        {
            return !char.IsLetterOrDigit((char)cp) && !char.IsWhiteSpace((char)cp);
        }

        // General punctuation blocks
        if (cp is (>= 0x2000 and <= 0x206F) or (>= 0x2E00 and <= 0x2E7F) or (>= 0x3000 and <= 0x303F))
        {
            // Exclude some CJK symbols that are more letter-like — still OK as punct for font inherit
            return true;
        }

        try
        {
            return Rune.GetUnicodeCategory(new Rune(cp)) is
                System.Globalization.UnicodeCategory.ConnectorPunctuation or
                System.Globalization.UnicodeCategory.DashPunctuation or
                System.Globalization.UnicodeCategory.OpenPunctuation or
                System.Globalization.UnicodeCategory.ClosePunctuation or
                System.Globalization.UnicodeCategory.InitialQuotePunctuation or
                System.Globalization.UnicodeCategory.FinalQuotePunctuation or
                System.Globalization.UnicodeCategory.OtherPunctuation;
        }
        catch
        {
            return false;
        }
    }
}
