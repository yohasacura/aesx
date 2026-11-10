// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aesx.Tests;

/// <summary>Misuse, hostile input and edge cases, run against the real executable.</summary>
public class RobustnessCliTests
{
    private const string Pw = "test-password";
    private static readonly string[] FastKdf = ["--iterations", "1000"];

    [Fact]
    public async Task OutputOptionMayNameAnExistingDirectory()
    {
        using var dir = new TempDir();
        string a = dir.File("a.txt");
        string b = dir.File("b.txt");
        await File.WriteAllTextAsync(a, "A");
        await File.WriteAllTextAsync(b, "B");
        Directory.CreateDirectory(dir.File("enc"));
        Directory.CreateDirectory(dir.File("dec"));

        CliResult enc = await Cli.RunAsync(null, ["encrypt", a, b, "-p", Pw, "-o", dir.File("enc"), .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Equal(["a.txt", "b.txt", "enc/a.txt.aesx", "enc/b.txt.aesx"], dir.Files());

        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("enc/a.txt.aesx"), "-p", Pw, "-o", dir.File("dec"));
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Equal("A", await File.ReadAllTextAsync(dir.File("dec/a.txt")));
    }

    [Fact]
    public async Task SeveralInputsWithAFileAsOutputIsAUsageError()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("a.txt"), "A");
        await File.WriteAllTextAsync(dir.File("b.txt"), "B");

        CliResult run = await Cli.RunAsync(null, "encrypt", dir.File("a.txt"), dir.File("b.txt"), "-p", Pw, "-o", dir.File("out.aesx"));

        Assert.Equal(2, run.ExitCode);
        Assert.Equal(["a.txt", "b.txt"], dir.Files());
    }

    [Fact]
    public async Task RefusesWhenTheOutputPathIsADirectory()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        Directory.CreateDirectory(dir.File("x.txt.aesx"));

        CliResult run = await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", Pw, .. FastKdf]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("is a directory", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(["x.txt"], dir.Files());
    }

    [Fact]
    public async Task FailsCleanlyWhenTheOutputDirectoryDoesNotExist()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");

        CliResult run = await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", Pw, "-o", dir.File("missing/sub/x.aesx"), .. FastKdf]);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["x.txt"], dir.Files());
        Assert.False(Directory.Exists(dir.File("missing")));
    }

    [Fact]
    public async Task EmptyOutputPathIsAUsageError()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");

        CliResult run = await Cli.RunAsync(null, "encrypt", dir.File("x.txt"), "-p", Pw, "-o", "");

        Assert.Equal(2, run.ExitCode);
        Assert.Equal(["x.txt"], dir.Files());
    }

    [Fact]
    public async Task DeleteVerifiesTheEncryptedFileBeforeRemovingTheOriginal()
    {
        using var dir = new TempDir();
        byte[] data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 3);
        await File.WriteAllBytesAsync(dir.File("keep-me.bin"), data);

        CliResult enc = await Cli.RunAsync(null, ["encrypt", dir.File("keep-me.bin"), "-p", Pw, "--delete", .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Contains("verified", enc.Stdout, StringComparison.Ordinal);
        Assert.Equal(["keep-me.bin.aesx"], dir.Files());

        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("keep-me.bin.aesx"), "-p", Pw, "--delete");
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Equal(["keep-me.bin"], dir.Files());
        byte[] restored = await File.ReadAllBytesAsync(dir.File("keep-me.bin"));
        Assert.True(data.AsSpan().SequenceEqual(restored));
    }

    [Fact]
    public async Task StripsAByteOrderMarkFromAPipedPassword()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "bom");

        CliResult enc = await Cli.RunAsync("﻿" + Pw + "\n", ["encrypt", dir.File("x.txt"), .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        File.Delete(dir.File("x.txt"));

        CliResult dec = await Cli.RunAsync(Pw + "\n", "decrypt", dir.File("x.txt.aesx"));
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Equal("bom", await File.ReadAllTextAsync(dir.File("x.txt")));
    }

    [Fact]
    public async Task AcceptsLongNonAsciiPasswordsAndKeepsSurroundingSpaces()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "long");
        string password = "  " + string.Concat(Enumerable.Repeat("пароль✓日本語 ", 400)) + "  ";
        Assert.True(Encoding.UTF8.GetByteCount(password) > 10_000);

        CliResult enc = await Cli.RunAsync(password + "\n", ["encrypt", dir.File("x.txt"), .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        File.Delete(dir.File("x.txt"));

        CliResult trimmed = await Cli.RunAsync(password.Trim() + "\n", "decrypt", dir.File("x.txt.aesx"));
        Assert.Equal(1, trimmed.ExitCode); // spaces are part of the password, never trimmed away

        CliResult exact = await Cli.RunAsync(password + "\n", "decrypt", dir.File("x.txt.aesx"));
        Assert.True(exact.ExitCode == 0, exact.ToString());
        Assert.Equal("long", await File.ReadAllTextAsync(dir.File("x.txt")));
    }

    [Fact]
    public async Task HandlesSpacesAndUnicodeInFileNames()
    {
        using var dir = new TempDir();
        const string name = "отчёт за 2025 год (final) — copy.txt";
        await File.WriteAllTextAsync(dir.File(name), "содержимое");

        CliResult enc = await Cli.RunAsync(null, [dir.File(name), "-p", Pw, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        File.Delete(dir.File(name));

        CliResult dec = await Cli.RunAsync(null, dir.File(name + ".aesx"), "-p", Pw);
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Equal("содержимое", await File.ReadAllTextAsync(dir.File(name)));
    }

    [Fact]
    public async Task FileNamesStartingWithADashWorkAfterDoubleDash()
    {
        using var dir = new TempDir();
        string previous = Environment.CurrentDirectory;
        await File.WriteAllTextAsync(dir.File("-weird.txt"), "dash");

        // Relative names are needed for the dash to be ambiguous at all; the executable runs with
        // the test process's working directory, so use absolute paths through "--" instead.
        CliResult enc = await Cli.RunAsync(null, ["encrypt", "-p", Pw, .. FastKdf, "--", dir.File("-weird.txt")]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Equal(["-weird.txt", "-weird.txt.aesx"], dir.Files());
        Assert.Equal(previous, Environment.CurrentDirectory);
    }

    [Fact]
    public async Task MixedEncryptAndDecryptInOneRun()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("plain.txt"), "p");
        await File.WriteAllTextAsync(dir.File("other.txt"), "o");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("other.txt"), "-p", Pw, .. FastKdf])).ExitCode);
        File.Delete(dir.File("other.txt"));

        CliResult run = await Cli.RunAsync(null, [dir.File("plain.txt"), dir.File("other.txt.aesx"), "-p", Pw, .. FastKdf]);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(["other.txt", "other.txt.aesx", "plain.txt", "plain.txt.aesx"], dir.Files());
    }

    [Fact]
    public async Task WarnsWhenIterationsAreGivenForDecryptionAndWhenEncryptingAnAesxFile()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", Pw, .. FastKdf])).ExitCode);

        CliResult dec = await Cli.RunAsync(null, ["decrypt", dir.File("x.txt.aesx"), "-p", Pw, "-o", dir.File("y.txt"), .. FastKdf]);
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Contains("ignored", dec.Stderr, StringComparison.Ordinal);

        CliResult twice = await Cli.RunAsync(null, ["encrypt", dir.File("x.txt.aesx"), "-p", Pw, .. FastKdf]);
        Assert.True(twice.ExitCode == 0, twice.ToString());
        Assert.Contains("encrypted a second time", twice.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsACraftedHeaderWithAHugeIterationCountQuickly()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", Pw, .. FastKdf])).ExitCode);
        File.Delete(dir.File("x.txt"));
        byte[] ct = await File.ReadAllBytesAsync(dir.File("x.txt.aesx"));
        ct[5] = ct[6] = ct[7] = ct[8] = 0xFF;
        await File.WriteAllBytesAsync(dir.File("x.txt.aesx"), ct);

        long started = Stopwatch.GetTimestamp();
        CliResult run = await Cli.RunAsync(null, "decrypt", dir.File("x.txt.aesx"), "-p", Pw);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("key-derivation", run.Stderr, StringComparison.Ordinal);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
        Assert.Equal(["x.txt.aesx"], dir.Files());
    }

    [Fact]
    public async Task ASymbolicLinkToTheInputIsRecognisedAsTheSameFile()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("real.txt"), "real");
        try
        {
            File.CreateSymbolicLink(dir.File("alias.txt"), dir.File("real.txt"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // creating links needs a privilege on some Windows setups; nothing to test then
        }

        CliResult run = await Cli.RunAsync(null, ["encrypt", dir.File("real.txt"), "-p", Pw, "-f", "-o", dir.File("alias.txt"), .. FastKdf]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("same file", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("real", await File.ReadAllTextAsync(dir.File("real.txt")));
    }

    [Fact]
    public async Task ARelativeSymbolicLinkToTheInputIsRecognisedAsTheSameFile()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("real.txt"), "real");
        try
        {
            File.CreateSymbolicLink(dir.File("alias.txt"), "real.txt"); // relative target, like `ln -s real.txt alias.txt`
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        // Relative paths all round, as typed in a shell sitting in that directory.
        CliResult run = await Cli.RunInAsync(dir.Path, null, ["encrypt", "real.txt", "-p", Pw, "-f", "-o", "alias.txt", .. FastKdf]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("same file", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("real", await File.ReadAllTextAsync(dir.File("real.txt")));
        Assert.Equal(["alias.txt", "real.txt"], dir.Files());
    }

    [Fact]
    public async Task ForcedOverwriteOfASymbolicLinkReplacesTheLinkNotItsTarget()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("victim.txt"), "do not touch");
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        try
        {
            File.CreateSymbolicLink(dir.File("x.txt.aesx"), dir.File("victim.txt"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        CliResult refused = await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", Pw, .. FastKdf]);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("already exists", refused.Stderr, StringComparison.Ordinal);

        CliResult forced = await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", Pw, "-f", .. FastKdf]);
        Assert.True(forced.ExitCode == 0, forced.ToString());
        Assert.Equal("do not touch", await File.ReadAllTextAsync(dir.File("victim.txt")));
        Assert.Null(new FileInfo(dir.File("x.txt.aesx")).LinkTarget);
    }

    [Fact]
    public async Task DecryptedFilesAreReadableByTheirOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX permissions only

        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("secret.txt"), "s");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("secret.txt"), "-p", Pw, .. FastKdf])).ExitCode);
        File.Delete(dir.File("secret.txt"));

        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("secret.txt.aesx"), "-p", Pw);

        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dir.File("secret.txt")));
    }

    [Fact]
    public async Task ParallelRunsOnTheSameInputDoNotInterfere()
    {
        using var dir = new TempDir();
        byte[] data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        await File.WriteAllBytesAsync(dir.File("shared.bin"), data);

        CliResult[] runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            Cli.RunAsync(null, ["encrypt", dir.File("shared.bin"), "-p", Pw, "-o", dir.File($"copy{i}.aesx"), .. FastKdf])));

        Assert.All(runs, r => Assert.True(r.ExitCode == 0, r.ToString()));
        foreach (int i in Enumerable.Range(0, 4))
        {
            CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File($"copy{i}.aesx"), "-p", Pw, "-o", dir.File($"copy{i}.bin"));
            Assert.True(dec.ExitCode == 0, dec.ToString());
            byte[] restored = await File.ReadAllBytesAsync(dir.File($"copy{i}.bin"));
            Assert.True(data.AsSpan().SequenceEqual(restored));
        }
        Assert.DoesNotContain(dir.Files(), f => f.EndsWith(".part", StringComparison.Ordinal));
    }
}
