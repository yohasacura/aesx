// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Aesx.Cli;

/// <summary>
/// The two windows aesx shows when the Finder started it: one that asks for the password, and
/// one that says what was left out or went wrong. macOS draws both. There is no user-interface
/// code here: aesx runs <c>/usr/bin/osascript</c>, which every Mac has, with a few lines of
/// AppleScript, and reads the answer from that program's standard output.
/// </summary>
/// <remarks>
/// The password travels through a pipe from that process to this one and nowhere else: it is in
/// no command line and in no file. The scripts are fixed text. What varies (the name of a file,
/// an error message) is handed over as an argument and never spliced into a script, so a file
/// name cannot turn into code.
/// </remarks>
/// <param name="osascript">The program that runs the scripts; a parameter so that tests can put a stand-in there.</param>
internal sealed class FinderDialogs(string osascript)
{
    public const string Title = "aesx";
    public const string PasswordLabel = "Password:";
    public const string ConfirmLabel = "The same password once more:";
    public const string EmptyComplaint = "The password must not be empty.";
    public const string MismatchComplaint = "The two passwords were not the same.";

    /// <summary>The first character of what the asking script prints: a password follows, or the dialog was cancelled.</summary>
    private const byte Answered = (byte)'+';
    private const byte Cancelled = (byte)'-';

    /// <summary>A password is typed by a person; anything near this size is something else.</summary>
    private const int MaxAnswer = 1 << 20;

    // Cancel is error -128 to AppleScript. It is turned into an answer of its own here, so that
    // "the person said no" and "no dialog could be shown" cannot be mistaken for each other, and
    // nothing depends on the wording of an error message in whatever language the Mac speaks.
    private const string AskScript = """
        on run argv
            try
                set answer to display dialog (item 1 of argv) default answer "" with hidden answer with title "aesx" buttons {"Cancel", "OK"} default button "OK" cancel button "Cancel"
            on error explanation number code
                if code is -128 then return "-"
                error explanation number code
            end try
            return "+" & text returned of answer
        end run
        """;

    private const string TellScript = """
        on run argv
            if (item 2 of argv) is "failed" then
                display dialog (item 1 of argv) with title "aesx" buttons {"OK"} default button "OK" with icon stop
            else
                display dialog (item 1 of argv) with title "aesx" buttons {"OK"} default button "OK" with icon caution
            end if
        end run
        """;

    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The dialogs of the Mac this is running on.</summary>
    public static FinderDialogs OfThisSystem { get; } = new("/usr/bin/osascript");

    /// <summary>Asks for the password, twice when <paramref name="confirm"/> is set, until it is usable.</summary>
    /// <param name="purpose">What the password is for, shown above the field ("Encrypt report.pdf").</param>
    /// <param name="confirm">Ask a second time and compare, because a typo while encrypting would lock the owner out.</param>
    /// <returns>The characters typed, for the caller to wipe.</returns>
    /// <exception cref="PromptCancelledException">The person pressed Cancel.</exception>
    /// <exception cref="InvalidOperationException">No dialog could be shown.</exception>
    public char[] AskPassword(string purpose, bool confirm)
    {
        string? complaint = null;
        while (true)
        {
            char[] password = Ask(complaint, purpose, PasswordLabel);
            if (password.Length == 0)
            {
                complaint = EmptyComplaint;
                continue;
            }
            if (!confirm) return password;

            char[] again;
            try
            {
                again = Ask(complaint: null, purpose, ConfirmLabel);
            }
            catch
            {
                Array.Clear(password);
                throw;
            }

            // Compared here, byte for byte: AppleScript's own comparison ignores case.
            bool same = CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(password.AsSpan()), MemoryMarshal.AsBytes(again.AsSpan()));
            Array.Clear(again);
            if (same) return password;
            Array.Clear(password);
            complaint = MismatchComplaint;
        }
    }

    /// <summary>Shows what a run started from the Finder has to report. Failing to show it is not an error: the lines are on standard error as well.</summary>
    public void Tell(IReadOnlyList<string> lines, bool failed)
    {
        try
        {
            _ = Run(TellScript, Notices.Digest(lines), failed ? "failed" : "warning");
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>One dialog with one hidden field.</summary>
    private char[] Ask(string? complaint, string purpose, string label)
    {
        string text = complaint is null ? $"{purpose}\n\n{label}" : $"{complaint}\n\n{purpose}\n\n{label}";
        (int exitCode, byte[] output, string error) = Run(AskScript, text);
        try
        {
            if (exitCode == 0 && output is [Cancelled, ..]) throw new PromptCancelledException();
            if (exitCode != 0 || output is not [Answered, ..])
                throw new InvalidOperationException($"no dialog could be shown to ask for the password ({Reason(exitCode, error)})");

            // osascript ends what it prints with one line feed, which is not part of the answer.
            ReadOnlySpan<byte> typed = output.AsSpan(1);
            if (typed is [.., (byte)'\n']) typed = typed[..^1];
            try
            {
                char[] password = new char[s_strictUtf8.GetCharCount(typed)];
                s_strictUtf8.GetChars(typed, password);
                return password;
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidOperationException("the password dialog returned text that is not valid UTF-8", ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    /// <summary>
    /// Runs one script. It goes in through standard input, so nothing but the dialog's text is on
    /// the command line, and what comes out is read as bytes into a buffer that can be wiped.
    /// </summary>
    private (int ExitCode, byte[] Output, string Error) Run(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo(osascript)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-"); // the script is on standard input; what follows are its arguments
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException($"cannot start {TextSafety.Display(osascript)}");
            Task<string> error = process.StandardError.ReadToEndAsync();
            using (Stream input = process.StandardInput.BaseStream)
            {
                input.Write(Encoding.ASCII.GetBytes(script.ReplaceLineEndings("\n")));
            }
            byte[] output = ReadToEnd(process.StandardOutput.BaseStream);
            process.WaitForExit();
            return (process.ExitCode, output, error.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            throw new InvalidOperationException($"cannot run {TextSafety.Display(osascript)}: {ex.Message}", ex);
        }
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        byte[] buffer = new byte[256];
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length >= MaxAnswer) throw new InvalidOperationException("the password dialog returned more than 1 MiB");
                    byte[] bigger = new byte[buffer.Length * 2];
                    buffer.AsSpan(0, length).CopyTo(bigger);
                    CryptographicOperations.ZeroMemory(buffer);
                    buffer = bigger;
                }
                int read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0) return buffer[..length];
                length += read;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>The last thing the program said about why it failed, or its exit code if it said nothing.</summary>
    private static string Reason(int exitCode, string error)
    {
        string last = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return last.Length > 0 ? TextSafety.Display(last) : $"osascript ended with code {exitCode}";
    }
}
