// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;
using System.Text;

namespace Aesx.Tests;

/// <summary>A read-only, non-seekable stream that returns at most <c>maxRead</c> bytes per call, to exercise partial reads.</summary>
internal sealed class TrickleStream(Stream inner, int maxRead) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maxRead));
    public override int Read(Span<byte> buffer) => inner.Read(buffer[..Math.Min(buffer.Length, maxRead)]);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Cancels a token source once a given number of bytes has been read through it.</summary>
internal sealed class CancelAfterStream(Stream inner, long cancelAfterBytes, CancellationTokenSource cancellation) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        _read += n;
        if (_read >= cancelAfterBytes) cancellation.Cancel();
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A scratch directory that disappears with the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("aesx-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Names of all files below the directory, relative, sorted; proves that no temporary files survive.</summary>
    public string[] Files()
        => Directory.GetFiles(Path, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record CliResult(int ExitCode, byte[] StdoutBytes, string Stderr)
{
    public string Stdout => Encoding.UTF8.GetString(StdoutBytes);

    public override string ToString() => $"exit {ExitCode}\n--- stdout ---\n{Stdout}\n--- stderr ---\n{Stderr}";
}

/// <summary>Everything that can be varied about one run of the executable.</summary>
internal sealed class CliRun
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Text piped into stdin as UTF-8; null closes stdin at once.</summary>
    public string? Stdin { get; init; }

    /// <summary>Raw bytes piped into stdin; takes precedence over <see cref="Stdin"/>.</summary>
    public byte[]? StdinBytes { get; init; }

    /// <summary>Environment variables to set (null value removes one).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

/// <summary>Runs the real <c>aesx</c> executable that the project reference placed next to the test assembly.</summary>
internal static class Cli
{
    private static readonly string s_fileName;
    private static readonly string? s_dllArgument;

    static Cli()
    {
        string dir = AppContext.BaseDirectory;
        string apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "aesx.exe" : "aesx");
        if (File.Exists(apphost))
        {
            s_fileName = apphost;
            return;
        }

        string dll = Path.Combine(dir, "aesx.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("aesx was not built next to the tests", dll);
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        s_fileName = string.IsNullOrEmpty(host) ? "dotnet" : host;
        s_dllArgument = dll;
    }

    /// <summary>Runs aesx with <paramref name="stdin"/> piped in (null closes stdin immediately) and captures everything.</summary>
    public static Task<CliResult> RunAsync(string? stdin, params string[] args) => RunWithAsync(new CliRun { Stdin = stdin }, args);

    /// <summary>Like <see cref="RunAsync"/>, with the process started in <paramref name="workingDirectory"/> so relative paths can be tested.</summary>
    public static Task<CliResult> RunInAsync(string? workingDirectory, string? stdin, params string[] args)
        => RunWithAsync(new CliRun { WorkingDirectory = workingDirectory, Stdin = stdin }, args);

    /// <summary>The general form: every aspect of the run comes from <paramref name="run"/>.</summary>
    public static async Task<CliResult> RunWithAsync(CliRun run, params string[] args)
    {
        using var process = Process.Start(StartInfo(run, args)) ?? throw new InvalidOperationException("could not start aesx");
        Task<byte[]> stdout = ReadAllAsync(process.StandardOutput.BaseStream);
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (run.StdinBytes is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(run.StdinBytes).ConfigureAwait(false);
            }
            else if (run.Stdin is not null)
            {
                await process.StandardInput.WriteAsync(run.Stdin).ConfigureAwait(false);
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process stopped reading (for instance after refusing an overlong line) and may
            // already have exited; what it did with the input is what the test checks.
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("aesx did not exit within two minutes");
        }
        return new CliResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>How the executable is started: every stream redirected, no window, and no password inherited from the test host.</summary>
    public static ProcessStartInfo StartInfo(CliRun run, params string[] args)
    {
        var psi = new ProcessStartInfo(s_fileName)
        {
            WorkingDirectory = run.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Pipe bytes the way modern shells do, instead of the Windows console code page.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.Environment.Remove(PasswordVariable); // never inherit one from the test host by accident
        if (run.Environment is not null)
        {
            foreach ((string name, string? value) in run.Environment)
            {
                if (value is null) psi.Environment.Remove(name);
                else psi.Environment[name] = value;
            }
        }
        if (s_dllArgument is not null) psi.ArgumentList.Add(s_dllArgument);
        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public const string PasswordVariable = "AESX_PASSWORD";

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
