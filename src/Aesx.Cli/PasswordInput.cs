// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Security.Cryptography;
using System.Text;

namespace Aesx.Cli;

internal static class PasswordInput
{
    public const string EnvironmentVariable = "AESX_PASSWORD";

    /// <summary>
    /// Returns the password as bytes; the caller zeroes them when done. Sources, in order:
    /// -p/--password (UTF-8 of the argument), the AESX_PASSWORD environment variable (UTF-8),
    /// piped standard input (the raw bytes of its first line), and finally the terminal
    /// (UTF-8 of the characters typed, without echo, confirmed when encrypting).
    /// </summary>
    public static byte[] Obtain(Options options, bool confirm)
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

        if (Console.IsInputRedirected)
        {
            byte[] piped = ConsoleSupport.ReadStandardInputLine();
            if (piped.Length == 0)
                throw new InvalidOperationException($"the password must not be empty (standard input is not a terminal, so its first line is used; -p and {EnvironmentVariable} are the alternatives)");
            return piped;
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
            Console.Error.WriteLine($"warning: {what} contains invisible characters ({string.Join(", ", invisible)}); they count as part of it");
    }
}
