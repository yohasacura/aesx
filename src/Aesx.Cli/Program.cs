// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;
using System.Security.Cryptography;

namespace Aesx.Cli;

internal static class Program
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;
    public const int ExitInterrupted = 130;

    /// <summary>Version of this build, from the assembly version (set through &lt;Version&gt; or -p:Version).</summary>
    public static string Version => (typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    private static int Main(string[] args)
    {
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
        if (o.Output is not null && o.Inputs.Count > 1 && !Directory.Exists(o.Output))
            throw new UsageException("with several input files, -o/--output must name an existing directory");

        var jobs = o.Inputs.ConvertAll(file => (File: file, Encrypt: o.Command switch
        {
            Command.Encrypt => true,
            Command.Decrypt => false,
            _ => !file.EndsWith(AesxFormat.FileExtension, StringComparison.OrdinalIgnoreCase),
        }));

        bool anyEncrypt = jobs.Exists(j => j.Encrypt);
        if (anyEncrypt && o.Iterations < 100_000)
            Console.Error.WriteLine($"warning: {o.Iterations} PBKDF2 iterations is low (default: {AesxFormat.DefaultIterations})");
        if (!anyEncrypt && o.IterationsSpecified)
            Console.Error.WriteLine("warning: --iterations only applies when encrypting; ignored (the count is stored in the file)");

        bool interactive = o.Password is null && !Console.IsInputRedirected;
        byte[] password = PasswordInput.Obtain(o, confirm: anyEncrypt);

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
                    ProcessFile(file, encrypt, o, password, interrupts.Token);
                }
                catch (Exception ex) when (jobs.Count > 1 && ex is not OperationCanceledException)
                {
                    failures++;
                    Console.Error.WriteLine($"error: {file}: {ex.Message}");
                }
            }
        }
        finally
        {
            interrupts.Armed = false;
            CryptographicOperations.ZeroMemory(password);
        }

        if (anyEncrypt && failures == 0 && interactive && !o.Quiet)
            Console.WriteLine("Keep the password safe: an encrypted file cannot be recovered without it.");
        return failures == 0 ? ExitSuccess : ExitFailure;
    }

    private static void ProcessFile(string input, bool encrypt, Options o, byte[] password, CancellationToken cancellationToken)
    {
        if (Directory.Exists(input))
            throw new InvalidOperationException($"'{input}' is a directory; aesx works on files (put a folder in a .zip or .tar archive first)");
        if (!File.Exists(input))
            throw new FileNotFoundException($"input file not found: {input}");
        if (encrypt && o.Command == Command.Encrypt && input.EndsWith(AesxFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"warning: {input} already has the {AesxFormat.FileExtension} extension and will be encrypted a second time");

        string output = OutputPathFor(input, encrypt, o.Output);
        if (Directory.Exists(output))
            throw new InvalidOperationException($"output path is a directory: {output}");
        if (SameFile(input, output))
            throw new InvalidOperationException("input and output are the same file");
        if (File.Exists(output) && !o.Force)
            throw new InvalidOperationException($"output file already exists: {output} (use -f to overwrite)");

        if (!o.Quiet)
            Console.WriteLine($"{(encrypt ? "Encrypting" : "Decrypting")} {input} -> {output}");

        // Write to a uniquely named temporary file created exclusively (so a planted symlink
        // cannot redirect the write), then rename it into place. A wrong password, a damaged
        // input, a full disk, an interrupt or a crash therefore never leaves a partial output.
        string temp = TemporaryPathNextTo(output);
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
            File.Move(temp, output, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        bool verified = false;
        if (o.DeleteInput)
        {
            if (encrypt)
            {
                // The original is about to disappear: make sure what replaces it really decrypts.
                try
                {
                    using FileStream check = OpenForReading(output);
                    AesxFormat.Decrypt(check, Stream.Null, password, cancellationToken);
                }
                catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException)
                {
                    throw new IOException($"{output} was written but failed verification ({ex.Message}); {input} was kept", ex);
                }
                verified = true;
            }
            try
            {
                File.Delete(input);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{output} was written, but the input could not be deleted: {ex.Message}", ex);
            }
        }

        if (!o.Quiet)
        {
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Console.WriteLine($"Done: {new FileInfo(output).Length:N0} bytes in {seconds:F1} s{(verified ? ", verified" : "")}{(o.DeleteInput ? ", input deleted" : "")}");
        }
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
        throw new UsageException($"cannot derive an output name for '{input}' (no {ext} extension); use -o <file>");
    }

    /// <summary>True when both names reach the same existing file, following symbolic links.</summary>
    private static bool SameFile(string input, string output)
    {
        if (!File.Exists(output)) return false;
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(FinalPath(input), FinalPath(output), comparison);
    }

    private static string FinalPath(string path)
    {
        // Resolve from the absolute path: a relative link target is interpreted relative to the
        // link's directory, which is only known once the link's own path is absolute.
        string full = Path.GetFullPath(path);
        try
        {
            return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    private static string TemporaryPathNextTo(string output)
        => $"{output}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.part";

    private static FileStream OpenForReading(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    /// <summary>Creates a brand-new file (never follows an existing path or link); decrypted output is readable by its owner only on Unix.</summary>
    private static FileStream CreateExclusively(string path, bool ownerOnly)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1 << 16,
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
}
