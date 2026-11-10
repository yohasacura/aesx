// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Aesx.Cli;

internal static class ConsoleSupport
{
    /// <summary>Longest line accepted from a pipe or file as a password; anything longer is almost certainly the wrong input.</summary>
    public const int MaxStandardInputLine = 1 << 20;

    /// <summary>
    /// Reads one line from the terminal key by key without echo, into a char array rather than
    /// a string so the caller can wipe it.
    /// </summary>
    public static char[] ReadHiddenLine()
    {
        char[] buffer = new char[64];
        int length = 0;
        while (HiddenLineEditor.Apply(Console.ReadKey(intercept: true), ref buffer, ref length))
        {
        }

        char[] result = buffer.AsSpan(0, length).ToArray();
        Array.Clear(buffer);
        return result;
    }

    /// <summary>
    /// Reads the first line of redirected standard input as raw bytes. No text decoding happens,
    /// so the bytes a shell pipes in are exactly the bytes used, on every platform (the console
    /// code page on Windows would otherwise mangle anything beyond ASCII). The line ending and a
    /// leading UTF-8 byte-order mark are dropped. A file saved by an editor as UTF-16 (byte-order
    /// mark FF FE or FE FF) is recognised and converted to UTF-8, so it matches the same password
    /// typed at a prompt.
    /// </summary>
    public static byte[] ReadStandardInputLine()
    {
        using var stdin = new BufferedStream(Console.OpenStandardInput(), 4096);
        byte[] buffer = new byte[256];
        int n = 0;
        while (true)
        {
            int b = stdin.ReadByte();
            if (b < 0 || b == '\n') break;
            if (n == MaxStandardInputLine)
            {
                Array.Clear(buffer);
                throw new InvalidOperationException("the first line of standard input is longer than 1 MiB; is the right file or pipe connected?");
            }
            if (n == buffer.Length)
            {
                byte[] bigger = new byte[Math.Min(buffer.Length * 2, MaxStandardInputLine)];
                buffer.AsSpan(0, n).CopyTo(bigger);
                Array.Clear(buffer);
                buffer = bigger;
            }
            buffer[n++] = (byte)b;
        }

        try
        {
            if (n >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)))
                return Utf16LineToUtf8(buffer.AsSpan(0, n));

            if (n > 0 && buffer[n - 1] == '\r') n--;
            int start = n >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            return buffer[start..n];
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static byte[] Utf16LineToUtf8(ReadOnlySpan<byte> line)
    {
        Encoding utf16 = line[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
        ReadOnlySpan<byte> body = line[2..];
        body = body[..(body.Length & ~1)]; // the newline's second byte, if the line ended on it, is left over
        char[] chars = new char[utf16.GetCharCount(body)];
        try
        {
            int count = utf16.GetChars(body, chars);
            if (count > 0 && chars[count - 1] == '\r') count--;
            return Encoding.UTF8.GetBytes(chars, 0, count);
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    /// <summary>
    /// True when this process has a console window all to itself, which is what a double-click,
    /// a drag-and-drop or a menu entry in Windows Explorer produces: no shell is waiting behind
    /// the window, and nobody chose to work in a terminal.
    /// </summary>
    public static bool OwnsItsConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected) return false;

        // With a one-element buffer the call returns how many processes share this console;
        // 1 means no shell launched us.
        uint first = 0;
        return GetConsoleProcessList(ref first, 1) == 1;
    }

    /// <summary>
    /// A console window that belongs to this process alone would vanish the instant it exits.
    /// Wait for a key so the result (or the error) can be read.
    /// </summary>
    public static void PauseIfLaunchedFromExplorer()
    {
        if (!OwnsItsConsole()) return;
        try
        {
            Console.Error.Write("Press any key to close...");
            Console.ReadKey(intercept: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // No usable console after all; purely cosmetic either way.
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetConsoleProcessList(ref uint processList, uint processCount);
}

/// <summary>
/// Warnings and errors go to standard error, and into a list as well when somebody will want to
/// show them elsewhere afterwards (see the Explorer menu entries).
/// </summary>
internal static class Notices
{
    public static List<string>? Collector { get; set; }

    public static void Say(string line)
    {
        Console.Error.WriteLine(line);
        Collector?.Add(line);
    }
}

/// <summary>The editing rules of the hidden password prompt, kept free of console calls so they can be tested.</summary>
internal static class HiddenLineEditor
{
    /// <summary>Applies one key; returns false once Enter completes the line.</summary>
    public static bool Apply(ConsoleKeyInfo key, ref char[] buffer, ref int length)
    {
        if (key.Key == ConsoleKey.Enter || key.KeyChar is '\n' or '\r') return false;

        if (key.Key == ConsoleKey.Backspace || key.KeyChar is '\b' or '\u007f')
        {
            if (length > 0)
            {
                length--;
                // Emoji and some CJK characters take two chars (a surrogate pair); erase both,
                // or an invisible half would stay in the password.
                if (length > 0 && char.IsLowSurrogate(buffer[length]) && char.IsHighSurrogate(buffer[length - 1])) length--;
            }
            return true;
        }

        if (key.KeyChar == '\u0015') // Ctrl+U: start over
        {
            length = 0;
            return true;
        }

        // Arrows, function keys and stray control combinations produce nothing visible; adding
        // them to the password would lock its owner out.
        if (key.KeyChar < ' ' || key.KeyChar == '\u007f') return true;

        if (length == buffer.Length)
        {
            char[] bigger = new char[buffer.Length * 2];
            buffer.AsSpan(0, length).CopyTo(bigger);
            Array.Clear(buffer);
            buffer = bigger;
        }
        buffer[length++] = key.KeyChar;
        return true;
    }
}

/// <summary>
/// Turns Ctrl+C, Ctrl+Break, a closing console window and SIGTERM/SIGHUP into a cancellation
/// while a file operation is in progress, so the partial output is removed before exit. When
/// nothing is in progress (for example at the password prompt) the signals keep their default
/// effect and terminate the process immediately; so does a second signal, should the first one
/// not get through.
/// </summary>
internal sealed class InterruptGuard : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly List<PosixSignalRegistration> _registrations = [];

    public InterruptGuard()
    {
        foreach (PosixSignal signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
            }
            catch (PlatformNotSupportedException)
            {
                // Not every signal exists on every platform; the others still work.
            }
        }
    }

    public bool Armed { get; set; }

    public CancellationToken Token => _cancellation.Token;

    /// <summary>Called once the partial output has been dealt with, which releases a waiting signal handler.</summary>
    public void MarkFinished() => _finished.Set();

    private void OnSignal(PosixSignalContext context)
    {
        if (!Armed || _cancellation.IsCancellationRequested) return;
        context.Cancel = true; // we exit ourselves, after the cleanup
        _cancellation.Cancel();
        // Windows ends the process as soon as the handler for a closing console window returns:
        // hold the handler until the main thread has removed the unfinished file.
        _finished.Wait(TimeSpan.FromSeconds(4));
    }

    public void Dispose()
    {
        foreach (PosixSignalRegistration registration in _registrations) registration.Dispose();
        _finished.Set();
        _finished.Dispose();
        _cancellation.Dispose();
    }
}
