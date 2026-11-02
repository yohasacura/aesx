// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Security.Cryptography;
using System.Text;

namespace Aesx.Cli;

internal static class PasswordInput
{
    /// <summary>
    /// Returns the password as UTF-8 bytes; the caller zeroes them when done.
    /// Precedence: -p/--password, then piped standard input, then the terminal.
    /// </summary>
    public static byte[] Obtain(Options options, bool confirm)
    {
        if (options.Password is not null)
        {
            if (options.Password.Length == 0) throw new UsageException("the password must not be empty");
            return Encoding.UTF8.GetBytes(options.Password);
        }

        bool interactive = !Console.IsInputRedirected;
        byte[] password = Read(interactive ? "Password: " : null);
        if (password.Length == 0)
            throw new InvalidOperationException("the password must not be empty");

        if (confirm && interactive)
        {
            byte[] again = Read("Confirm password: ");
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

    /// <summary>With a prompt, reads from the terminal without echo; without one, reads a line of standard input.</summary>
    private static byte[] Read(string? prompt)
    {
        char[] chars;
        if (prompt is null)
        {
            chars = ConsoleSupport.ReadLineChars(hidden: false);
        }
        else
        {
            Console.Error.Write(prompt);
            chars = ConsoleSupport.ReadLineChars(hidden: true);
            Console.Error.WriteLine();
        }
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
