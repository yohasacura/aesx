// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Formats.Tar;
using Aesx.Cli;

namespace Aesx.Tests;

/// <summary>The parts of folder archiving that are awkward to reach through the executable.</summary>
public class FolderArchiveTests
{
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("файл 🔐 (копия).txt")]
    [InlineData(".hidden")]
    [InlineData("a b")]
    [InlineData("CONSOLE")]          // only the exact device names are reserved
    [InlineData("COM10")]
    [InlineData("nul-and-void.txt")]
    [InlineData("LPT")]
    public void OrdinaryNamesAreSafeEverywhere(string name) => Assert.True(FolderArchive.IsSafeName(name));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\0b")]
    public void NamesThatAreNoNamesAreSafeNowhere(string name) => Assert.False(FolderArchive.IsSafeName(name));

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL.txt")]          // the extension does not rescue a device name
    [InlineData("aux.tar.gz")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    [InlineData("COM¹")]
    [InlineData("CONIN$")]
    [InlineData("prn .txt")]         // nor do spaces before the dot
    [InlineData("name.")]            // Windows would drop the dot and the space,
    [InlineData("name ")]            // turning two names into one
    [InlineData("a:b")]              // a drive, or an alternate data stream
    [InlineData("a\\b")]             // a separator in disguise
    [InlineData("what?")]
    [InlineData("pipe|")]
    [InlineData("star*")]
    [InlineData("quote\"")]
    [InlineData("tab\there")]
    public void NamesWindowsWouldMisreadAreUnsafeOnWindowsOnly(string name)
        => Assert.Equal(!OperatingSystem.IsWindows(), FolderArchive.IsSafeName(name));

    [Theory]
    [InlineData("photos.folder.aesx", true)]
    [InlineData("PHOTOS.FOLDER.AESX", true)]
    [InlineData("a.b.folder.aesx", true)]
    [InlineData(".folder.aesx", false)]       // a name is needed in front
    [InlineData("photos.aesx", false)]
    [InlineData("photos.tar.aesx", false)]
    [InlineData("photos.folder", false)]
    [InlineData("folder.aesx", false)]
    public void TheDoubleExtensionMarksAnEncryptedFolder(string name, bool expected)
    {
        Assert.Equal(expected, FolderArchive.HasExtension(name));
        Assert.Equal(expected, FolderArchive.HasExtension(Path.Combine("some", "where", name)));
    }

    [Fact]
    public void AFileThatShrinksWhileItIsReadStopsTheArchive()
    {
        using var dir = new TempDir();
        string victim = CreateTreeWithOneLargeFile(dir, out string root);
        using var destination = new TriggerStream(afterBytes: 64 * 1024, () => Resize(victim, 1000));

        var ex = Assert.Throws<IOException>(() => FolderArchive.Write(root, "tree", destination, [], _ => { }, null, CancellationToken.None));

        Assert.Contains("became shorter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tree/log.bin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatGrowsWhileItIsReadIsCutAtItsOriginalLengthAndReported()
    {
        using var dir = new TempDir();
        string victim = CreateTreeWithOneLargeFile(dir, out string root);
        long original = new FileInfo(victim).Length;
        using var destination = new TriggerStream(afterBytes: 64 * 1024, () => Resize(victim, original + 12345));
        var warnings = new List<string>();

        FolderSummary summary = FolderArchive.Write(root, "tree", destination, [], warnings.Add, null, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Contains(warnings, w => w.Contains("grew while it was being read", StringComparison.Ordinal) && w.Contains("tree/log.bin", StringComparison.Ordinal));

        // The archive is still well-formed: the entry is exactly as long as its header promised.
        using var reader = new TarReader(new MemoryStream(destination.ToArray()));
        Assert.Equal("tree/", reader.GetNextEntry()?.Name);
        TarEntry? file = reader.GetNextEntry();
        Assert.Equal("tree/log.bin", file?.Name);
        Assert.Equal(original, file?.Length);
        Assert.Equal("tree/zz-after.txt", reader.GetNextEntry()?.Name);
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public void CheckingAnArchiveWritesNothingAndCountsWhatIsInIt()
    {
        using var dir = new TempDir();
        CreateTreeWithOneLargeFile(dir, out string root);
        using var archive = new MemoryStream();
        FolderSummary written = FolderArchive.Write(root, "tree", archive, [], _ => { }, null, CancellationToken.None);
        archive.Position = 0;

        FolderSummary read = FolderArchive.Extract(archive, destination: null, progress: null, CancellationToken.None);

        Assert.Equal(written with { Skipped = 0 }, read);
        Assert.Equal(2, read.Files);
        Assert.Equal(1, read.Folders);
        Assert.Equal(archive.Length, archive.Position); // read to the very end
    }

    [Fact]
    public void ExcludedPathsAndCancellationAreHonoured()
    {
        using var dir = new TempDir();
        string victim = CreateTreeWithOneLargeFile(dir, out string root);
        using var archive = new MemoryStream();

        FolderSummary summary = FolderArchive.Write(root, "tree", archive, [victim], _ => { }, null, CancellationToken.None);
        Assert.Equal(1, summary.Files);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => FolderArchive.Write(root, "tree", new MemoryStream(), [], _ => { }, null, cancelled.Token));
        archive.Position = 0;
        Assert.Throws<OperationCanceledException>(() => FolderArchive.Extract(archive, null, null, cancelled.Token));
    }

    private static string CreateTreeWithOneLargeFile(TempDir dir, out string root)
    {
        root = dir.File("tree");
        Directory.CreateDirectory(root);
        string large = Path.Combine(root, "log.bin");
        File.WriteAllBytes(large, new byte[4 * 1024 * 1024]);
        File.WriteAllText(Path.Combine(root, "zz-after.txt"), "after");
        return large;
    }

    private static void Resize(string path, long length)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.SetLength(length);
    }

    /// <summary>Collects what is written and runs an action once, when a given amount has arrived.</summary>
    private sealed class TriggerStream(long afterBytes, Action trigger) : MemoryStream
    {
        private bool _fired;

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Fire();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            Fire();
        }

        private void Fire()
        {
            if (_fired || Length < afterBytes) return;
            _fired = true;
            trigger();
        }
    }
}
