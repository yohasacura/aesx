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
        if (o.Output is not null && o.Inputs.Count > 1)
            throw new UsageException("-o/--output can only be used with a single input file");

        var jobs = o.Inputs.ConvertAll(file => (File: file, Encrypt: o.Command switch
        {
            Command.Encrypt => true,
            Command.Decrypt => false,
            _ => !file.EndsWith(AesxFormat.FileExtension, StringComparison.OrdinalIgnoreCase),
        }));

        bool anyEncrypt = jobs.Exists(j => j.Encrypt);
        if (anyEncrypt && o.Iterations < 100_000)
            Console.Error.WriteLine($"warning: {o.Iterations} PBKDF2 iterations is low (default: {AesxFormat.DefaultIterations})");

        byte[] password = PasswordInput.Obtain(o, confirm: anyEncrypt);
        int failures = 0;
        try
        {
            foreach ((string file, bool encrypt) in jobs)
            {
                try
                {
                    ProcessFile(file, encrypt, o, password);
                }
                catch (Exception ex) when (jobs.Count > 1)
                {
                    failures++;
                    Console.Error.WriteLine($"error: {file}: {ex.Message}");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
        return failures == 0 ? ExitSuccess : ExitFailure;
    }

    private static void ProcessFile(string input, bool encrypt, Options o, byte[] password)
    {
        if (!File.Exists(input))
            throw new FileNotFoundException($"input file not found: {input}");

        string output = o.Output ?? DefaultOutput(input, encrypt);
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("input and output are the same file");
        if (File.Exists(output) && !o.Force)
            throw new InvalidOperationException($"output file already exists: {output} (use -f to overwrite)");

        if (!o.Quiet)
            Console.WriteLine($"{(encrypt ? "Encrypting" : "Decrypting")} {input} -> {output}");

        // Write to a temporary file and rename at the end, so a wrong password, a corrupted
        // input or a crash never leaves a partial output behind.
        string temp = output + ".part";
        long started = Stopwatch.GetTimestamp();
        try
        {
            using (var fin = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var fout = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                if (encrypt) AesxFormat.Encrypt(fin, fout, password, o.Iterations);
                else AesxFormat.Decrypt(fin, fout, password);
                fout.Flush(flushToDisk: true);
            }
            File.Move(temp, output, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        if (o.DeleteInput) File.Delete(input);
        if (!o.Quiet)
            Console.WriteLine($"Done: {new FileInfo(output).Length:N0} bytes in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
    }

    private static string DefaultOutput(string input, bool encrypt)
    {
        string ext = AesxFormat.FileExtension;
        if (encrypt) return input + ext;
        if (input.Length > ext.Length && input.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            return input[..^ext.Length];
        throw new UsageException($"cannot derive an output name for '{input}' (no {ext} extension); use -o <file>");
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
