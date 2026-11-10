// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Aesx.Tests;

/// <summary>End-to-end tests that run the real aesx executable against real files.</summary>
public class CliTests
{
    private const string Pw = "test-password";
    private static readonly string[] FastKdf = ["--iterations", "1000"];

    [Fact]
    public async Task EncryptsAndDecryptsAFile()
    {
        using var dir = new TempDir();
        byte[] data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        string plain = dir.File("report.pdf");
        await File.WriteAllBytesAsync(plain, data);

        CliResult enc = await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Equal(["report.pdf", "report.pdf.aesx"], dir.Files());
        Assert.Contains("Encrypting", enc.Stdout, StringComparison.Ordinal);

        string restored = dir.File("restored.pdf");
        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("report.pdf.aesx"), "-p", Pw, "-o", restored);
        Assert.True(dec.ExitCode == 0, dec.ToString());
        byte[] restoredBytes = await File.ReadAllBytesAsync(restored);
        Assert.True(data.AsSpan().SequenceEqual(restoredBytes));
        Assert.Equal(["report.pdf", "report.pdf.aesx", "restored.pdf"], dir.Files());
    }

    [Fact]
    public async Task HandlesEmptyFiles()
    {
        using var dir = new TempDir();
        string plain = dir.File("empty.txt");
        await File.WriteAllBytesAsync(plain, []);

        CliResult enc = await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Equal(AesxFormat.HeaderSize + AesxFormat.TagSize, new FileInfo(dir.File("empty.txt.aesx")).Length);

        File.Delete(plain);
        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("empty.txt.aesx"), "-p", Pw);
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Equal(0, new FileInfo(plain).Length);
    }

    [Fact]
    public async Task WrongPasswordLeavesNothingBehind()
    {
        using var dir = new TempDir();
        string plain = dir.File("secret.bin");
        await File.WriteAllBytesAsync(plain, RandomNumberGenerator.GetBytes(100_000));
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, .. FastKdf])).ExitCode);
        File.Delete(plain);

        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("secret.bin.aesx"), "-p", "not-the-password");

        Assert.Equal(1, dec.ExitCode);
        Assert.Contains("wrong password", dec.Stderr, StringComparison.Ordinal);
        Assert.Equal(["secret.bin.aesx"], dir.Files());
    }

    [Fact]
    public async Task RejectsATamperedFile()
    {
        using var dir = new TempDir();
        string plain = dir.File("data.bin");
        await File.WriteAllBytesAsync(plain, RandomNumberGenerator.GetBytes(3 * 1024 * 1024));
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, .. FastKdf])).ExitCode);
        File.Delete(plain);

        string encrypted = dir.File("data.bin.aesx");
        byte[] ct = await File.ReadAllBytesAsync(encrypted);
        ct[AesxFormat.HeaderSize + AesxFormat.ChunkSize + AesxFormat.TagSize + 12345] ^= 0x80; // inside chunk 1
        await File.WriteAllBytesAsync(encrypted, ct);

        CliResult dec = await Cli.RunAsync(null, "decrypt", encrypted, "-p", Pw);

        Assert.Equal(1, dec.ExitCode);
        Assert.Contains("tampered", dec.Stderr, StringComparison.Ordinal);
        Assert.Equal(["data.bin.aesx"], dir.Files());
    }

    [Fact]
    public async Task RefusesToOverwriteUnlessForced()
    {
        using var dir = new TempDir();
        string plain = dir.File("notes.txt");
        await File.WriteAllTextAsync(plain, "hello");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, .. FastKdf])).ExitCode);
        byte[] first = await File.ReadAllBytesAsync(dir.File("notes.txt.aesx"));

        CliResult again = await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, .. FastKdf]);
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("already exists", again.Stderr, StringComparison.Ordinal);
        byte[] untouched = await File.ReadAllBytesAsync(dir.File("notes.txt.aesx"));
        Assert.True(first.AsSpan().SequenceEqual(untouched));

        CliResult forced = await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, "--force", .. FastKdf]);
        Assert.True(forced.ExitCode == 0, forced.ToString());
        byte[] replaced = await File.ReadAllBytesAsync(dir.File("notes.txt.aesx"));
        Assert.False(first.AsSpan().SequenceEqual(replaced));
    }

    [Fact]
    public async Task ReadsThePasswordFromStandardInput()
    {
        using var dir = new TempDir();
        string plain = dir.File("piped.txt");
        await File.WriteAllTextAsync(plain, "piped content");

        CliResult enc = await Cli.RunAsync("from-stdin\n", ["encrypt", plain, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        File.Delete(plain);

        CliResult dec = await Cli.RunAsync("from-stdin\r\n", "decrypt", dir.File("piped.txt.aesx"));
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Equal("piped content", await File.ReadAllTextAsync(plain));
    }

    [Fact]
    public async Task DecidesByExtensionWhenNoCommandIsGiven()
    {
        using var dir = new TempDir();
        string plain = dir.File("photo.jpg");
        byte[] data = RandomNumberGenerator.GetBytes(5000);
        await File.WriteAllBytesAsync(plain, data);

        Assert.Equal(0, (await Cli.RunAsync(null, [plain, "-p", Pw, .. FastKdf])).ExitCode);
        Assert.Equal(["photo.jpg", "photo.jpg.aesx"], dir.Files());
        File.Delete(plain);

        Assert.Equal(0, (await Cli.RunAsync(null, dir.File("photo.jpg.aesx"), "-p", Pw)).ExitCode);
        byte[] restored = await File.ReadAllBytesAsync(plain);
        Assert.True(data.AsSpan().SequenceEqual(restored));
    }

    [Fact]
    public async Task AFileNamedLikeACommandOrASwitchIsStillAFile()
    {
        using var dir = new TempDir();
        foreach (string name in new[] { "Embedding", "verify", "info", "register" })
        {
            await File.WriteAllTextAsync(dir.File(name), name);

            // Given alone and by its bare name, as somebody sitting in that folder would type it.
            CliResult run = await Cli.RunInAsync(dir.Path, Pw + "\n", name);

            Assert.True(run.ExitCode == 0, $"{name}: {run}");
            Assert.True(File.Exists(dir.File(name + ".aesx")), name);
        }
    }

    [Fact]
    public async Task DeletesTheInputOnRequest()
    {
        using var dir = new TempDir();
        string plain = dir.File("gone.txt");
        await File.WriteAllTextAsync(plain, "x");

        CliResult enc = await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, "--delete", .. FastKdf]);

        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Equal(["gone.txt.aesx"], dir.Files());
    }

    [Fact]
    public async Task ContinuesWithTheRemainingFilesAfterAFailure()
    {
        using var dir = new TempDir();
        string good = dir.File("good.txt");
        await File.WriteAllTextAsync(good, "ok");

        CliResult run = await Cli.RunAsync(null, ["encrypt", dir.File("missing.txt"), good, "-p", Pw, .. FastKdf]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("missing.txt", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(["good.txt", "good.txt.aesx"], dir.Files());
    }

    [Fact]
    public async Task ReportsUsageErrorsWithExitCode2()
    {
        using var dir = new TempDir();
        string plain = dir.File("a.txt");
        await File.WriteAllTextAsync(plain, "a");

        Assert.Equal(2, (await Cli.RunAsync(null, "encrypt", plain, "-p", Pw, "--iterations", "lots")).ExitCode);
        Assert.Equal(2, (await Cli.RunAsync(null, "encrypt", plain, "-p", Pw, "--iterations", "0")).ExitCode);
        Assert.Equal(2, (await Cli.RunAsync(null, "encrypt", plain, "-p", Pw, "--bogus")).ExitCode);
        Assert.Equal(2, (await Cli.RunAsync(null, "encrypt", plain, "-p")).ExitCode);
        Assert.Equal(2, (await Cli.RunAsync(null, "encrypt", plain, plain, "-p", Pw, "-o", dir.File("out"))).ExitCode);
        Assert.Equal(2, (await Cli.RunAsync(null, "encrypt", plain, "-p", "")).ExitCode);
        Assert.Equal(2, (await Cli.RunAsync(null, "decrypt", plain, "-p", Pw)).ExitCode); // no .aesx extension and no -o

        CliResult noArgs = await Cli.RunAsync(null);
        Assert.Equal(2, noArgs.ExitCode);
        Assert.Contains("Usage:", noArgs.Stderr, StringComparison.Ordinal);
        Assert.Equal(["a.txt"], dir.Files());
    }

    [Fact]
    public async Task EmptyPasswordFromStandardInputIsRefused()
    {
        using var dir = new TempDir();
        string plain = dir.File("a.txt");
        await File.WriteAllTextAsync(plain, "a");

        CliResult run = await Cli.RunAsync("\n", ["encrypt", plain, .. FastKdf]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("must not be empty", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(["a.txt"], dir.Files());
    }

    [Fact]
    public async Task PrintsVersionAndHelp()
    {
        CliResult version = await Cli.RunAsync(null, "--version");
        Assert.Equal(0, version.ExitCode);
        Assert.Matches(new Regex(@"^aesx \d+\.\d+\.\d+\s*$"), version.Stdout);

        CliResult help = await Cli.RunAsync(null, "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Usage:", help.Stdout, StringComparison.Ordinal);
        Assert.Contains("--iterations", help.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfTestPasses()
    {
        CliResult run = await Cli.RunAsync(null, "selftest");

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("All self-tests passed.", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", run.Stdout, StringComparison.Ordinal);
    }
}
