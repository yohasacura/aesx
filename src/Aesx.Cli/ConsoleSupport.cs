// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aesx.Cli;

internal static class ConsoleSupport
{
    /// <summary>
    /// Reads one line from the terminal key by key without echo, into a char array rather than
    /// a string so the caller can wipe it.
    /// </summary>
    public static char[] ReadHiddenLine()
    {
        char[] buffer = new char[64];
        int n = 0;
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (n > 0) n--;
                continue;
            }
            if (key.KeyChar == '\0') continue; // arrows, function keys, ...

            if (n == buffer.Length)
            {
                char[] bigger = new char[buffer.Length * 2];
                buffer.AsSpan(0, n).CopyTo(bigger);
                Array.Clear(buffer);
                buffer = bigger;
            }
            buffer[n++] = key.KeyChar;
        }

        char[] result = buffer.AsSpan(0, n).ToArray();
        Array.Clear(buffer);
        return result;
    }

    /// <summary>
    /// Reads the first line of redirected standard input as raw bytes. No text decoding happens,
    /// so the bytes a shell pipes in are exactly the bytes used, on every platform (the console
    /// code page on Windows would otherwise mangle anything beyond ASCII). The line ending and a
    /// leading UTF-8 byte-order mark, which some shells and editors prepend, are dropped.
    /// </summary>
    public static byte[] ReadStandardInputLine()
    {
        using var stdin = new BufferedStream(Console.OpenStandardInput(), 256);
        byte[] buffer = new byte[256];
        int n = 0;
        while (true)
        {
            int b = stdin.ReadByte();
            if (b < 0 || b == '\n') break;

            if (n == buffer.Length)
            {
                byte[] bigger = new byte[buffer.Length * 2];
                buffer.AsSpan(0, n).CopyTo(bigger);
                Array.Clear(buffer);
                buffer = bigger;
            }
            buffer[n++] = (byte)b;
        }

        if (n > 0 && buffer[n - 1] == '\r') n--;
        int start = n >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        byte[] result = buffer[start..n];
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
/// Turns Ctrl+C, Ctrl+Break, a closing console window and SIGTERM/SIGHUP into a cancellation
/// while a file operation is in progress, so the partial output is removed before exit. When
/// nothing is in progress (for example at the password prompt) the signals keep their default
/// effect and terminate the process immediately.
/// </summary>
internal sealed class InterruptGuard : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
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

    private void OnSignal(PosixSignalContext context)
    {
        if (!Armed) return;
        context.Cancel = true; // we exit ourselves, after the cleanup
        _cancellation.Cancel();
    }

    public void Dispose()
    {
        foreach (PosixSignalRegistration registration in _registrations) registration.Dispose();
        _cancellation.Dispose();
    }
}
