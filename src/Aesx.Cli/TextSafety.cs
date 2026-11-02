// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Globalization;
using System.Text;

namespace Aesx.Cli;

/// <summary>Keeps untrusted text (file names, mostly) from doing anything on the terminal.</summary>
internal static class TextSafety
{
    /// <summary>
    /// Returns the text with control characters and bidirectional-override characters shown as
    /// <c>\u{XXXX}</c> escapes. A file name can otherwise carry escape sequences that restyle or
    /// command the terminal, or right-to-left overrides that make "report‮txt.exe" display
    /// as "reportexe.txt". Hebrew, Arabic, Chinese, emoji and every other ordinary character are
    /// left exactly as they are.
    /// </summary>
    public static string Display(string text)
    {
        StringBuilder? sb = null;
        int copied = 0;
        int index = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            int length = r.Utf16SequenceLength;
            if (IsDisplayHazard(r))
            {
                sb ??= new StringBuilder(text.Length + 16);
                sb.Append(text, copied, index - copied);
                sb.Append(CultureInfo.InvariantCulture, $"\\u{{{r.Value:X4}}}");
                copied = index + length;
            }
            index += length;
        }
        if (sb is null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static bool IsDisplayHazard(Rune r)
        => Rune.GetUnicodeCategory(r) == UnicodeCategory.Control
           || r.Value is >= 0x202A and <= 0x202E   // LRE, RLE, PDF, LRO, RLO
           || r.Value is >= 0x2066 and <= 0x2069   // LRI, RLI, FSI, PDI
           || r.Value is 0x2028 or 0x2029;         // line and paragraph separators

    /// <summary>
    /// Code points of characters in the text that are invisible or only affect layout (Unicode
    /// categories Format and Control: zero-width joiners, right-to-left marks, byte-order marks,
    /// soft hyphens, stray control keys), as "U+XXXX", each once. Such characters are legitimate
    /// in a password, but the person typing usually cannot see them and may not be able to type
    /// them again, so the prompt warns about them.
    /// </summary>
    public static IReadOnlyList<string> InvisibleCharacters(ReadOnlySpan<char> text)
    {
        List<string>? found = null;
        foreach (Rune r in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(r);
            if (category is not (UnicodeCategory.Format or UnicodeCategory.Control)) continue;
            string label = string.Create(CultureInfo.InvariantCulture, $"U+{r.Value:X4}");
            found ??= [];
            if (!found.Contains(label)) found.Add(label);
        }
        return found ?? [];
    }
}
