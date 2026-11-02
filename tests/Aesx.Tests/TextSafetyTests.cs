// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using Aesx.Cli;

namespace Aesx.Tests;

public class TextSafetyTests
{
    [Theory]
    [InlineData("report‮txt.exe", "report\\u{202E}txt.exe")]                     // right-to-left override spoofing the extension
    [InlineData("evil\u001B]0;owned\u0007.txt", "evil\\u{001B}]0;owned\\u{0007}.txt")] // terminal title / command sequence
    [InlineData("iso⁦late⁩.txt", "iso\\u{2066}late\\u{2069}.txt")]           // bidi isolates
    [InlineData("tab\there", "tab\\u{0009}here")]
    [InlineData("del\u007Fete", "del\\u{007F}ete")]
    public void EscapesCharactersThatCanRestyleOrSpoofTheTerminal(string raw, string shown)
        => Assert.Equal(shown, TextSafety.Display(raw));

    [Fact]
    public void EscapesUnicodeLineAndParagraphSeparators()
    {
        // Built at run time: the C# compiler refuses these two code points inside a literal.
        string raw = "line" + char.ConvertFromUtf32(0x2028) + "break" + char.ConvertFromUtf32(0x2029) + "end";
        Assert.Equal("line\\u{2028}break\\u{2029}end", TextSafety.Display(raw));
    }

    [Theory]
    [InlineData("דוח 2025.pdf")]
    [InlineData("报告 最终版.docx")]
    [InlineData("日本語のファイル名.txt")]
    [InlineData("تقرير مالي.txt")]
    [InlineData("family 👨‍👩‍👧 photo.jpg")] // zero-width joiners inside an emoji sequence are not a hazard
    [InlineData("café (é).txt")]
    [InlineData("ümlaut ñ ß ø.txt")]
    [InlineData("plain-ascii_name.txt")]
    public void LeavesOrdinaryTextInAnyScriptUntouched(string name)
        => Assert.Same(name, TextSafety.Display(name));

    [Theory]
    [InlineData("pass​word‏﻿", "U+200B,U+200F,U+FEFF")]
    [InlineData("pa\u0007ss\u0007", "U+0007")]
    [InlineData("soft­hyphen", "U+00AD")]
    [InlineData("👨‍👩‍👧", "U+200D")]
    [InlineData("סיסמה 密码 🔐 pässword", "")]
    public void ListsInvisibleCharactersOnce(string text, string expected)
        => Assert.Equal(expected, string.Join(",", TextSafety.InvisibleCharacters(text)));

    private static ConsoleKeyInfo Key(char c) => new(c, c == '\r' ? ConsoleKey.Enter : (ConsoleKey)0xE7, false, false, false);
    private static ConsoleKeyInfo Special(ConsoleKey key) => new('\0', key, false, false, false);

    private static string Type(params ConsoleKeyInfo[] keys)
    {
        char[] buffer = new char[4];
        int length = 0;
        foreach (ConsoleKeyInfo key in keys)
        {
            if (!HiddenLineEditor.Apply(key, ref buffer, ref length)) break;
        }
        return new string(buffer, 0, length);
    }

    [Fact]
    public void BackspaceRemovesAWholeEmojiNotHalfOfIt()
    {
        // 🔐 is the surrogate pair D83D DD10; a lone half would silently corrupt the password.
        Assert.Equal("ab", Type(Key('a'), Key('b'), Key('\uD83D'), Key('\uDD10'), Special(ConsoleKey.Backspace)));
        Assert.Equal("a🔐", Type(Key('a'), Key('\uD83D'), Key('\uDD10'), Key('c'), Special(ConsoleKey.Backspace)));
    }

    [Fact]
    public void BackspaceOnAnEmptyLineAndDeleteKeysAreHarmless()
    {
        Assert.Equal("", Type(Special(ConsoleKey.Backspace), Key('\u007F'), Key('\b')));
        Assert.Equal("a", Type(Key('a'), Key('b'), Key('\u007F')));
    }

    [Fact]
    public void ControlKeysAreIgnoredAndCtrlUStartsOver()
    {
        Assert.Equal("ab", Type(Key('a'), Special(ConsoleKey.LeftArrow), Key('\u001B'), Key('\t'), Key('b')));
        Assert.Equal("z", Type(Key('a'), Key('b'), Key('\u0015'), Key('z')));
    }

    [Fact]
    public void EnterEndsTheLineAndNothingAfterItCounts()
    {
        Assert.Equal("ab", Type(Key('a'), Key('b'), Special(ConsoleKey.Enter), Key('c')));
        Assert.Equal("ab", Type(Key('a'), Key('b'), Key('\r'), Key('c')));
    }

    [Fact]
    public void GrowsPastTheInitialBufferWithoutLosingCharacters()
    {
        string typed = Type([.. Enumerable.Range(0, 500).Select(i => Key((char)('a' + i % 26)))]);
        Assert.Equal(500, typed.Length);
        Assert.Equal(string.Concat(Enumerable.Range(0, 500).Select(i => (char)('a' + i % 26))), typed);
    }
}
