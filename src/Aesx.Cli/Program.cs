// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aesx.Cli;

internal static class Program
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;
    public const int ExitInterrupted = 130;

    /// <summary>The -o value that means "write to standard output".</summary>
    public const string StandardOutput = "-";

    /// <summary>Version of this build, from the assembly version (set through &lt;Version&gt; or -p:Version).</summary>
    public static string Version => (typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    private static int Main(string[] args)
    {
        UseUtf8ForRedirectedOutput();
        int code;
        try
        {
            code = Run(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("Run 'aesx --help' for usage.");
            code = ExitUsage;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("error: interrupted; the unfinished output file was removed");
            code = ExitInterrupted;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("AESX_DEBUG") == "1") Console.Error.WriteLine(ex);
            code = ExitFailure;
        }
        ConsoleSupport.PauseIfLaunchedFromExplorer();
        return code;
    }

    private static int Run(string[] args)
    {
        Options o = CommandLine.Parse(args);
        switch (o.Command)
        {
            case Command.Help:
                CommandLine.PrintUsage(Console.Out);
                return ExitSuccess;
            case Command.Version:
                Console.WriteLine($"aesx {Version}");
                return ExitSuccess;
            case Command.SelfTest:
                return SelfTest.Run();
            default:
                break;
        }

        if (o.Inputs.Count == 0)
        {
            CommandLine.PrintUsage(Console.Error);
            return ExitUsage;
        }
        if (!AesxFormat.IsSupported)
            throw new PlatformNotSupportedException("AES-GCM is not available on this system");

        bool toStandardOutput = o.Output == StandardOutput;
        if (o.Inputs.Count > 1 && o.Output is not null && (toStandardOutput || !Directory.Exists(o.Output)))
            throw new UsageException("with several input files, -o/--output must name an existing directory");
        if (toStandardOutput && o.DeleteInput)
            throw new UsageException("--delete cannot be combined with -o - (the result cannot be verified or kept)");

        var jobs = o.Inputs.ConvertAll(file => (File: file, Encrypt: o.Command switch
        {
            Command.Encrypt => true,
            Command.Decrypt => false,
            _ => !file.EndsWith(AesxFormat.FileExtension, StringComparison.OrdinalIgnoreCase),
        }));

        bool anyEncrypt = jobs.Exists(j => j.Encrypt);
        if (toStandardOutput && anyEncrypt && !Console.IsOutputRedirected && !o.Force)
            throw new UsageException("refusing to write encrypted data to the terminal; redirect standard output or use --force");
        if (anyEncrypt && o.Iterations < 100_000)
            Console.Error.WriteLine($"warning: {o.Iterations} PBKDF2 iterations is low (default: {AesxFormat.DefaultIterations})");
        if (!anyEncrypt && o.IterationsSpecified)
            Console.Error.WriteLine("warning: --iterations only applies when encrypting; ignored (the count is stored in the file)");

        bool interactive = o.Password is null
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PasswordInput.EnvironmentVariable))
            && !Console.IsInputRedirected;
        byte[] password = PasswordInput.Obtain(o, confirm: anyEncrypt);

        // Status lines go to standard output, unless that is where the data goes.
        TextWriter status = toStandardOutput || o.Quiet ? TextWriter.Null : Console.Out;
        if (toStandardOutput && !o.Quiet) status = Console.Error;

        // From here on an interrupt (Ctrl+C, closed window, SIGTERM) is turned into a
        // cancellation so the file in progress is cleaned up before exiting.
        using var interrupts = new InterruptGuard();
        int failures = 0;
        try
        {
            interrupts.Armed = true;
            foreach ((string file, bool encrypt) in jobs)
            {
                try
                {
                    ProcessFile(file, encrypt, o, password, status, interrupts.Token);
                }
                catch (Exception ex) when (jobs.Count > 1 && ex is not OperationCanceledException)
                {
                    failures++;
                    Console.Error.WriteLine($"error: {TextSafety.Display(file)}: {ex.Message}");
                }
            }
        }
        finally
        {
            interrupts.Armed = false;
            interrupts.MarkFinished();
            CryptographicOperations.ZeroMemory(password);
        }

        if (anyEncrypt && failures == 0 && interactive)
            status.WriteLine("Keep the password safe: an encrypted file cannot be recovered without it.");
        return failures == 0 ? ExitSuccess : ExitFailure;
    }

    private static void ProcessFile(string input, bool encrypt, Options o, byte[] password, TextWriter status, CancellationToken cancellationToken)
    {
        string shownInput = TextSafety.Display(input);
        string fullInput = FullPathOrFail(input, "input");
        if (Directory.Exists(fullInput))
            throw new InvalidOperationException($"'{shownInput}' is a directory; aesx works on files (put a folder in a .zip or .tar archive first)");
        if (!File.Exists(fullInput))
            throw new FileNotFoundException($"input file not found: {shownInput}");
        if (encrypt && o.Command == Command.Encrypt && input.EndsWith(AesxFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"warning: {shownInput} already has the {AesxFormat.FileExtension} extension and will be encrypted a second time");

        if (o.Output == StandardOutput)
        {
            status.WriteLine($"{(encrypt ? "Encrypting" : "Decrypting")} {shownInput} -> standard output");
            long started = Stopwatch.GetTimestamp();
            using (FileStream fin = OpenForReading(fullInput))
            using (Stream stdout = Console.OpenStandardOutput())
            using (Stream source = ProgressStream.WrapIfWorthwhile(fin, o.Quiet))
            {
                if (encrypt) AesxFormat.Encrypt(source, stdout, password, o.Iterations, cancellationToken);
                else AesxFormat.Decrypt(source, stdout, password, cancellationToken);
                stdout.Flush();
            }
            status.WriteLine($"Done in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
            return;
        }

        string output = OutputPathFor(input, encrypt, o.Output);
        string shownOutput = TextSafety.Display(output);
        string fullOutput = FullPathOrFail(output, "output");
        if (Directory.Exists(fullOutput))
            throw new InvalidOperationException($"output path is a directory: {shownOutput}");
        if (SameFile(fullInput, fullOutput))
            throw new InvalidOperationException("input and output are the same file");
        if (File.Exists(fullOutput) && !o.Force)
            throw new InvalidOperationException($"output file already exists: {shownOutput} (use -f to overwrite)");

        status.WriteLine($"{(encrypt ? "Encrypting" : "Decrypting")} {shownInput} -> {shownOutput}");
        WriteThroughTemporaryFile(fullInput, fullOutput, encrypt, o, password, cancellationToken, out double seconds);

        bool verified = false;
        if (o.DeleteInput)
        {
            if (encrypt)
            {
                // The original is about to disappear: make sure what replaces it really decrypts.
                try
                {
                    using FileStream check = OpenForReading(fullOutput);
                    AesxFormat.Decrypt(check, Stream.Null, password, cancellationToken);
                }
                catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException)
                {
                    throw new IOException($"{shownOutput} was written but failed verification ({ex.Message}); {shownInput} was kept", ex);
                }
                verified = true;
            }
            try
            {
                File.Delete(fullInput);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{shownOutput} was written, but the input could not be deleted: {ex.Message}", ex);
            }
        }

        status.WriteLine($"Done: {new FileInfo(fullOutput).Length:N0} bytes in {seconds:F1} s{(verified ? ", verified" : "")}{(o.DeleteInput ? ", input deleted" : "")}");
    }

    /// <summary>
    /// Writes to a uniquely named temporary file created exclusively (so a planted symbolic link
    /// cannot redirect the write), then renames it into place. A wrong password, a damaged
    /// input, a full disk, an interrupt or a crash therefore never leaves a partial output.
    /// </summary>
    private static void WriteThroughTemporaryFile(string input, string output, bool encrypt, Options o, byte[] password, CancellationToken cancellationToken, out double seconds)
    {
        string temp = $"{output}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.part";
        long started = Stopwatch.GetTimestamp();
        try
        {
            using (FileStream fin = OpenForReading(input))
            using (FileStream fout = CreateExclusively(temp, ownerOnly: !encrypt))
            using (Stream source = ProgressStream.WrapIfWorthwhile(fin, o.Quiet))
            {
                if (encrypt) AesxFormat.Encrypt(source, fout, password, o.Iterations, cancellationToken);
                else AesxFormat.Decrypt(source, fout, password, cancellationToken);
                fout.Flush(flushToDisk: true);
            }

            // Without --force the rename itself refuses to replace a file, so one that appeared
            // while we were working is as safe as one that was there from the start.
            try
            {
                File.Move(temp, output, overwrite: o.Force);
            }
            catch (IOException) when (!o.Force && File.Exists(output))
            {
                throw new InvalidOperationException($"output file appeared while working: {TextSafety.Display(output)} (use -f to overwrite)");
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    /// <summary>The default name next to the input, or the requested path; a requested directory receives the default name inside it.</summary>
    private static string OutputPathFor(string input, bool encrypt, string? requested)
    {
        if (requested is null) return DefaultOutput(input, encrypt);
        if (Directory.Exists(requested)) return Path.Combine(requested, Path.GetFileName(DefaultOutput(input, encrypt)));
        return requested;
    }

    private static string DefaultOutput(string input, bool encrypt)
    {
        string ext = AesxFormat.FileExtension;
        if (encrypt) return input + ext;
        if (input.Length > ext.Length && input.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            return input[..^ext.Length];
        throw new UsageException($"cannot derive an output name for '{TextSafety.Display(input)}' (no {ext} extension); use -o <file>");
    }

    private static string FullPathOrFail(string path, string role)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            throw new InvalidOperationException($"invalid {role} path '{TextSafety.Display(path)}': {ex.Message}", ex);
        }

        // Windows forbids a handful of characters in names; the file system would only say so
        // after the work is done, with a message that does not name the culprit.
        if (OperatingSystem.IsWindows()
            && (Path.GetFileName(full).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || full.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
        {
            throw new InvalidOperationException($"invalid {role} path '{TextSafety.Display(path)}': Windows does not allow the characters < > : \" | ? * or control characters in file names");
        }
        return full;
    }

    /// <summary>True when both names reach the same existing file, following symbolic links.</summary>
    private static bool SameFile(string input, string output)
    {
        if (!File.Exists(output)) return false;
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(FinalPath(input), FinalPath(output), comparison);
    }

    private static string FinalPath(string fullPath)
    {
        try
        {
            return File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return fullPath;
        }
    }

    /// <summary>Unbuffered: the format reads whole 1 MiB chunks, so an extra copy of plaintext in a stream buffer would only linger.</summary>
    private static FileStream OpenForReading(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);

    /// <summary>Creates a brand-new file (never follows an existing path or link); decrypted output is readable by its owner only on Unix.</summary>
    private static FileStream CreateExclusively(string path, bool ownerOnly)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
        };
        if (ownerOnly && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Logs and pipes get UTF-8 (the Unix default); on Windows the console code page would turn non-Latin file names into question marks. A live console is left alone.</summary>
    private static void UseUtf8ForRedirectedOutput()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (Console.IsOutputRedirected) Console.OutputEncoding = utf8;
        if (Console.IsErrorRedirected && !Console.IsOutputRedirected) Console.OutputEncoding = utf8;
    }
}
