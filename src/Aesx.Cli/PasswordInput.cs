// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Security.Cryptography;
using System.Text;

namespace Aesx.Cli;

/// <summary>The person closed the password window: nothing was done, and there is nothing to report or wait for.</summary>
internal sealed class PromptCancelledException() : Exception("no password was entered");

internal static class PasswordInput
{
    public const string EnvironmentVariable = "AESX_PASSWORD";

    /// <summary>
    /// Returns the password as bytes; the caller zeroes them when done. Sources, in order:
    /// -p/--password (UTF-8 of the argument), the AESX_PASSWORD environment variable (UTF-8),
    /// piped standard input (the raw bytes of its first line), and finally the person at the
    /// keyboard (UTF-8 of the characters typed, without echo, confirmed when encrypting): on the
    /// terminal, or in a window when Windows Explorer started aesx.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="confirm">Ask twice, because a typo while encrypting would lock the owner out.</param>
    /// <param name="standardInputIsData">Standard input carries the file, so it cannot also carry the password.</param>
    /// <param name="purpose">What the password is for, in a few words, for the password window.</param>
    public static byte[] Obtain(Options options, bool confirm, bool standardInputIsData, string purpose)
    {
        if (options.Password is not null)
        {
            if (options.Password.Length == 0) throw new UsageException("the password must not be empty");
            WarnAboutInvisibleCharacters(options.Password, "the password");
            return Encoding.UTF8.GetBytes(options.Password);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            WarnAboutInvisibleCharacters(fromEnvironment, $"the password in {EnvironmentVariable}");
            return Encoding.UTF8.GetBytes(fromEnvironment);
        }

        if (standardInputIsData)
            throw new UsageException($"standard input carries the data, so the password has to come from {EnvironmentVariable} or -p");

        // Somebody who chose a menu entry in Explorer is at the desktop: there is no pipe to
        // read a password from, whatever standard input happens to be connected to.
        if (!options.FromExplorer && Console.IsInputRedirected)
        {
            byte[] piped = ConsoleSupport.ReadStandardInputLine();
            if (piped.Length == 0)
                throw new InvalidOperationException($"the password must not be empty (standard input is not a terminal, so its first line is used; -p and {EnvironmentVariable} are the alternatives)");
            return piped;
        }

        if (OperatingSystem.IsWindows() && (options.FromExplorer || ConsoleSupport.OwnsItsConsole()))
        {
            char[]? typed = PasswordDialog.Ask(purpose, confirm);
            if (typed is not null) return Encode(typed);
            // No window could be shown (no desktop, for instance): the terminal still works.
        }

        byte[] password = ReadFromTerminal("Password: ");
        if (password.Length == 0)
            throw new InvalidOperationException("the password must not be empty");

        if (confirm)
        {
            byte[] again = ReadFromTerminal("Confirm password: ");
            bool same = CryptographicOperations.FixedTimeEquals(password, again);
            CryptographicOperations.ZeroMemory(again);
            if (!same)
            {
                CryptographicOperations.ZeroMemory(password);
                throw new InvalidOperationException("the passwords do not match");
            }
        }
        return password;
    }

    private static byte[] ReadFromTerminal(string prompt)
    {
        Console.Error.Write(prompt);
        char[] chars = ConsoleSupport.ReadHiddenLine();
        Console.Error.WriteLine();
        return Encode(chars);
    }

    /// <summary>UTF-8 of the characters typed; the characters themselves are wiped.</summary>
    private static byte[] Encode(char[] chars)
    {
        try
        {
            WarnAboutInvisibleCharacters(chars, "the password");
            return Encoding.UTF8.GetBytes(chars);
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    private static void WarnAboutInvisibleCharacters(ReadOnlySpan<char> password, string what)
    {
        IReadOnlyList<string> invisible = TextSafety.InvisibleCharacters(password);
        if (invisible.Count > 0)
            Notices.Say($"warning: {what} contains invisible characters ({string.Join(", ", invisible)}); they count as part of it");
    }
}
