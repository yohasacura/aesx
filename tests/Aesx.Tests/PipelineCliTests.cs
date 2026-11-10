// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Security.Cryptography;

namespace Aesx.Tests;

/// <summary>
/// Data arriving on standard input, files that are only checked, and headers that are only
/// read: the commands that write nothing, or nothing with a name.
/// </summary>
public class PipelineCliTests
{
    private const string Pw = "test-password";
    private static readonly string[] FastKdf = ["--iterations", "1000"];
    private static readonly Dictionary<string, string?> PwInEnvironment = new() { [Cli.PasswordVariable] = Pw };

    [Fact]
    public async Task EncryptsAndDecryptsDataFromStandardInput()
    {
        using var dir = new TempDir();
        byte[] data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 99);

        CliResult enc = await Cli.RunWithAsync(new CliRun { StdinBytes = data, Environment = PwInEnvironment }, ["encrypt", "-", "-o", dir.File("dump.aesx"), .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Contains("Encrypting standard input", enc.Stdout, StringComparison.Ordinal);
        Assert.Equal(AesxFormat.HeaderSize + data.Length + 3 * AesxFormat.TagSize, new FileInfo(dir.File("dump.aesx")).Length);

        byte[] encrypted = await File.ReadAllBytesAsync(dir.File("dump.aesx"));
        CliResult dec = await Cli.RunWithAsync(new CliRun { StdinBytes = encrypted }, "decrypt", "-", "-p", Pw, "-o", dir.File("dump.bin"));
        Assert.True(dec.ExitCode == 0, dec.ToString());
        byte[] restored = await File.ReadAllBytesAsync(dir.File("dump.bin"));
        Assert.True(data.AsSpan().SequenceEqual(restored));
        Assert.Equal(["dump.aesx", "dump.bin"], dir.Files());
    }

    [Fact]
    public async Task WorksAsAFilterBetweenTwoPipes()
    {
        byte[] data = RandomNumberGenerator.GetBytes(1024 * 1024 + 5);

        CliResult enc = await Cli.RunWithAsync(new CliRun { StdinBytes = data, Environment = PwInEnvironment }, ["encrypt", "-", "-o", "-", .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Contains("standard input -> standard output", enc.Stderr, StringComparison.Ordinal);

        CliResult dec = await Cli.RunWithAsync(new CliRun { StdinBytes = enc.StdoutBytes, Environment = PwInEnvironment }, "decrypt", "-", "-o", "-", "--quiet");
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.True(data.AsSpan().SequenceEqual(dec.StdoutBytes));
        Assert.Equal("", dec.Stderr);
    }

    [Fact]
    public async Task EmptyStandardInputIsAnEmptyFile()
    {
        using var dir = new TempDir();

        CliResult enc = await Cli.RunWithAsync(new CliRun { StdinBytes = [], Environment = PwInEnvironment }, ["encrypt", "-", "-o", dir.File("nothing.aesx"), .. FastKdf]);

        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Equal(AesxFormat.HeaderSize + AesxFormat.TagSize, new FileInfo(dir.File("nothing.aesx")).Length);
    }

    [Fact]
    public async Task DamagedDataFromStandardInputLeavesNoFile()
    {
        using var dir = new TempDir();
        byte[] data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        CliResult enc = await Cli.RunWithAsync(new CliRun { StdinBytes = data, Environment = PwInEnvironment }, ["encrypt", "-", "-o", "-", "-q", .. FastKdf]);
        byte[] damaged = enc.StdoutBytes;
        damaged[^5] ^= 0x10;

        CliResult dec = await Cli.RunWithAsync(new CliRun { StdinBytes = damaged, Environment = PwInEnvironment }, "decrypt", "-", "-o", dir.File("out.bin"));

        Assert.Equal(1, dec.ExitCode);
        Assert.Contains("tampered", dec.Stderr, StringComparison.Ordinal);
        Assert.Empty(dir.Files());
    }

    [Fact]
    public async Task StandardInputCannotCarryBothTheDataAndThePassword()
    {
        using var dir = new TempDir();

        CliResult run = await Cli.RunWithAsync(new CliRun { Stdin = "would-be-password\nand data\n" }, ["encrypt", "-", "-o", dir.File("x.aesx"), .. FastKdf]);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(Cli.PasswordVariable, run.Stderr, StringComparison.Ordinal);
        Assert.Empty(dir.Files());
    }

    [Fact]
    public async Task StandardInputAsDataHasItsOwnUsageErrors()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("a.txt"), "a");
        Directory.CreateDirectory(dir.File("folder"));
        var piped = new CliRun { Stdin = "data", Environment = PwInEnvironment };

        Assert.Equal(2, (await Cli.RunWithAsync(piped, "-")).ExitCode);                                                  // encrypt or decrypt?
        Assert.Equal(2, (await Cli.RunWithAsync(piped, "-", "-o", dir.File("x"))).ExitCode);                             // still: which one?
        Assert.Equal(2, (await Cli.RunWithAsync(piped, "encrypt", "-")).ExitCode);                                       // no name to derive an output from
        Assert.Equal(2, (await Cli.RunWithAsync(piped, "decrypt", "-")).ExitCode);
        Assert.Equal(2, (await Cli.RunWithAsync(piped, "encrypt", "-", dir.File("a.txt"), "-o", dir.File("folder"))).ExitCode); // one stream, one input
        Assert.Equal(2, (await Cli.RunWithAsync(piped, "encrypt", "-", "-o", dir.File("x.aesx"), "--delete")).ExitCode); // nothing to delete
        Assert.Equal(2, (await Cli.RunWithAsync(piped, "encrypt", "-", "-o", dir.File("folder"))).ExitCode);             // a directory is not a file name
        Assert.Equal(2, (await Cli.RunWithAsync(piped, "info", "-")).ExitCode);
        Assert.Equal(["a.txt"], dir.Files());
    }

    [Fact]
    public async Task VerifyChecksFilesAndWritesNothing()
    {
        using var dir = new TempDir();
        byte[] data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 3);
        await File.WriteAllBytesAsync(dir.File("a.bin"), data);
        await File.WriteAllTextAsync(dir.File("b.txt"), "bb");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("a.bin"), dir.File("b.txt"), "-p", Pw, "--delete", .. FastKdf])).ExitCode);

        CliResult ok = await Cli.RunAsync(null, "verify", dir.File("a.bin.aesx"), dir.File("b.txt.aesx"), "-p", Pw);
        Assert.True(ok.ExitCode == 0, ok.ToString());
        Assert.Contains($"OK: {data.Length:N0} bytes", ok.Stdout, StringComparison.Ordinal);
        Assert.Contains("OK: 2 bytes", ok.Stdout, StringComparison.Ordinal);

        CliResult wrong = await Cli.RunAsync(null, "verify", dir.File("a.bin.aesx"), "-p", "not-the-password");
        Assert.Equal(1, wrong.ExitCode);
        Assert.Contains("wrong password", wrong.Stderr, StringComparison.Ordinal);

        CliResult piped = await Cli.RunWithAsync(new CliRun { StdinBytes = await File.ReadAllBytesAsync(dir.File("a.bin.aesx")), Environment = PwInEnvironment }, "verify", "-");
        Assert.True(piped.ExitCode == 0, piped.ToString());

        Assert.Equal(["a.bin.aesx", "b.txt.aesx"], dir.Files());
    }

    [Fact]
    public async Task VerifyReportsEachDamagedFileAndCarriesOn()
    {
        using var dir = new TempDir();
        foreach (string name in new[] { "one", "two", "three" })
        {
            await File.WriteAllBytesAsync(dir.File(name), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        }
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("one"), dir.File("two"), dir.File("three"), "-p", Pw, "--delete", .. FastKdf])).ExitCode);
        byte[] ct = await File.ReadAllBytesAsync(dir.File("two.aesx"));
        ct[AesxFormat.HeaderSize + AesxFormat.ChunkSize + AesxFormat.TagSize + 77] ^= 0x04; // inside chunk 1
        await File.WriteAllBytesAsync(dir.File("two.aesx"), ct);
        await File.WriteAllBytesAsync(dir.File("short.aesx"), ct[..1000]);

        CliResult run = await Cli.RunAsync(null, "verify", dir.File("one.aesx"), dir.File("two.aesx"), dir.File("short.aesx"), dir.File("missing.aesx"), dir.File("three.aesx"), "-p", Pw);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("two.aesx: authentication failed at chunk 1", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("short.aesx: ", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("missing.aesx: ", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, run.Stdout.Split("OK: ").Length - 1); // the first and the last
        Assert.Equal(["one.aesx", "short.aesx", "three.aesx", "two.aesx"], dir.Files());
    }

    [Fact]
    public async Task VerifyAndInfoRefuseOptionsThatImplyWriting()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", Pw, .. FastKdf])).ExitCode);
        string file = dir.File("x.txt.aesx");

        foreach (string command in new[] { "verify", "info" })
        {
            Assert.Equal(2, (await Cli.RunAsync(null, command, file, "-o", dir.File("y"))).ExitCode);
            Assert.Equal(2, (await Cli.RunAsync(null, command, file, "--delete")).ExitCode);
            Assert.Equal(2, (await Cli.RunAsync(null, command, file, "--force")).ExitCode);
            Assert.Equal(2, (await Cli.RunAsync(null, command, file, "-i", "5000")).ExitCode);
            Assert.Equal(2, (await Cli.RunAsync(null, command)).ExitCode); // nothing to look at
        }
        Assert.Equal(2, (await Cli.RunAsync(null, "info", file, "-p", Pw)).ExitCode);
        Assert.Equal(["x.txt", "x.txt.aesx"], dir.Files());
    }

    [Fact]
    public async Task InfoDescribesAFileWithoutAPassword()
    {
        using var dir = new TempDir();
        await File.WriteAllBytesAsync(dir.File("data.bin"), new byte[3 * 1024 * 1024 + 10]);
        Assert.Equal(0, (await Cli.RunAsync(null, "encrypt", dir.File("data.bin"), "-p", Pw, "--iterations", "123456")).ExitCode);

        // No password anywhere: not on the command line, not in the environment, not on stdin.
        CliResult info = await Cli.RunAsync(null, "info", dir.File("data.bin.aesx"));

        Assert.True(info.ExitCode == 0, info.ToString());
        Assert.Contains("AESX version 1", info.Stdout, StringComparison.Ordinal);
        Assert.Contains("iterations  123,456", info.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{AesxFormat.HeaderSize + 3 * 1024 * 1024 + 10 + 4 * AesxFormat.TagSize:N0} bytes in 4 chunks", info.Stdout, StringComparison.Ordinal);
        Assert.Contains($"decrypted   {3 * 1024 * 1024 + 10:N0} bytes", info.Stdout, StringComparison.Ordinal);
        Assert.Equal("", info.Stderr);
    }

    [Fact]
    public async Task InfoRejectsWhatIsNotAnAesxFile()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("plain.txt"), "this is not encrypted, and it is longer than a header");
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", Pw, .. FastKdf])).ExitCode);
        byte[] good = await File.ReadAllBytesAsync(dir.File("x.txt.aesx"));
        await File.WriteAllBytesAsync(dir.File("cut.aesx"), good[..(AesxFormat.HeaderSize + 5)]);
        Directory.CreateDirectory(dir.File("folder"));

        CliResult plain = await Cli.RunAsync(null, "info", dir.File("plain.txt"));
        Assert.Equal(1, plain.ExitCode);
        Assert.Contains("not an AESX file", plain.Stderr, StringComparison.Ordinal);
        Assert.Equal("", plain.Stdout);

        CliResult cut = await Cli.RunAsync(null, "info", dir.File("cut.aesx"));
        Assert.Equal(1, cut.ExitCode);
        Assert.Contains("truncated", cut.Stderr, StringComparison.Ordinal);

        CliResult several = await Cli.RunAsync(null, "info", dir.File("plain.txt"), dir.File("folder"), dir.File("x.txt.aesx"));
        Assert.Equal(1, several.ExitCode);
        Assert.Contains("x.txt.aesx", several.Stdout, StringComparison.Ordinal);
        Assert.Contains("is a directory", several.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyFilesCanBeDecryptedCheckedOrDescribed()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("folder.aesx"));

        foreach (string command in new[] { "decrypt", "verify" })
        {
            CliResult run = await Cli.RunAsync(null, command, dir.File("folder.aesx"), "-p", Pw);
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("is a directory", run.Stderr, StringComparison.Ordinal);
        }
        Assert.Empty(dir.Files());
        Assert.Equal(["folder.aesx"], Directory.GetFileSystemEntries(dir.Path).Select(e => Path.GetFileName(e)));
    }

    [Fact]
    public async Task WildcardsAreExpandedOnWindowsOnly()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("b.txt"), "b");
        await File.WriteAllTextAsync(dir.File("a.txt"), "a");
        await File.WriteAllTextAsync(dir.File("c.md"), "c");
        await File.WriteAllTextAsync(dir.File("hidden.txt"), "h");
        Directory.CreateDirectory(dir.File("folder.txt"));

        if (!OperatingSystem.IsWindows())
        {
            // The shell would have expanded an unquoted pattern; one that arrives is a name.
            await File.WriteAllTextAsync(dir.File("star*.txt"), "literal");
            CliResult literal = await Cli.RunInAsync(dir.Path, null, ["encrypt", "star*.txt", "-p", Pw, .. FastKdf]);
            Assert.True(literal.ExitCode == 0, literal.ToString());
            Assert.True(File.Exists(dir.File("star*.txt.aesx")));
            Assert.False(File.Exists(dir.File("a.txt.aesx")));
            return;
        }

        File.SetAttributes(dir.File("hidden.txt"), FileAttributes.Hidden);

        CliResult run = await Cli.RunInAsync(dir.Path, null, ["encrypt", "*.txt", "-p", Pw, .. FastKdf]);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["a.txt", "a.txt.aesx", "b.txt", "b.txt.aesx", "c.md", "hidden.txt"], dir.Files()); // files only, hidden ones left alone
        Assert.True(run.Stdout.IndexOf("a.txt ->", StringComparison.Ordinal) < run.Stdout.IndexOf("b.txt ->", StringComparison.Ordinal));

        CliResult absolute = await Cli.RunAsync(null, "decrypt", Path.Combine(dir.Path, "?.TXT.aesx"), "-p", Pw, "--force");
        Assert.True(absolute.ExitCode == 0, absolute.ToString());

        CliResult none = await Cli.RunInAsync(dir.Path, null, "encrypt", "*.pdf", "-p", Pw);
        Assert.Equal(1, none.ExitCode);
        Assert.Contains("no files match '*.pdf'", none.Stderr, StringComparison.Ordinal);

        CliResult inFolder = await Cli.RunAsync(null, "encrypt", Path.Combine(dir.Path, "fold*", "x.txt"), "-p", Pw);
        Assert.Equal(2, inFolder.ExitCode);
        Assert.Contains("only expanded in the file name", inFolder.Stderr, StringComparison.Ordinal);
    }
}
