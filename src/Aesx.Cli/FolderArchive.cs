// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Formats.Tar;
using System.Security.Cryptography;

namespace Aesx.Cli;

/// <summary>What went into a folder archive, or came out of one.</summary>
internal readonly record struct FolderSummary(long Files, long Folders, long Bytes, long Skipped);

/// <summary>
/// Folders travel inside an AESX file as a tar archive (POSIX pax), written and read by the .NET
/// base class library and streamed straight through the cipher, so no plaintext archive ever
/// touches the disk. The AESX format itself is unchanged: decrypt a <c>.folder.aesx</c> file to
/// standard output and any <c>tar</c> can read what comes out.
/// </summary>
/// <remarks>
/// <para>
/// An archive holds one top-level folder with regular files and folders below it, each with its
/// modification time and, on Unix, its permission bits. Symbolic links and junctions are neither
/// followed nor stored. Owners, access control lists, extended attributes and alternate data
/// streams are not recorded.
/// </para>
/// <para>
/// Reading is deliberately stricter than tar: anything that is not a plain file or folder under
/// that one top-level folder, and any name that could leave the destination or mean something
/// else on this system, rejects the whole archive. The reader only ever sees authenticated
/// plaintext, so this guards against someone who knows the password, not against a stranger.
/// </para>
/// </remarks>
internal static class FolderArchive
{
    /// <summary>The double extension that marks an AESX file as an encrypted folder.</summary>
    public const string Extension = ".folder" + AesxFormat.FileExtension;

    private const int CopyBufferSize = 1 << 17;
    private const UnixFileMode PermissionBits = (UnixFileMode)0b111_111_111;
    private const UnixFileMode OwnerBits = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode DefaultFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode DefaultFolderMode = DefaultFileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Whether the name says "encrypted folder": something, then <c>.folder.aesx</c>.</summary>
    public static bool HasExtension(string path)
    {
        string name = Path.GetFileName(path);
        return name.Length > Extension.Length && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes <paramref name="directory"/> to <paramref name="destination"/> as a tar archive
    /// whose single top-level folder is called <paramref name="topName"/>.
    /// </summary>
    /// <param name="directory">Full path of the folder to archive.</param>
    /// <param name="topName">Name of the top-level folder inside the archive.</param>
    /// <param name="destination">Where the archive goes; left open.</param>
    /// <param name="excluded">Full paths to leave out: the output file, when it lies inside the folder.</param>
    /// <param name="warn">Receives one line for everything that was left out or cut short.</param>
    /// <param name="progress">Called as files are added, with the totals so far.</param>
    /// <param name="cancellationToken">Checked before every file and folder.</param>
    public static FolderSummary Write(
        string directory,
        string topName,
        Stream destination,
        IReadOnlyCollection<string> excluded,
        Action<string> warn,
        Action<FolderSummary>? progress,
        CancellationToken cancellationToken)
    {
        var walk = new Walk(excluded, warn, progress, cancellationToken);
        try
        {
            using (var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true))
            {
                walk.Writer = writer;
                walk.AddFolder(new DirectoryInfo(directory), topName + "/");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(walk.Buffer);
        }
        return walk.Summary;
    }

    /// <summary>
    /// Reads a folder archive from <paramref name="source"/> to its very end and recreates the
    /// contents of its top-level folder inside <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">The plaintext of the archive; read until it ends, which is what proves the AESX file complete.</param>
    /// <param name="destination">
    /// Full path of an existing, empty folder the caller has just created, or null to check the
    /// archive without writing anything. The caller removes it when this method throws.
    /// </param>
    /// <param name="progress">Called as files are restored, with the totals so far.</param>
    /// <param name="cancellationToken">Checked before every entry.</param>
    /// <exception cref="InvalidDataException">The data is not a folder archive that aesx would write.</exception>
    public static FolderSummary Extract(Stream source, string? destination, Action<FolderSummary>? progress, CancellationToken cancellationToken)
    {
        string? top = null;
        long files = 0, folders = 0, bytes = 0;
        var folderTimes = new List<(string Path, DateTimeOffset Time)>();
        byte[] buffer = new byte[CopyBufferSize];
        try
        {
            using (var reader = new TarReader(source, leaveOpen: true))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TarEntry? entry = NextEntry(reader);
                    if (entry is null) break;
                    if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue; // comments from another tar, nothing to restore

                    bool folder = entry.EntryType == TarEntryType.Directory;
                    if (!folder && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        throw new InvalidDataException($"the archive holds something that is neither a file nor a folder ({entry.EntryType}: '{TextSafety.Display(entry.Name)}')");

                    string relative = RelativePath(entry.Name, folder, ref top);
                    string? target = destination is null ? null : Resolve(destination, relative);
                    if (folder)
                    {
                        folders++;
                        if (target is not null)
                        {
                            CreateFolder(target);
                            folderTimes.Add((target, entry.ModificationTime));
                        }
                    }
                    else
                    {
                        files++;
                        bytes += entry.Length;
                        if (target is not null) RestoreFile(entry, target, buffer);
                    }
                    progress?.Invoke(new FolderSummary(files, folders, bytes, 0));
                }
            }

            // Tar is done after its end marker; the cipher is done only when the stream is, and a
            // file cut off at a chunk boundary would otherwise pass for complete.
            while (source.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        if (top is null) throw new InvalidDataException("the archive is empty: it does not hold a folder");

        // Last, deepest first, because creating a file inside a folder changes the folder's time.
        for (int i = folderTimes.Count - 1; i >= 0; i--) SetTime(folderTimes[i].Path, folderTimes[i].Time, folder: true);
        return new FolderSummary(files, folders, bytes, 0);
    }

    private static TarEntry? NextEntry(TarReader reader)
    {
        try
        {
            return reader.GetNextEntry(copyData: false);
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"not a folder archive: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checks an entry name and returns the part below the archive's top-level folder, with the
    /// separators of this system; empty for the top-level folder itself.
    /// </summary>
    private static string RelativePath(string name, bool folder, ref string? top)
    {
        string shown = TextSafety.Display(name);
        ReadOnlySpan<char> rest = name;
        if (folder && rest.EndsWith('/')) rest = rest[..^1];

        var parts = new List<string>();
        foreach (Range range in rest.Split('/'))
        {
            string part = rest[range].ToString();
            if (!IsSafeName(part))
                throw new InvalidDataException($"the archive holds a name that cannot be restored safely on this system: '{shown}'");
            parts.Add(part);
        }

        top ??= parts[0];
        if (!string.Equals(parts[0], top, StringComparison.Ordinal))
            throw new InvalidDataException($"the archive holds more than one top-level folder: '{shown}'");
        if (!folder && parts.Count == 1)
            throw new InvalidDataException($"the archive holds a file outside its folder: '{shown}'");
        return string.Join(Path.DirectorySeparatorChar, parts.Skip(1));
    }

    /// <summary>
    /// One path component that means the same thing in the archive and on this system: not
    /// empty, not a step sideways or up, and on Windows nothing that the file system would
    /// reject, reinterpret (a drive, a stream, a device) or quietly rename.
    /// </summary>
    internal static bool IsSafeName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Contains('\0')) return false;
        if (!OperatingSystem.IsWindows()) return true;

        if (name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false; // \ / : * ? " < > | and control characters
        if (name[^1] is ' ' or '.') return false;                                        // dropped by Windows, so two names would become one

        ReadOnlySpan<char> stem = name;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        stem = stem.TrimEnd(' ');
        if (stem.Length is < 3 or > 7) return true;
        Span<char> upper = stackalloc char[stem.Length];
        stem.ToUpperInvariant(upper);
        return upper switch
        {
            "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" => false,
            ['C', 'O', 'M', var port] => !IsPortNumber(port),
            ['L', 'P', 'T', var port] => !IsPortNumber(port),
            _ => true,
        };
    }

    /// <summary>COM1 and LPT1 through 9, and the superscript digits Windows accepts in their place.</summary>
    private static bool IsPortNumber(char c) => char.IsAsciiDigit(c) || c is '¹' or '²' or '³';

    private static string Resolve(string destination, string relative)
    {
        if (relative.Length == 0) return destination;
        string target = Path.GetFullPath(Path.Join(destination, relative));

        // The names were checked one by one; this is the belt to those braces.
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"the archive holds a name that leads outside the folder: '{TextSafety.Display(relative)}'");
        return target;
    }

    /// <summary>Folders are created for their owner only on Unix, like every file aesx decrypts.</summary>
    private static void CreateFolder(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, OwnerBits);
    }

    private static void RestoreFile(TarEntry entry, string target, byte[] buffer)
    {
        CreateFolder(Path.GetDirectoryName(target)!); // a no-op when the archive listed the folder first, as aesx does

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, // the destination started out empty, so a name that exists is a duplicate in the archive
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
        };

        // The owner's own bits come back, the executable one included; group and others get nothing.
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = entry.Mode & OwnerBits;

        using (var output = new FileStream(target, options))
        {
            Stream? data = entry.DataStream;
            if (data is not null)
            {
                int n;
                while ((n = data.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
            }
            output.Flush(flushToDisk: true);
        }
        SetTime(target, entry.ModificationTime, folder: false);
    }

    /// <summary>Timestamps are a courtesy: one the file system cannot represent is no reason to fail.</summary>
    private static void SetTime(string path, DateTimeOffset time, bool folder)
    {
        try
        {
            if (folder) Directory.SetLastWriteTimeUtc(path, time.UtcDateTime);
            else File.SetLastWriteTimeUtc(path, time.UtcDateTime);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The state of one pass over a folder tree.</summary>
    private sealed class Walk(IReadOnlyCollection<string> excluded, Action<string> warn, Action<FolderSummary>? progress, CancellationToken cancellationToken)
    {
        private static readonly StringComparison s_pathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private long _files;
        private long _folders;
        private long _bytes;
        private long _skipped;

        public TarWriter Writer { get; set; } = null!;

        /// <summary>The only buffer file contents pass through on their way to the cipher; wiped by the caller.</summary>
        public byte[] Buffer { get; } = new byte[CopyBufferSize];

        public FolderSummary Summary => new(_files, _folders, _bytes, _skipped);

        public void AddFolder(DirectoryInfo folder, string entryName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, entryName)
            {
                ModificationTime = folder.LastWriteTimeUtc,
                Mode = OperatingSystem.IsWindows() ? DefaultFolderMode : folder.UnixFileMode & PermissionBits,
            });
            _folders++;

            FileSystemInfo[] children;
            try
            {
                children = folder.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"cannot read the folder '{TextSafety.Display(entryName)}': {ex.Message}", ex);
            }

            // Sorted, so the order inside the archive does not depend on the file system's mood.
            Array.Sort(children, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (FileSystemInfo child in children)
            {
                string name = entryName + child.Name;
                if (IsExcluded(child.FullName)) continue;
                if (child.LinkTarget is not null)
                {
                    // Following a link could archive half the disk or never end; storing one would
                    // let an archive plant a link on whoever unpacks it.
                    _skipped++;
                    warn($"not included (a link): {TextSafety.Display(name)}");
                }
                else if (child is DirectoryInfo subfolder)
                {
                    AddFolder(subfolder, name + "/");
                }
                else
                {
                    AddFile((FileInfo)child, name);
                }
            }
        }

        private void AddFile(FileInfo file, string entryName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string shown = TextSafety.Display(entryName);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                ModificationTime = file.LastWriteTimeUtc,
                Mode = OperatingSystem.IsWindows() ? DefaultFileMode : file.UnixFileMode & PermissionBits,
            };

            // Something that reports no size is written as an empty file without being opened:
            // on Unix that is also what named pipes, sockets and devices report, and opening a
            // pipe would wait for ever.
            long length = 0;
            if (file.Length == 0)
            {
                Writer.WriteEntry(entry);
            }
            else
            {
                FileStream source;
                try
                {
                    source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, FileOptions.SequentialScan);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"cannot read '{shown}': {ex.Message}", ex);
                }

                using (source)
                {
                    length = source.Length;
                    using var exact = new ExactLengthStream(source, length, shown, Buffer);
                    entry.DataStream = exact;
                    Writer.WriteEntry(entry);
                    if (source.Length > length)
                    {
                        _skipped++;
                        warn($"grew while it was being read, so only its first {length:N0} bytes are included: {shown}");
                    }
                }
            }

            _files++;
            _bytes += length;
            progress?.Invoke(Summary);
        }

        private bool IsExcluded(string fullPath)
        {
            foreach (string path in excluded)
            {
                if (string.Equals(path, fullPath, s_pathComparison)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Hands the tar writer exactly the number of bytes that it has already promised in the
    /// entry's header. A file that shrinks while it is read must fail loudly, because a short
    /// entry would shift everything after it and ruin the archive without a sound; one that grows
    /// is cut off at the promised length. Copying goes through one caller-owned buffer, so that
    /// file contents do not linger in pooled arrays.
    /// </summary>
    private sealed class ExactLengthStream(Stream inner, long length, string shownName, byte[] copyBuffer) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true; // the tar writer only asks for Length and Position
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = length - _position;
            if (left == 0 || buffer.IsEmpty) return 0;
            if (buffer.Length > left) buffer = buffer[..(int)left];

            int n;
            try
            {
                n = inner.Read(buffer);
            }
            catch (IOException ex)
            {
                throw new IOException($"cannot read '{shownName}': {ex.Message}", ex);
            }
            if (n <= 0) throw new IOException($"'{shownName}' became shorter while it was being read");
            _position += n;
            return n;
        }

        public override void CopyTo(Stream destination, int bufferSize)
        {
            int n;
            while ((n = Read(copyBuffer.AsSpan())) > 0) destination.Write(copyBuffer, 0, n);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
