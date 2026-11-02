// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Security.Cryptography;
using System.Text;

namespace Aesx.Cli;

internal static class PasswordInput
{
    /// <summary>
    /// Returns the password as bytes; the caller zeroes them when done.
    /// Precedence: -p/--password (UTF-8 of the argument), then piped standard input (the raw
    /// bytes of its first line), then the terminal (UTF-8 of the characters typed, no echo).
    /// </summary>
    public static byte[] Obtain(Options options, bool confirm)
    {
        if (options.Password is not null)
        {
            if (options.Password.Length == 0) throw new UsageException("the password must not be empty");
            return Encoding.UTF8.GetBytes(options.Password);
        }

        bool interactive = !Console.IsInputRedirected;
        byte[] password = interactive ? ReadFromTerminal("Password: ") : ConsoleSupport.ReadStandardInputLine();
        if (password.Length == 0)
            throw new InvalidOperationException("the password must not be empty");

        if (confirm && interactive)
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
            return Encoding.UTF8.GetBytes(chars);
        }
        finally
        {
            Array.Clear(chars);
        }
    }
}
