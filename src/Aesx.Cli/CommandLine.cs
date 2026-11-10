// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Globalization;

namespace Aesx.Cli;

/// <summary>A command-line mistake; reported with exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

internal enum Command
{
    /// <summary>Decide per input: folders are encrypted, names ending in .aesx are decrypted, everything else is encrypted.</summary>
    Auto,
    Encrypt,
    Decrypt,

    /// <summary>Decrypt and discard: proves that the file is intact and the password right.</summary>
    Verify,

    /// <summary>Print what the header says; no password involved.</summary>
    Info,

    /// <summary>Add aesx to the right-click menus of the current user: Windows Explorer, or the Finder on macOS.</summary>
    Register,
    Unregister,

    /// <summary>Started by Windows for one of those menu entries: the selection comes from Explorer, not from the command line.</summary>
    Explorer,
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

    /// <summary>
    /// The inputs were selected in Windows Explorer or in the Finder, so the person is at a
    /// desktop, not at a terminal: the password is asked for in a window, and so is said
    /// whatever needs saying afterwards.
    /// </summary>
    public bool FromDesktop { get; set; }
}

internal static class CommandLine
{
    /// <summary>As an input: read the data from standard input. As <c>-o</c>: write it to standard output.</summary>
    public const string StandardStream = "-";

    /// <summary>
    /// What the Finder entries on macOS put in front of everything else. There is no terminal
    /// behind a menu, so the password is asked for in a dialog and problems are reported in one.
    /// </summary>
    public const string FinderSwitch = "--finder";

    public static Options Parse(string[] args)
    {
        var o = new Options();

        // The one argument Windows adds when it starts the program behind a menu entry: the
        // switch itself and nothing else, so that a file called Embedding stays a file.
        if (OperatingSystem.IsWindows() && args is [['-' or '/', .. var name] only]
            && name.Equals("Embedding", StringComparison.OrdinalIgnoreCase) && !File.Exists(only))
        {
            o.Command = Command.Explorer;
            return o;
        }

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
                case FinderSwitch when OperatingSystem.IsMacOS():
                    o.FromDesktop = true;
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
                "verify" => Command.Verify,
                "info" => Command.Info,
                "register" => Command.Register,
                "unregister" => Command.Unregister,
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

    /// <summary>
    /// Refuses combinations that cannot mean anything, before a password is asked for. What
    /// depends on the file system (does the output exist, is the input a folder) is checked
    /// later, input by input.
    /// </summary>
    public static void CheckCombinations(Options o, IReadOnlyList<string> inputs)
    {
        string name = o.Command.ToString().ToLowerInvariant();
        if (o.Command is Command.Verify or Command.Info)
        {
            if (o.Output is not null) throw new UsageException($"-o/--output does not apply to '{name}', which writes nothing");
            if (o.DeleteInput) throw new UsageException($"--delete does not apply to '{name}'");
            if (o.Force) throw new UsageException($"-f/--force does not apply to '{name}', which writes nothing");
            if (o.IterationsSpecified) throw new UsageException($"-i/--iterations does not apply to '{name}' (the count is stored in the file)");
        }
        if (o.Command == Command.Info && o.Password is not null)
            throw new UsageException("'info' reads only the header and takes no password");

        bool toStandardOutput = o.Output == StandardStream;
        if (inputs.Contains(StandardStream))
        {
            if (inputs.Count > 1)
                throw new UsageException("- (standard input) cannot be combined with other inputs");
            switch (o.Command)
            {
                case Command.Auto:
                    throw new UsageException("with - (standard input) say what to do: 'aesx encrypt - -o <file>' or 'aesx decrypt - -o <file>'");
                case Command.Info:
                    throw new UsageException("'info' needs a file: standard input has no length to report");
                case Command.Encrypt or Command.Decrypt when o.Output is null:
                    throw new UsageException("data read from standard input needs -o <file>, or -o - for standard output");
                default:
                    break;
            }
            if (o.DeleteInput)
                throw new UsageException("--delete cannot be combined with - (there is no input file to delete)");
            if (!Console.IsInputRedirected)
                throw new UsageException("- reads the data from standard input, but nothing is piped or redirected into it");
        }

        if (inputs.Count > 1 && o.Output is not null && (toStandardOutput || !Directory.Exists(o.Output)))
            throw new UsageException("with several inputs, -o/--output must name an existing directory");
        if (toStandardOutput && o.DeleteInput)
            throw new UsageException("--delete cannot be combined with -o - (the result cannot be verified or kept)");
    }

    /// <summary>
    /// Expands <c>*</c> and <c>?</c> in file names on Windows, where the shell passes them on
    /// untouched and neither can occur in a real name. Elsewhere the shell has already expanded
    /// them, and one that arrives here was quoted on purpose, so nothing is done. Matches are
    /// files only, sorted, without hidden and system files, like <c>dir</c> and <c>copy</c>.
    /// </summary>
    public static List<string> ExpandWildcards(IReadOnlyList<string> inputs)
    {
        var expanded = new List<string>(inputs.Count);
        foreach (string input in inputs)
        {
            if (!OperatingSystem.IsWindows() || !HasWildcard(input))
            {
                expanded.Add(input);
                continue;
            }

            string pattern = Path.GetFileName(input);
            string directory = input[..^pattern.Length];
            if (!HasWildcard(pattern) || HasWildcard(WithoutDevicePrefix(directory)))
                throw new UsageException($"wildcards are only expanded in the file name, not in the folders before it: {TextSafety.Display(input)}");

            var options = new EnumerationOptions
            {
                MatchType = MatchType.Win32,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
            };
            string[] matches;
            try
            {
                matches = Directory.GetFiles(directory.Length == 0 ? "." : directory, pattern, options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new InvalidOperationException($"cannot list the files for '{TextSafety.Display(input)}': {ex.Message}", ex);
            }
            if (matches.Length == 0)
                throw new InvalidOperationException($"no files match '{TextSafety.Display(input)}'");

            // Keep the names the way they were typed: relative stays relative.
            Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
            foreach (string match in matches) expanded.Add(directory + Path.GetFileName(match));
        }
        return expanded;
    }

    private static bool HasWildcard(ReadOnlySpan<char> text) => text.IndexOfAny('*', '?') >= 0;

    /// <summary>The question mark in <c>\\?\C:\...</c> is part of the prefix, not a wildcard.</summary>
    private static ReadOnlySpan<char> WithoutDevicePrefix(ReadOnlySpan<char> path)
        => path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") ? path[4..] : path;

    public static void PrintUsage(TextWriter writer)
    {
        string ext = AesxFormat.FileExtension;

        // Only a Mac has the switch, so only a Mac's help mentions it.
        string finder = OperatingSystem.IsMacOS()
            ? $"""

                    {FinderSwitch}             ask for the password in a dialog and report problems
                                         in one, as the Finder entries do
              """
            : "";
        writer.WriteLine($"""
            aesx {Program.Version} - offline file encryption (AES-256-GCM, PBKDF2-HMAC-SHA256)

            Usage:
              aesx <path>...                    encrypt; names ending in {ext} are decrypted instead
              aesx encrypt <path>... [options]  files become <file>{ext}, folders <folder>{FolderArchive.Extension}
              aesx decrypt <file>... [options]  back to the name without {ext}; a {FolderArchive.Extension}
                                                file becomes the folder again
              aesx verify <file>...             check that files decrypt, without writing anything
              aesx info <file>...               show what the header says (no password needed)
              aesx register | unregister        add aesx to the right-click menu of Windows Explorer
                                                or of the Finder on macOS, or remove it again
              aesx selftest                     run the built-in self-tests

            Options:
              -o, --output <path>      output file, an existing directory to write into, or "-"
                                       for standard output (one input only)
              -p, --password <pw>      password on the command line (visible to other users
                                       and kept in shell history; prefer the prompt or stdin)
              -i, --iterations <n>     PBKDF2 iterations when encrypting (default {AesxFormat.DefaultIterations})
              -f, --force              overwrite an existing output file
                  --delete             delete the input file after the output has been written
                                       and, for encryption, verified
              -q, --quiet              no progress output
              -h, --help               show this help
              -V, --version            show the version{finder}

            A single "-" as the input reads the data from standard input (with -o).
            Password sources, first one found wins: -p, the {PasswordInput.EnvironmentVariable} environment
            variable, the first line of stdin when it is not a terminal (a pipe or a file),
            otherwise a prompt on the terminal without echo.
            Exit codes: 0 success, 1 failure, 2 usage error, 130 interrupted.
            """);
    }
}
