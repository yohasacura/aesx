// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Globalization;

namespace Aesx.Cli;

/// <summary>A command-line mistake; reported with exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

internal enum Command
{
    /// <summary>Decide per file: decrypt if it ends in .aesx, otherwise encrypt.</summary>
    Auto,
    Encrypt,
    Decrypt,
    SelfTest,
    Help,
    Version,
}

internal sealed class Options
{
    public Command Command { get; set; } = Command.Auto;
    public List<string> Inputs { get; } = [];
    public string? Output { get; set; }
    public string? Password { get; set; }
    public int Iterations { get; set; } = AesxFormat.DefaultIterations;
    public bool IterationsSpecified { get; set; }
    public bool Force { get; set; }
    public bool DeleteInput { get; set; }
    public bool Quiet { get; set; }
}

internal static class CommandLine
{
    public static Options Parse(string[] args)
    {
        var o = new Options();
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    o.Command = Command.Help;
                    return o;
                case "-V" or "--version":
                    o.Command = Command.Version;
                    return o;
                case "-f" or "--force":
                    o.Force = true;
                    break;
                case "--delete":
                    o.DeleteInput = true;
                    break;
                case "-q" or "--quiet":
                    o.Quiet = true;
                    break;
                case "-o" or "--output":
                    o.Output = Value(args, ref i, a);
                    if (o.Output.Length == 0) throw new UsageException("the output path must not be empty");
                    break;
                case "-p" or "--password":
                    o.Password = Value(args, ref i, a);
                    break;
                case "-i" or "--iterations":
                    {
                        string v = Value(args, ref i, a);
                        if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                            || n < 1 || n > AesxFormat.MaxIterations)
                        {
                            throw new UsageException($"invalid iteration count '{v}' (allowed: 1..{AesxFormat.MaxIterations})");
                        }
                        o.Iterations = n;
                        o.IterationsSpecified = true;
                        break;
                    }
                case "--":
                    positional.AddRange(args[(i + 1)..]);
                    i = args.Length;
                    break;
                default:
                    if (a.Length > 1 && a[0] == '-' && !File.Exists(a))
                        throw new UsageException($"unknown option '{a}'");
                    positional.Add(a);
                    break;
            }
        }

        // The first word may be a command, unless it is simply the one file we were given.
        if (positional.Count > 0 && !(positional.Count == 1 && File.Exists(positional[0])))
        {
            Command? command = positional[0].ToLowerInvariant() switch
            {
                "e" or "enc" or "encrypt" => Command.Encrypt,
                "d" or "dec" or "decrypt" => Command.Decrypt,
                "selftest" or "self-test" or "test" => Command.SelfTest,
                "help" => Command.Help,
                "version" => Command.Version,
                _ => null,
            };
            if (command is { } c)
            {
                o.Command = c;
                positional.RemoveAt(0);
            }
        }
        o.Inputs.AddRange(positional);
        return o;
    }

    private static string Value(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length) throw new UsageException($"option '{option}' requires a value");
        return args[++i];
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine($"""
            aesx {Program.Version} - offline file encryption (AES-256-GCM, PBKDF2-HMAC-SHA256)

            Usage:
              aesx <file>...                    encrypt; files ending in {AesxFormat.FileExtension} are decrypted instead
              aesx encrypt <file>... [options]  encrypt file(s) to <file>{AesxFormat.FileExtension}
              aesx decrypt <file>... [options]  decrypt file(s) to <file> without {AesxFormat.FileExtension}
              aesx selftest                     run the built-in self-tests

            Options:
              -o, --output <path>      output file, or an existing directory to write into
              -p, --password <pw>      password on the command line (visible to other users
                                       and kept in shell history; prefer the prompt or stdin)
              -i, --iterations <n>     PBKDF2 iterations when encrypting (default {AesxFormat.DefaultIterations})
              -f, --force              overwrite an existing output file
                  --delete             delete the input after the output has been written and,
                                       for encryption, verified
              -q, --quiet              no progress output
              -h, --help               show this help
              -V, --version            show the version

            The password is asked for on the terminal without echo. When stdin is not a
            terminal (a pipe or a file), its first line is used as the password.
            Exit codes: 0 success, 1 failure, 2 usage error, 130 interrupted.
            """);
    }
}
