// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Formats.Tar;
using System.Security.Cryptography;

namespace Aesx.Tests;

/// <summary>Folders in and out, end to end through the real executable.</summary>
public class FolderCliTests
{
    private const string Pw = "test-password";
    private static readonly string[] FastKdf = ["--iterations", "1000"];

    /// <summary>A small tree with everything that tends to go wrong: depth, emptiness, scripts, a file larger than a chunk.</summary>
    private static async Task<string> CreateTreeAsync(TempDir dir, string name = "photos")
    {
        string root = dir.File(name);
        Directory.CreateDirectory(Path.Combine(root, "2024", "empty folder"));
        Directory.CreateDirectory(Path.Combine(root, "вложенная папка 日本語"));
        await File.WriteAllBytesAsync(Path.Combine(root, "big.bin"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 4321));
        await File.WriteAllTextAsync(Path.Combine(root, "2024", "notes.txt"), "hello");
        await File.WriteAllBytesAsync(Path.Combine(root, "2024", "zero.dat"), []);
        await File.WriteAllTextAsync(Path.Combine(root, "вложенная папка 日本語", "файл 🔐.txt"), "содержимое");
        return root;
    }

    /// <summary>Every folder and file below a root, with a hash of each file's contents.</summary>
    private static string[] Snapshot(string root)
        => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                return Directory.Exists(path) ? relative + "/" : relative + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Names of what lies directly in a folder, sorted.</summary>
    private static string[] Entries(string path)
        => [.. Directory.GetFileSystemEntries(path).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task EncryptsAFolderAndBringsItBack()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        string[] before = Snapshot(root);

        CliResult enc = await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Contains("Encrypting folder", enc.Stdout, StringComparison.Ordinal);
        Assert.Contains("4 files", enc.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(dir.File("photos.folder.aesx")));
        Assert.Equal(before, Snapshot(root)); // the original is untouched

        Directory.Move(root, dir.File("original"));
        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("photos.folder.aesx"), "-p", Pw);
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.Contains("-> folder", dec.Stdout, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(root));
        Assert.Equal(["original", "photos", "photos.folder.aesx"], Entries(dir.Path));
    }

    [Fact]
    public async Task WithoutACommandAFolderIsEncryptedAndAFolderArchiveUnpacked()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir, "папка с документами");
        string[] before = Snapshot(root);

        Assert.Equal(0, (await Cli.RunAsync(null, [root, "-p", Pw, .. FastKdf])).ExitCode);
        Directory.Delete(root, recursive: true);
        Assert.Equal(0, (await Cli.RunAsync(null, root + ".folder.aesx", "-p", Pw)).ExitCode);

        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    public async Task ATrailingSeparatorAndARelativeNameMakeNoDifference()
    {
        using var dir = new TempDir();
        await CreateTreeAsync(dir);

        CliResult enc = await Cli.RunInAsync(dir.Path, null, ["encrypt", "photos" + Path.DirectorySeparatorChar, "-p", Pw, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.True(File.Exists(dir.File("photos.folder.aesx")));

        // "." has no name of its own: the folder's real name is used, and the file lands beside it.
        CliResult dot = await Cli.RunInAsync(dir.File("photos"), null, ["encrypt", ".", "-p", Pw, "-f", .. FastKdf]);
        Assert.True(dot.ExitCode == 0, dot.ToString());
        Assert.False(File.Exists(Path.Combine(dir.File("photos"), ".folder.aesx")));
    }

    [Fact]
    public async Task TheOutputOptionNamesTheFolderOrADirectoryToPutItIn()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        string[] before = Snapshot(root);
        Directory.CreateDirectory(dir.File("vault"));
        Directory.CreateDirectory(dir.File("out"));

        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, "-o", dir.File("vault"), .. FastKdf])).ExitCode);
        string archive = dir.File("vault/photos.folder.aesx");
        Assert.True(File.Exists(archive));

        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", archive, "-p", Pw, "-o", dir.File("out"))).ExitCode);
        Assert.Equal(before, Snapshot(dir.File("out/photos")));

        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", archive, "-p", Pw, "-o", dir.File("renamed"))).ExitCode);
        Assert.Equal(before, Snapshot(dir.File("renamed")));
    }

    [Fact]
    public async Task AnExistingFolderIsNeverTouchedNotEvenWithForce()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, .. FastKdf])).ExitCode);
        await File.WriteAllTextAsync(Path.Combine(root, "added later.txt"), "keep me");
        string[] before = Snapshot(root);

        CliResult plain = await Cli.RunAsync(null, "decrypt", dir.File("photos.folder.aesx"), "-p", Pw);
        CliResult forced = await Cli.RunAsync(null, "decrypt", dir.File("photos.folder.aesx"), "-p", Pw, "--force");

        Assert.Equal(1, plain.ExitCode);
        Assert.Equal(1, forced.ExitCode);
        Assert.Contains("already exists", plain.Stderr, StringComparison.Ordinal);
        Assert.Contains("not even with --force", forced.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    public async Task AWrongPasswordOrADamagedArchiveLeavesNothingBehind()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, .. FastKdf])).ExitCode);
        Directory.Delete(root, recursive: true);
        string archive = dir.File("photos.folder.aesx");

        CliResult wrong = await Cli.RunAsync(null, "decrypt", archive, "-p", "not-the-password");
        Assert.Equal(1, wrong.ExitCode);
        Assert.Contains("wrong password", wrong.Stderr, StringComparison.Ordinal);
        Assert.Equal(["photos.folder.aesx"], Entries(dir.Path));

        // Damage in the last chunk: by then most of the folder has been unpacked into the
        // temporary one, and all of it has to go.
        byte[] ct = await File.ReadAllBytesAsync(archive);
        ct[^20] ^= 0x01;
        await File.WriteAllBytesAsync(archive, ct);
        CliResult damaged = await Cli.RunAsync(null, "decrypt", archive, "-p", Pw);
        Assert.Equal(1, damaged.ExitCode);
        Assert.Contains("tampered", damaged.Stderr, StringComparison.Ordinal);
        Assert.Equal(["photos.folder.aesx"], Entries(dir.Path));

        // Cut off exactly at a chunk boundary: every chunk that is left is genuine, but the last
        // of them is not the one that was marked as final.
        await File.WriteAllBytesAsync(archive, ct[..(AesxFormat.HeaderSize + 2 * (AesxFormat.ChunkSize + AesxFormat.TagSize))]);
        CliResult truncated = await Cli.RunAsync(null, "decrypt", archive, "-p", Pw);
        Assert.Equal(1, truncated.ExitCode);
        Assert.Equal(["photos.folder.aesx"], Entries(dir.Path));
    }

    [Fact]
    public async Task DeleteDoesNotRemoveFoldersButDoesRemoveTheArchiveAfterUnpacking()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        string[] before = Snapshot(root);

        CliResult refused = await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, "--delete", .. FastKdf]);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("--delete does not remove folders", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(root));
        Assert.False(File.Exists(dir.File("photos.folder.aesx")));

        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, .. FastKdf])).ExitCode);
        Directory.Delete(root, recursive: true);
        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("photos.folder.aesx"), "-p", Pw, "--delete");
        Assert.True(dec.ExitCode == 0, dec.ToString());
        Assert.False(File.Exists(dir.File("photos.folder.aesx")));
        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    public async Task AnOutputFileInsideTheFolderIsNotSwallowedByIt()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        string[] before = Snapshot(root);
        string inside = Path.Combine(root, "backup.folder.aesx");

        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, "-o", inside, .. FastKdf])).ExitCode);
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, "-o", inside, "--force", .. FastKdf])).ExitCode); // the previous output is there now

        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", inside, "-p", Pw, "-o", dir.File("restored"))).ExitCode);
        Assert.Equal(before, Snapshot(dir.File("restored")));
    }

    [Fact]
    public async Task TheDecryptedBytesAreAnOrdinaryTarArchive()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, .. FastKdf])).ExitCode);

        CliResult raw = await Cli.RunAsync(null, "decrypt", dir.File("photos.folder.aesx"), "-p", Pw, "-o", "-", "--quiet");
        Assert.True(raw.ExitCode == 0, raw.ToString());

        var names = new List<string>();
        using (var reader = new TarReader(new MemoryStream(raw.StdoutBytes)))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                names.Add($"{entry.EntryType} {entry.Name}");
                Assert.Equal(0, entry.Uid);
                Assert.Equal(0, entry.Gid);
                Assert.True(entry is PaxTarEntry pax && string.IsNullOrEmpty(pax.UserName) && string.IsNullOrEmpty(pax.GroupName));
            }
        }
        Assert.Equal(
            [
                "Directory photos/",
                "Directory photos/2024/",
                "Directory photos/2024/empty folder/",
                "RegularFile photos/2024/notes.txt",
                "RegularFile photos/2024/zero.dat",
                "RegularFile photos/big.bin",
                "Directory photos/вложенная папка 日本語/",
                "RegularFile photos/вложенная папка 日本語/файл 🔐.txt",
            ],
            names);

    }

    [Fact]
    public async Task AFolderArchiveUnderAnotherNameDecryptsToATarFile()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);

        CliResult enc = await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, "-o", dir.File("backup.tar.aesx"), .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Contains("will give a tar archive", enc.Stderr, StringComparison.Ordinal);

        CliResult dec = await Cli.RunAsync(null, "decrypt", dir.File("backup.tar.aesx"), "-p", Pw);
        Assert.True(dec.ExitCode == 0, dec.ToString());
        using var reader = new TarReader(File.OpenRead(dir.File("backup.tar")));
        Assert.Equal("photos/", reader.GetNextEntry()?.Name);
        Assert.Equal(["backup.tar", "backup.tar.aesx", "photos"], Entries(dir.Path));
    }

    [Fact]
    public async Task LinksAreLeftOutAndSaidSo()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        await File.WriteAllTextAsync(dir.File("outside.txt"), "not part of the folder");
        Directory.CreateDirectory(dir.File("elsewhere"));
        await File.WriteAllTextAsync(dir.File("elsewhere/secret.txt"), "not part of the folder either");
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "file-link.txt"), dir.File("outside.txt"));
            Directory.CreateSymbolicLink(Path.Combine(root, "folder-link"), dir.File("elsewhere"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // creating links needs a privilege on some Windows setups; nothing to test then
        }

        CliResult enc = await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, .. FastKdf]);
        Assert.True(enc.ExitCode == 0, enc.ToString());
        Assert.Contains("not included (a link): photos/file-link.txt", enc.Stderr, StringComparison.Ordinal);
        Assert.Contains("not included (a link): photos/folder-link", enc.Stderr, StringComparison.Ordinal);
        Assert.Contains("2 not included in full", enc.Stdout, StringComparison.Ordinal);

        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", dir.File("photos.folder.aesx"), "-p", Pw, "-o", dir.File("restored"))).ExitCode);
        Assert.DoesNotContain(Snapshot(dir.File("restored")), e => e.Contains("link", StringComparison.Ordinal) || e.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnpackedFilesBelongToTheirOwnerOnlyAndKeepTheExecutableBitAndTheTime()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        string script = Path.Combine(root, "run.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n");
        var written = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(root, "2024", "notes.txt"), written);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "2024", "empty folder"), written);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(Path.Combine(root, "big.bin"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead);
        }

        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, .. FastKdf])).ExitCode);
        string restored = dir.File("restored");
        Assert.Equal(0, (await Cli.RunAsync(null, "decrypt", dir.File("photos.folder.aesx"), "-p", Pw, "-o", restored)).ExitCode);

        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(restored, "2024", "notes.txt")));
        Assert.Equal(written, Directory.GetLastWriteTimeUtc(Path.Combine(restored, "2024", "empty folder")));
        if (OperatingSystem.IsWindows()) return; // POSIX permissions only

        const UnixFileMode ownerAll = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(ownerAll, File.GetUnixFileMode(Path.Combine(restored, "run.sh")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(restored, "big.bin")));
        Assert.Equal(ownerAll, File.GetUnixFileMode(restored));
        Assert.Equal(ownerAll, File.GetUnixFileMode(Path.Combine(restored, "2024")));
    }

    [Fact]
    public async Task VerifyWalksTheArchiveInsideAnEncryptedFolder()
    {
        using var dir = new TempDir();
        string root = await CreateTreeAsync(dir);
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", root, "-p", Pw, .. FastKdf])).ExitCode);
        Directory.Delete(root, recursive: true);

        CliResult ok = await Cli.RunAsync(null, "verify", dir.File("photos.folder.aesx"), "-p", Pw);
        Assert.True(ok.ExitCode == 0, ok.ToString());
        Assert.Contains("OK: a folder with 4 files", ok.Stdout, StringComparison.Ordinal);
        Assert.Equal(["photos.folder.aesx"], Entries(dir.Path));

        // A file that only claims to be a folder is not one, however well it decrypts.
        await File.WriteAllTextAsync(dir.File("fake.folder"), "just some text, long enough to not look like an empty archive either");
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", dir.File("fake.folder"), "-p", Pw, .. FastKdf])).ExitCode);
        CliResult fake = await Cli.RunAsync(null, "verify", dir.File("fake.folder.aesx"), "-p", Pw);
        Assert.Equal(1, fake.ExitCode);
    }

    public static TheoryData<string> HostileArchives =>
    [
        "parent",
        "parent in the middle",
        "absolute",
        "second top-level folder",
        "file outside the folder",
        "symbolic link",
        "hard link",
        "same file twice",
        "empty name",
        "not a tar at all",
        "nothing in it",
    ];

    [Theory]
    [MemberData(nameof(HostileArchives))]
    public async Task AnArchiveThatAesxWouldNotWriteIsRejectedWithoutATrace(string kind)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("work"));
        string plain = dir.File("work/evil.folder");
        using (FileStream file = File.Create(plain))
        {
            if (kind == "not a tar at all")
            {
                file.Write(RandomNumberGenerator.GetBytes(5000));
            }
            else
            {
                using var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: true);
                if (kind != "nothing in it") tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "evil/"));
                switch (kind)
                {
                    case "parent": AddFile(tar, "evil/../escaped.txt"); break;
                    case "parent in the middle": AddFile(tar, "evil/sub/../../escaped.txt"); break;
                    case "absolute": AddFile(tar, "/" + dir.File("escaped.txt").Replace('\\', '/').TrimStart('/')); break;
                    case "second top-level folder": AddFile(tar, "other/escaped.txt"); break;
                    case "file outside the folder": AddFile(tar, "escaped.txt"); break;
                    case "symbolic link": tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "evil/link") { LinkName = dir.Path }); break;
                    case "hard link": tar.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "evil/link") { LinkName = "evil/" }); break;
                    case "same file twice": AddFile(tar, "evil/a.txt"); AddFile(tar, "evil/a.txt"); break;
                    case "empty name": AddFile(tar, "evil//a.txt"); break;
                    default: break;
                }
            }
        }
        Assert.Equal(0, (await Cli.RunAsync(null, ["encrypt", plain, "-p", Pw, .. FastKdf])).ExitCode);
        File.Delete(plain);

        CliResult verify = await Cli.RunAsync(null, "verify", plain + ".aesx", "-p", Pw);
        CliResult decrypt = await Cli.RunAsync(null, "decrypt", plain + ".aesx", "-p", Pw);

        // "same file twice" is a well-formed archive; only writing it out shows the clash.
        if (kind != "same file twice") Assert.True(verify.ExitCode == 1, verify.ToString());
        Assert.True(decrypt.ExitCode == 1, decrypt.ToString());
        Assert.Equal(["work/evil.folder.aesx"], dir.Files());
        Assert.Equal(["work"], Entries(dir.Path));
        Assert.Equal(["evil.folder.aesx"], Entries(dir.File("work")));
    }

    private static void AddFile(TarWriter tar, string name)
        => tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream("escaped"u8.ToArray()) });
}
