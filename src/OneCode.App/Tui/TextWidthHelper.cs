using System.Text;

namespace OneCode.App.Tui;

/// <summary>
/// Calculates the display width of text in a monospace terminal, accounting
/// for wide characters (CJK, fullwidth, emoji) that occupy two columns.
/// Also provides width-aware word-wrapping and truncation.
/// </summary>
public static class TextWidthHelper
{
    /// <summary>
    /// Returns the number of terminal columns the given character occupies.
    /// Wide characters (CJK, fullwidth, emoji) return 2; combining marks return 0; others return 1.
    /// </summary>
    public static int GetCharDisplayWidth(char c)
    {
        // Combining marks — zero width
        if (c >= '\u0300' && c <= '\u036F') return 0;
        if (c >= '\u1AB0' && c <= '\u1AFF') return 0;
        if (c >= '\u1DC0' && c <= '\u1DFF') return 0;
        if (c >= '\u20D0' && c <= '\u20FF') return 0;
        if (c >= '\uFE20' && c <= '\uFE2F') return 0;

        // CJK Unified Ideographs and extensions
        if (c >= '\u4E00' && c <= '\u9FFF') return 2;  // CJK Unified
        if (c >= '\u3400' && c <= '\u4DBF') return 2;  // CJK Extension A
        if (c >= '\uF900' && c <= '\uFAFF') return 2;  // CJK Compatibility Ideographs
        // CJK Extension B (U+20000-U+2A6DF) are surrogate pairs, handled separately above

        // CJK Radicals and punctuation
        if (c >= '\u2E80' && c <= '\u2EFF') return 2;  // CJK Radicals Supplement
        if (c >= '\u2F00' && c <= '\u2FDF') return 2;  // Kangxi Radicals
        if (c >= '\u3000' && c <= '\u303F') return 2;  // CJK Symbols and Punctuation
        if (c >= '\u31C0' && c <= '\u31EF') return 2;  // CJK Strokes

        // Hiragana and Katakana
        if (c >= '\u3040' && c <= '\u309F') return 2;  // Hiragana
        if (c >= '\u30A0' && c <= '\u30FF') return 2;  // Katakana
        if (c >= '\u31F0' && c <= '\u31FF') return 2;  // Katakana Phonetic Extensions

        // Fullwidth forms
        if (c >= '\uFF01' && c <= '\uFF60') return 2;  // Fullwidth punctuation/letters
        if (c >= '\uFFE0' && c <= '\uFFE6') return 2;  // Fullwidth signs

        // Hangul Syllables (Korean)
        if (c >= '\uAC00' && c <= '\uD7AF') return 2;
        if (c >= '\u1100' && c <= '\u11FF') return 2;  // Hangul Jamo
        if (c >= '\uA960' && c <= '\uA97F') return 2;  // Hangul Jamo Extended-A

        // Misc symbols that are typically wide in terminals
        if (c >= '\u2600' && c <= '\u27BF') return 2;  // Misc symbols & dingbats (includes many emoji)

        return 1;
    }

    /// <summary>
    /// Calculates the total display width of a string in terminal columns.
    /// Handles surrogate pairs (emoji above U+FFFF) as width 2.
    /// </summary>
    public static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var width = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            // Surrogate pair — emoji and CJK Extension B
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                width += 2;
                i++; // skip low surrogate
                continue;
            }
            width += GetCharDisplayWidth(c);
        }
        return width;
    }

    /// <summary>
    /// Truncates text to fit within the given display width, appending an
    /// ellipsis if truncation occurs.
    /// </summary>
    public static string TruncateByWidth(string text, int maxDisplayWidth)
    {
        if (maxDisplayWidth <= 0) return "";
        if (string.IsNullOrEmpty(text)) return text;

        var width = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            int charWidth;

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                charWidth = 2;
                // Check if adding this pair would overflow (leave room for ellipsis)
                if (width + charWidth > maxDisplayWidth - 1)
                {
                    return maxDisplayWidth > 1
                        ? text[..i] + "\u2026"
                        : text[..i];
                }
                width += charWidth;
                i++; // skip low surrogate
                continue;
            }

            charWidth = GetCharDisplayWidth(c);
            if (width + charWidth > maxDisplayWidth - 1)
            {
                return maxDisplayWidth > 1
                    ? text[..i] + "\u2026"
                    : text[..i];
            }
            width += charWidth;
        }

        return text;
    }

    /// <summary>
    /// Word-wraps text to fit within maxWidth terminal columns, using display
    /// width (not character count) for measurement. Handles:
    /// - Explicit newlines (\n) preserved as line breaks
    /// - Spaces as soft break opportunities
    /// - CJK text without spaces: breaks at any character boundary when line is full
    /// - Wide characters (CJK/emoji) correctly measured as 2 columns
    /// </summary>
    public static List<string> WordWrapByWidth(string text, int maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        if (maxWidth <= 0) maxWidth = 40;

        var paragraphs = text.Replace("\r\n", "\n").Split('\n');

        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrEmpty(paragraph))
            {
                result.Add("");
                continue;
            }

            var currentLine = new StringBuilder();
            var currentWidth = 0;
            var lastSpaceIdx = -1;  // index in currentLine where last space was appended
            var lastSpaceWidth = 0; // width at the point of last space

            var i = 0;
            while (i < paragraph.Length)
            {
                char c = paragraph[i];

                // Handle surrogate pairs
                if (char.IsHighSurrogate(c) && i + 1 < paragraph.Length && char.IsLowSurrogate(paragraph[i + 1]))
                {
                    int charWidth = 2;
                    string charStr = paragraph.Substring(i, 2);

                    if (currentWidth + charWidth > maxWidth)
                    {
                        // Line is full — flush current line
                        result.Add(currentLine.ToString().TrimEnd());
                        currentLine.Clear();
                        currentWidth = 0;
                        lastSpaceIdx = -1;
                        lastSpaceWidth = 0;
                    }

                    currentLine.Append(charStr);
                    currentWidth += charWidth;
                    i += 2;
                    continue;
                }

                int cw = GetCharDisplayWidth(c);

                if (c == ' ')
                {
                    // Record break opportunity
                    lastSpaceIdx = currentLine.Length;
                    lastSpaceWidth = currentWidth;

                    if (currentWidth + cw > maxWidth)
                    {
                        // Space itself overflows — flush
                        result.Add(currentLine.ToString().TrimEnd());
                        currentLine.Clear();
                        currentWidth = 0;
                        lastSpaceIdx = -1;
                        lastSpaceWidth = 0;
                        i++;
                        continue;
                    }

                    currentLine.Append(c);
                    currentWidth += cw;
                    i++;
                    continue;
                }

                // Regular character or CJK character
                if (currentWidth + cw > maxWidth)
                {
                    // Need to break
                    if (lastSpaceIdx >= 0 && cw == 1)
                    {
                        // Break at last space (for ASCII/mixed text)
                        var lineContent = currentLine.ToString(0, lastSpaceIdx).TrimEnd();
                        result.Add(lineContent);

                        // Continue from after the space
                        var remainder = currentLine.ToString(lastSpaceIdx + 1, currentLine.Length - lastSpaceIdx - 1);
                        currentLine.Clear();
                        currentLine.Append(remainder);
                        currentWidth = currentWidth - lastSpaceWidth - 1; // -1 for the space itself
                        lastSpaceIdx = -1;
                        lastSpaceWidth = 0;

                        // Now try to add current character
                        if (currentWidth + cw > maxWidth)
                        {
                            // Still doesn't fit — flush and start fresh
                            if (currentLine.Length > 0)
                                result.Add(currentLine.ToString().TrimEnd());
                            currentLine.Clear();
                            currentWidth = 0;
                        }
                    }
                    else
                    {
                        // No space to break at, or CJK char — break at current position
                        if (currentLine.Length > 0)
                            result.Add(currentLine.ToString().TrimEnd());
                        currentLine.Clear();
                        currentWidth = 0;
                        lastSpaceIdx = -1;
                        lastSpaceWidth = 0;
                    }
                }

                currentLine.Append(c);
                currentWidth += cw;
                i++;
            }

            if (currentLine.Length > 0)
                result.Add(currentLine.ToString().TrimEnd());
        }

        return result;
    }
}
