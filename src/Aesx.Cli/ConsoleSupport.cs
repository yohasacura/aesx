// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aesx.Cli;

internal static class ConsoleSupport
{
    /// <summary>
    /// Reads one line into a char array rather than a string, so the caller can wipe it.
    /// When <paramref name="hidden"/> is set the keys come from the terminal without echo;
    /// otherwise the characters come from (redirected) standard input.
    /// </summary>
    public static char[] ReadLineChars(bool hidden)
    {
        char[] buffer = new char[64];
        int n = 0;
        while (true)
        {
            char ch;
            if (hidden)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (n > 0) n--;
                    continue;
                }
                if (key.KeyChar == '\0') continue; // arrows, function keys, ...
                ch = key.KeyChar;
            }
            else
            {
                int c = Console.In.Read();
                if (c < 0 || c == '\n') break;
                if (c == '\r') continue;
                ch = (char)c;
            }

            if (n == buffer.Length)
            {
                char[] bigger = new char[buffer.Length * 2];
                buffer.AsSpan(0, n).CopyTo(bigger);
                Array.Clear(buffer);
                buffer = bigger;
            }
            buffer[n++] = ch;
        }

        char[] result = buffer.AsSpan(0, n).ToArray();
        Array.Clear(buffer);
        return result;
    }

    /// <summary>
    /// When started by double-click or drag-and-drop on Windows, the console window belongs to
    /// this process alone and would vanish the instant it exits. Wait for a key so the result
    /// (or the error) can be read.
    /// </summary>
    public static void PauseIfLaunchedFromExplorer()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected) return;
        try
        {
            // With a one-element buffer the call returns how many processes share this console;
            // 1 means no shell launched us.
            uint first = 0;
            if (GetConsoleProcessList(ref first, 1) == 1)
            {
                Console.Error.Write("Press any key to close...");
                Console.ReadKey(intercept: true);
            }
        }
        catch (InvalidOperationException)
        {
            // No usable console after all; purely cosmetic either way.
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetConsoleProcessList(ref uint processList, uint processCount);
}
