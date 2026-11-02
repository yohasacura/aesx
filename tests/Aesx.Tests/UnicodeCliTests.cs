// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aesx.Tests;

/// <summary>Non-Latin scripts, invisible characters, hostile names and the unusual ways a password can arrive.</summary>
public class UnicodeCliTests
{
    private const string Pw = "test-password";
    private static readonly string[] FastKdf = ["--iterations", "1000"];

    public static TheoryData<string> FileNames =>
    [
        "דוח_2025 סופי.txt",
        "报告 最终版.txt",
        "日本語のファイル.txt",
        "تقرير مالي.txt",
        "фото 🔐🗂️.jpg",
        "café (decomposed).txt",
        "zero​width.txt",
        "mixed עברית and 中文 and emoji 🎉.bin",
    ];

    [Theory]
    [MemberData(nameof(FileNames))]
    public async Task RoundTripsFileNamesInAnyScript(string name)
    {
        using var dir = new TempDir();
        byte[] data = RandomNumberGenerator.GetBytes(3000);
        await File.WriteAllBytesAsync(dir.File(name), data);

        CliResult enc = await Cli.RunAsync(null, [dir.File(name), "-p", Pw, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Contains(name, enc.Stdout, StringComparison.Ordinal); // printed as is: nothing hazardous in it
        File.Delete(dir.File(name));

        CliResult dec = await Cli.RunAsync(null, dir.File(name + ".aesx"), "-p", Pw);
        Assert.True(dec.ExitCode == 0, dec.ToString());
        byte[] restored = await File.ReadAllBytesAsync(dir.File(name));
        Assert.True(data.AsSpan().SequenceEqual(restored));
    }

    [Fact]
    public async Task ABidiOverrideInAFileNameIsShownAsAnEscapeNotObeyed()
    {
        using var dir = new TempDir();
        const string name = "report‮txt.exe"; // renders as "reportexe.txt" if printed raw
        await File.WriteAllTextAsync(dir.File(name), "x");

        CliResult enc = await Cli.RunAsync(null, ["encrypt", dir.File(name), "-p", Pw, .. FastKdf]);

        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.DoesNotContain("‮", enc.Stdout, StringComparison.Ordinal);
        Assert.Contains("report\\u{202E}txt.exe", enc.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(dir.File(name + ".aesx")));
    }

    [Fact]
    public async Task AnEscapeSequenceInAFileNameCannotReachTheTerminal()
    {
        if (OperatingSystem.IsWindows()) return; // Windows does not allow control characters in names

        using var dir = new TempDir();
        const string name = "evil\u001B]0;owned\u0007.txt";
        await File.WriteAllTextAsync(dir.File(name), "x");

        CliResult enc = await Cli.RunAsync(null, ["encrypt", dir.File(name), "-p", Pw, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.DoesNotContain('\u001B', enc.Stdout);
        Assert.DoesNotContain('\u0007', enc.Stdout);
        Assert.Contains("evil\\u{001B}]0;owned\\u{0007}.txt", enc.Stdout, StringComparison.Ordinal);

        CliResult missing = await Cli.RunAsync(null, "encrypt", dir.File("nope\u001Bc.txt"), "-p", Pw);
        Assert.Equal(1, missing.ExitCode);
        Assert.DoesNotContain('\u001B', missing.Stderr);
    }

    public static TheoryData<string> Passwords =>
    [
        "סיסמה סודית מאוד",
        "密码 很长 的 密码",
        "パスワード🔐",
        "كلمة المرور",
        "  mixed עברית 中文 🔐 with spaces  ",
        "ümlaut ñ ß ø",
        "zero​width",
        "é decomposed and é composed",
    ];

    [Theory]
    [MemberData(nameof(Passwords))]
    public async Task PasswordsInAnyScriptAgreeAcrossAllInputPaths(string password)
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("a.txt"), "a");
        await File.WriteAllTextAsync(dir.File("b.txt"), "b");

        // Piped bytes (UTF-8) must equal the UTF-8 of the same text given on the command line ...
        CliResult enc = await Cli.RunAsync(password + "\n", ["encrypt", dir.File("a.txt"), .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("a.txt.aesx"), "-p", password, "-o", dir.File("a.out"));
        Assert.True(dec.ExitCode == 0, dec.ToString());

        // ... and the same text in the environment variable.
        CliResult enc2 = await Cli.RunAsync(null, ["encrypt", dir.File("b.txt"), "-p", password, .. FastKdf]);
        Assert.True(enc2.ExitCode == 0, enc2.ToString());
        var env = new Dictionary<string, string?> { [Cli.PasswordVariable] = password };
        CliResult dec2 = await Cli.RunWithAsync(new CliRun { Environment = env }, "decrypt", dir.File("b.txt.aesx"), "-o", dir.File("b.out"));
        Assert.True(dec2.ExitCode == 0, dec2.ToString());
        Assert.Equal("b", await File.ReadAllTextAsync(dir.File("b.out")));
    }

    [Fact]
    public async Task CommandLineBeatsEnvironmentWhichBeatsStandardInput()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        var env = new Dictionary<string, string?> { [Cli.PasswordVariable] = "from-env" };

        CliResult enc = await Cli.RunWithAsync(new CliRun { Stdin = "from-stdin\n", Environment = env }, ["encrypt", dir.File("x.txt"), "-p", "from-arg", .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", dir.File("x.txt.aesx"), "-p", "from-arg", "-o", dir.File("1.out"))).ExitCode);
        Assert.Equal(1, (await Cli.RunAsync(null, "decrypt", dir.File("x.txt.aesx"), "-p", "from-env", "-o", dir.File("2.out"))).ExitCode);

        CliResult enc2 = await Cli.RunWithAsync(new CliRun { Stdin = "from-stdin\n", Environment = env }, ["encrypt", dir.File("x.txt"), "-o", dir.File("y.aesx"), .. FastKdf]);
        Assert.True(enc2.ExitCode == 0, enc2.ToString());
        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", dir.File("y.aesx"), "-p", "from-env", "-o", dir.File("3.out"))).ExitCode);
        Assert.Equal(1, (await Cli.RunAsync(null, "decrypt", dir.File("y.aesx"), "-p", "from-stdin", "-o", dir.File("4.out"))).ExitCode);
    }

    [Fact]
    public async Task WarnsAboutInvisibleCharactersInAPassword()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");

        CliResult run = await Cli.RunAsync(null, ["encrypt", dir.File("x.txt"), "-p", "pass​word‏", .. FastKdf]);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("invisible characters (U+200B, U+200F)", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APasswordFileSavedAsUtf16IsUnderstood()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        const string password = "пароль🔐 secret";
        byte[] littleEndian = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(password + "\r\n")];
        byte[] bigEndian = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(password + "\n")];

        CliResult le = await Cli.RunWithAsync(new CliRun { StdinBytes = littleEndian }, ["encrypt", dir.File("x.txt"), "-o", dir.File("le.aesx"), .. FastKdf]);
        Assert.True(le.ExitCode == 0, le.ToString());
        CliResult be = await Cli.RunWithAsync(new CliRun { StdinBytes = bigEndian }, ["encrypt", dir.File("x.txt"), "-o", dir.File("be.aesx"), .. FastKdf]);
        Assert.True(be.ExitCode == 0, be.ToString());

        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", dir.File("le.aesx"), "-p", password, "-o", dir.File("le.out"))).ExitCode);
        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", dir.File("be.aesx"), "-p", password, "-o", dir.File("be.out"))).ExitCode);
    }

    [Fact]
    public async Task NulBytesInAPipedPasswordArePreserved()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");

        CliResult enc = await Cli.RunWithAsync(new CliRun { StdinBytes = "pa\0ss\n"u8.ToArray() }, ["encrypt", dir.File("x.txt"), .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());

        Assert.Equal(1, (await Cli.RunAsync("pass\n", "decrypt", dir.File("x.txt.aesx"), "-o", dir.File("1.out"))).ExitCode);
        Assert.Equal(0, (await Cli.RunWithAsync(new CliRun { StdinBytes = "pa\0ss\n"u8.ToArray() }, "decrypt", dir.File("x.txt.aesx"), "-o", dir.File("2.out"))).ExitCode);
    }

    [Fact]
    public async Task AnOverlongStandardInputLineIsRefusedQuickly()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");
        byte[] twoMebibytesNoNewline = new byte[2 << 20];
        Array.Fill(twoMebibytesNoNewline, (byte)'x');

        long started = Stopwatch.GetTimestamp();
        CliResult run = await Cli.RunWithAsync(new CliRun { StdinBytes = twoMebibytesNoNewline }, ["encrypt", dir.File("x.txt"), .. FastKdf]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("longer than 1 MiB", run.Stderr, StringComparison.Ordinal);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20));
        Assert.Equal(["x.txt"], dir.Files());
    }

    [Fact]
    public async Task StreamsToStandardOutputWhenAskedTo()
    {
        using var dir = new TempDir();
        byte[] data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 7);
        await File.WriteAllBytesAsync(dir.File("data.bin"), data);

        CliResult enc = await Cli.RunAsync(null, ["encrypt", dir.File("data.bin"), "-p", Pw, "-o", "-", .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Contains("standard output", enc.Stderr, StringComparison.Ordinal); // status moved off stdout
        Assert.Equal(AesxFormat.HeaderSize + data.Length + 3 * AesxFormat.TagSize, enc.StdoutBytes.Length);
        Assert.Equal(["data.bin"], dir.Files());
        await File.WriteAllBytesAsync(dir.File("streamed.aesx"), enc.StdoutBytes);

        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("streamed.aesx"), "-p", Pw, "-o", "-");
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.True(data.AsSpan().SequenceEqual(dec.StdoutBytes));

        CliResult quiet = await Cli.RunAsync(null, "decrypt", dir.File("streamed.aesx"), "-p", Pw, "-o", "-", "--quiet");
        Assert.True(quiet.ExitCode == 0, quiet.ToString());
        Assert.Equal("", quiet.Stderr);

        Assert.Equal(2, (await Cli.RunAsync(null, "encrypt", dir.File("data.bin"), "-p", Pw, "-o", "-", "--delete")).ExitCode);
        Assert.Equal(2, (await Cli.RunAsync(null, "encrypt", dir.File("data.bin"), dir.File("streamed.aesx"), "-p", Pw, "-o", "-")).ExitCode);
        Assert.Equal(1, (await Cli.RunAsync(null, "decrypt", dir.File("streamed.aesx"), "-p", "wrong", "-o", "-")).ExitCode);
    }

    [Fact]
    public async Task InvalidPathCharactersAreReportedCleanly()
    {
        if (!OperatingSystem.IsWindows()) return; // only Windows rejects characters in paths

        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("x.txt"), "x");

        CliResult run = await Cli.RunAsync(null, "encrypt", dir.File("x.txt"), "-p", Pw, "-o", dir.File("bad<name>.aesx"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("invalid output path", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(["x.txt"], dir.Files());
    }
}
