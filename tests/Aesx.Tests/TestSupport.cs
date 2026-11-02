// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;

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

/// <summary>A scratch directory that disappears with the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("aesx-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string[] Files() => Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

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

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    public override string ToString() => $"exit {ExitCode}\n--- stdout ---\n{Stdout}\n--- stderr ---\n{Stderr}";
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
    public static async Task<CliResult> RunAsync(string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(s_fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (s_dllArgument is not null) psi.ArgumentList.Add(s_dllArgument);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start aesx");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (stdin is not null) await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        process.StandardInput.Close();

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
}
