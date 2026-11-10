// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Aesx;

/// <summary>
/// Reads and writes the AESX container format (version 1): a password-protected file
/// made of independently authenticated AES-256-GCM chunks.
/// </summary>
/// <remarks>
/// <para>The format is specified in <c>docs/FORMAT.md</c>. In short:</para>
/// <code>
/// Header (29 bytes)
///   offset  size  field
///   0       4     magic "AESX"
///   4       1     format version = 1
///   5       4     PBKDF2 iteration count, uint32 little-endian
///   9       16    random salt
///   25      4     random nonce prefix
///
/// Body: chunks, each stored as  ciphertext || 16-byte GCM tag.
/// Every chunk except the last carries exactly ChunkSize bytes of plaintext;
/// the last carries 0..ChunkSize bytes. An empty file is one empty final chunk.
///
///   key   = PBKDF2-HMAC-SHA256(password, salt, iterations) -> 32 bytes
///   nonce = noncePrefix(4) || chunkIndex (uint64 big-endian)
///   aad   = header(29)     || chunkIndex (uint64 big-endian) || isLast (0 or 1)
/// </code>
/// <para>
/// The key is unique per file (random salt), the nonce is unique per chunk (counter), and
/// the additional authenticated data pins every chunk to its position, to the header and to
/// the "final chunk" flag. Any bit flip, reordering, duplication, truncation or header edit
/// therefore fails authentication, and no plaintext is emitted for a chunk until its tag has
/// verified.
/// </para>
/// <para>
/// <see cref="Encrypt(Stream, Stream, ReadOnlySpan{byte}, int, CancellationToken)"/> and
/// <see cref="Decrypt"/> copy from one stream to another. When the data is produced or consumed
/// piece by piece instead (an archive being written, a parser reading), use
/// <see cref="AesxEncryptStream"/> and <see cref="AesxDecryptStream"/>; all four share one
/// implementation of the chunk cryptography.
/// </para>
/// <para>
/// All cryptography comes from <see cref="System.Security.Cryptography"/>; the library has no
/// other dependencies. Structural problems raise <see cref="InvalidDataException"/>; a wrong
/// password or tampered data raises <see cref="CryptographicException"/>; a cancelled token
/// raises <see cref="OperationCanceledException"/> between chunks.
/// </para>
/// </remarks>
public static class AesxFormat
{
    /// <summary>File name extension conventionally used for AESX files.</summary>
    public const string FileExtension = ".aesx";

    /// <summary>The format version written by, and accepted by, this implementation.</summary>
    public const byte Version = 1;

    /// <summary>Size of the random PBKDF2 salt in bytes.</summary>
    public const int SaltSize = 16;

    /// <summary>Size of the random per-file nonce prefix in bytes.</summary>
    public const int NoncePrefixSize = 4;

    /// <summary>Size of the AES-GCM nonce in bytes: the prefix followed by a 64-bit chunk counter.</summary>
    public const int NonceSize = 12;

    /// <summary>Size of the derived AES key in bytes (AES-256).</summary>
    public const int KeySize = 32;

    /// <summary>Size of the GCM authentication tag in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>Plaintext bytes per chunk (1 MiB). Every chunk but the last is exactly this long.</summary>
    public const int ChunkSize = 1 << 20;

    /// <summary>Total header size in bytes.</summary>
    public const int HeaderSize = 4 + 1 + 4 + SaltSize + NoncePrefixSize;

    /// <summary>Default PBKDF2-HMAC-SHA256 iteration count (the OWASP recommendation for SHA-256).</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>
    /// Largest iteration count accepted, as a parameter or in a header. Sixteen times the default
    /// is ample headroom, while keeping the work a hostile file can demand before it is rejected
    /// to a few seconds.
    /// </summary>
    public const int MaxIterations = 10_000_000;

    /// <summary>Maximum number of chunks in one file (the NIST SP 800-38D invocation limit per key).</summary>
    public const ulong MaxChunks = 1UL << 32;

    internal const int IterationsOffset = 5;
    internal const int SaltOffset = 9;
    internal const int NoncePrefixOffset = 25;
    internal const int AadSize = HeaderSize + 8 + 1;
    internal const int SealedChunkSize = ChunkSize + TagSize;

    /// <summary>The four magic bytes every AESX file starts with ("AESX").</summary>
    public static ReadOnlySpan<byte> Magic => "AESX"u8;

    /// <summary>Whether this platform provides AES-GCM (true on Windows, macOS and Linux with OpenSSL).</summary>
    public static bool IsSupported => AesGcm.IsSupported;

    /// <summary>Encrypts <paramref name="input"/> to <paramref name="output"/> with a fresh random salt and nonce prefix.</summary>
    /// <param name="input">Plaintext source; read until it ends.</param>
    /// <param name="output">Destination for the AESX file.</param>
    /// <param name="password">Password bytes exactly as the application defines them (the CLI uses UTF-8). Must not be empty.</param>
    /// <param name="iterations">PBKDF2 iteration count, 1..<see cref="MaxIterations"/>.</param>
    /// <param name="cancellationToken">Checked before the header and before every chunk; cancelling stops the work at a chunk boundary.</param>
    /// <exception cref="ArgumentException">The password is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The iteration count is out of range.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static void Encrypt(
        Stream input,
        Stream output,
        ReadOnlySpan<byte> password,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        Span<byte> salt = stackalloc byte[SaltSize];
        Span<byte> noncePrefix = stackalloc byte[NoncePrefixSize];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(noncePrefix);
        Encrypt(input, output, password, iterations, salt, noncePrefix, cancellationToken);
    }

    /// <summary>
    /// Encrypts with a caller-supplied salt and nonce prefix. This exists only so tests can
    /// produce deterministic vectors: reusing a salt and prefix with the same password reuses
    /// key/nonce pairs, which destroys GCM's security. Production code uses the public overload.
    /// </summary>
    internal static void Encrypt(
        Stream input,
        Stream output,
        ReadOnlySpan<byte> password,
        int iterations,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> noncePrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        Span<byte> header = stackalloc byte[HeaderSize];
        BuildHeader(header, password, iterations, salt, noncePrefix);
        cancellationToken.ThrowIfCancellationRequested();

        using var cipher = new ChunkCipher(password, header);
        byte[] bufA = new byte[ChunkSize];
        byte[] bufB = new byte[ChunkSize];
        byte[] sealedChunk = new byte[SealedChunkSize];
        try
        {
            output.Write(header);

            // One chunk of look-ahead tells whether the chunk in hand is the final one.
            int lenA = ReadFull(input, bufA);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int lenB = ReadFull(input, bufB);
                bool last = lenB == 0;
                cipher.Seal(bufA.AsSpan(0, lenA), sealedChunk, last);
                output.Write(sealedChunk, 0, lenA + TagSize);
                if (last) break;
                (bufA, bufB) = (bufB, bufA);
                lenA = lenB;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bufA);
            CryptographicOperations.ZeroMemory(bufB);
        }
    }

    /// <summary>Decrypts an AESX file from <paramref name="input"/> to <paramref name="output"/>.</summary>
    /// <remarks>
    /// Plaintext is written chunk by chunk, each only after its tag has verified, so a wrong
    /// password produces no output at all. If a later chunk fails, the chunks already written
    /// are authentic but the file as a whole is not: callers that need all-or-nothing semantics
    /// write to a temporary destination and discard it on failure, as the CLI does.
    /// </remarks>
    /// <param name="input">The AESX file; read until it ends.</param>
    /// <param name="output">Destination for the plaintext.</param>
    /// <param name="password">Password bytes, encoded exactly as they were for encryption.</param>
    /// <param name="cancellationToken">Checked before key derivation and before every chunk.</param>
    /// <exception cref="InvalidDataException">The input is not a complete, well-formed AESX file.</exception>
    /// <exception cref="CryptographicException">The password is wrong or the data has been tampered with.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static void Decrypt(Stream input, Stream output, ReadOnlySpan<byte> password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var decryptor = new AesxDecryptStream(input, password, leaveOpen: true, cancellationToken);
        decryptor.CopyTo(output);
    }

    /// <summary>
    /// Reads and checks the 29-byte header at the current position of <paramref name="input"/>
    /// without a password. What it returns is what the file claims: the header is authenticated
    /// only together with the first chunk, when the file is decrypted.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream does not start with a well-formed AESX version 1 header.</exception>
    public static AesxHeader ReadHeader(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> header = stackalloc byte[HeaderSize];
        return ReadHeader(input, header);
    }

    /// <summary>
    /// Works out from the length of a whole AESX file how many chunks it holds and how long its
    /// plaintext is; the format has no padding, so the length alone decides both.
    /// </summary>
    /// <returns>False when no well-formed AESX file has that length (it is truncated, or not an AESX file).</returns>
    public static bool TryGetLayout(long fileLength, out long chunks, out long plaintextLength)
    {
        chunks = 0;
        plaintextLength = 0;
        long body = fileLength - HeaderSize;
        if (body < TagSize) return false;

        // Every chunk but the last is full; whatever is left over is the last one, and it must at
        // least hold its tag.
        long fullChunks = body / SealedChunkSize;
        long remainder = body % SealedChunkSize;
        if (remainder != 0 && remainder < TagSize) return false;

        chunks = fullChunks + (remainder == 0 ? 0 : 1);
        plaintextLength = body - chunks * TagSize;
        return true;
    }

    /// <summary>Reads the header into <paramref name="header"/> and applies step 1 of the decryption procedure.</summary>
    internal static AesxHeader ReadHeader(Stream input, Span<byte> header)
    {
        if (ReadFull(input, header) < HeaderSize || !header[..4].SequenceEqual(Magic))
            throw new InvalidDataException("not an AESX file");
        if (header[4] != Version)
            throw new InvalidDataException($"unsupported AESX format version {header[4]} (this build supports version {Version})");
        uint iterations = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(IterationsOffset, 4));
        if (iterations == 0 || iterations > MaxIterations)
            throw new InvalidDataException("invalid key-derivation parameters in header");
        return new AesxHeader(header[4], (int)iterations);
    }

    /// <summary>Validates the parameters of an encryption and lays out its header.</summary>
    internal static void BuildHeader(Span<byte> header, ReadOnlySpan<byte> password, int iterations, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> noncePrefix)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);
        if (password.IsEmpty)
            throw new ArgumentException("The password must not be empty.", nameof(password));
        if (salt.Length != SaltSize)
            throw new ArgumentException($"The salt must be {SaltSize} bytes.", nameof(salt));
        if (noncePrefix.Length != NoncePrefixSize)
            throw new ArgumentException($"The nonce prefix must be {NoncePrefixSize} bytes.", nameof(noncePrefix));

        Magic.CopyTo(header);
        header[4] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(IterationsOffset, 4), (uint)iterations);
        salt.CopyTo(header.Slice(SaltOffset, SaltSize));
        noncePrefix.CopyTo(header.Slice(NoncePrefixOffset, NoncePrefixSize));
    }

    internal static byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>Fills the buffer unless the stream ends first; returns the number of bytes read.</summary>
    internal static int ReadFull(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n <= 0) break;
            if (n > buffer.Length - total)
                throw new IOException("the input stream reported more bytes than were requested");
            total += n;
        }
        return total;
    }
}

/// <summary>
/// What the header of an AESX file says. None of it is secret, and none of it is proven until the
/// file has been decrypted: the header is authenticated together with every chunk.
/// </summary>
/// <param name="Version">The format version byte.</param>
/// <param name="Iterations">The PBKDF2-HMAC-SHA256 iteration count the key was derived with.</param>
public readonly record struct AesxHeader(byte Version, int Iterations);

/// <summary>
/// The chunk cryptography of one file, shared by both directions and by the stream classes: the
/// key, the nonce and associated data of the chunk that comes next, and the chunk counter.
/// </summary>
internal sealed class ChunkCipher : IDisposable
{
    private readonly AesGcm _aes;
    private readonly byte[] _nonce = new byte[AesxFormat.NonceSize];
    private readonly byte[] _aad = new byte[AesxFormat.AadSize];
    private ulong _index;

    /// <summary>Derives the key from the password and the (already validated) header.</summary>
    public ChunkCipher(ReadOnlySpan<byte> password, ReadOnlySpan<byte> header)
    {
        int iterations = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(AesxFormat.IterationsOffset, 4));
        byte[] key = AesxFormat.DeriveKey(password, header.Slice(AesxFormat.SaltOffset, AesxFormat.SaltSize), iterations);
        try
        {
            _aes = new AesGcm(key, AesxFormat.TagSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        header.Slice(AesxFormat.NoncePrefixOffset, AesxFormat.NoncePrefixSize).CopyTo(_nonce);
        header.CopyTo(_aad);
    }

    /// <summary>Encrypts the next chunk into <paramref name="sealedChunk"/> as ciphertext followed by its tag.</summary>
    public void Seal(ReadOnlySpan<byte> plaintext, Span<byte> sealedChunk, bool last)
    {
        Address(last);
        _aes.Encrypt(_nonce, plaintext, sealedChunk[..plaintext.Length], sealedChunk.Slice(plaintext.Length, AesxFormat.TagSize), _aad);
        Advance(last);
    }

    /// <summary>Verifies the next chunk (ciphertext followed by its tag) and only then releases its plaintext.</summary>
    public void Open(ReadOnlySpan<byte> sealedChunk, Span<byte> plaintext, bool last)
    {
        int length = sealedChunk.Length - AesxFormat.TagSize;
        Address(last);
        try
        {
            _aes.Decrypt(_nonce, sealedChunk[..length], sealedChunk[length..], plaintext[..length], _aad);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException(_index == 0
                ? "wrong password, or the file is corrupted"
                : $"authentication failed at chunk {_index}: the file is corrupted or has been tampered with");
        }
        Advance(last);
    }

    private void Address(bool last)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_nonce.AsSpan(AesxFormat.NoncePrefixSize, 8), _index);
        BinaryPrimitives.WriteUInt64BigEndian(_aad.AsSpan(AesxFormat.HeaderSize, 8), _index);
        _aad[AesxFormat.HeaderSize + 8] = last ? (byte)1 : (byte)0;
    }

    private void Advance(bool last)
    {
        if (last) return;
        _index++;
        if (_index >= AesxFormat.MaxChunks)
            throw new InvalidDataException("the data exceeds the AESX size limit of 2^32 chunks (4 PiB)");
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>
/// A write-only stream that turns what is written to it into an AESX file on another stream, for
/// data that is produced piece by piece (an archive writer, a serializer) rather than read from a
/// stream. It writes the same bytes as <see cref="AesxFormat.Encrypt(Stream, Stream, ReadOnlySpan{byte}, int, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// Call <see cref="Complete"/> after the last write: it marks the final chunk, without which the
/// file does not decrypt. Disposing the stream without completing it leaves an unfinished,
/// deliberately invalid file behind, which is the right outcome when the writer failed half way.
/// The class is not thread-safe.
/// </remarks>
public sealed class AesxEncryptStream : Stream
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly CancellationToken _cancellationToken;
    private readonly ChunkCipher _cipher;
    private readonly byte[] _header = new byte[AesxFormat.HeaderSize];
    private readonly byte[] _pending;
    private readonly byte[] _sealed;
    private int _count;
    private bool _headerWritten;
    private bool _completed;
    private bool _disposed;

    /// <summary>Starts an AESX file on <paramref name="output"/> with a fresh random salt and nonce prefix.</summary>
    /// <param name="output">Destination for the AESX file.</param>
    /// <param name="password">Password bytes exactly as the application defines them (the CLI uses UTF-8). Must not be empty.</param>
    /// <param name="iterations">PBKDF2 iteration count, 1..<see cref="AesxFormat.MaxIterations"/>.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves <paramref name="output"/> open.</param>
    /// <param name="cancellationToken">Checked before every chunk is encrypted; a cancelled token makes the write in progress throw.</param>
    /// <exception cref="ArgumentException">The password is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The iteration count is out of range.</exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.</exception>
    public AesxEncryptStream(
        Stream output,
        ReadOnlySpan<byte> password,
        int iterations = AesxFormat.DefaultIterations,
        bool leaveOpen = false,
        CancellationToken cancellationToken = default)
        : this(output, password, iterations,
            RandomNumberGenerator.GetBytes(AesxFormat.SaltSize), RandomNumberGenerator.GetBytes(AesxFormat.NoncePrefixSize),
            leaveOpen, cancellationToken)
    {
    }

    /// <summary>The deterministic form, for test vectors only; see the internal overload of <see cref="AesxFormat"/>'s Encrypt.</summary>
    internal AesxEncryptStream(
        Stream output,
        ReadOnlySpan<byte> password,
        int iterations,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> noncePrefix,
        bool leaveOpen,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        AesxFormat.BuildHeader(_header, password, iterations, salt, noncePrefix);
        cancellationToken.ThrowIfCancellationRequested();
        _output = output;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
        _cipher = new ChunkCipher(password, _header);
        _pending = new byte[AesxFormat.ChunkSize];
        _sealed = new byte[AesxFormat.SealedChunkSize];
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed && !_completed;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The AESX file has already been completed.");

        while (!buffer.IsEmpty)
        {
            // A full chunk is held back until more data arrives or Complete is called, because
            // only then is it known whether it is the final one.
            if (_count == AesxFormat.ChunkSize) Emit(last: false);
            int n = Math.Min(buffer.Length, AesxFormat.ChunkSize - _count);
            buffer[..n].CopyTo(_pending.AsSpan(_count));
            _count += n;
            buffer = buffer[n..];
        }
    }

    /// <summary>
    /// Writes the final chunk, which is what makes the file valid. Nothing can be written
    /// afterwards. The underlying stream is not flushed or closed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The AESX file has already been completed.");
        Emit(last: true);
        _completed = true;
    }

    private void Emit(bool last)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!_headerWritten)
        {
            _output.Write(_header);
            _headerWritten = true;
        }
        _cipher.Seal(_pending.AsSpan(0, _count), _sealed, last);
        _output.Write(_sealed, 0, _count + AesxFormat.TagSize);
        _count = 0;
    }

    /// <summary>Flushes the underlying stream. Data still waiting for its chunk to fill up stays where it is.</summary>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _output.Flush();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            CryptographicOperations.ZeroMemory(_pending);
            _cipher.Dispose();
            if (!_leaveOpen) _output.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A read-only stream over the plaintext of an AESX file, for consumers that pull data (an
/// archive reader, a parser). Every byte it returns comes from a chunk whose tag has verified.
/// </summary>
/// <remarks>
/// <para>
/// The constructor reads the header and derives the key, so it fails at once on anything that is
/// not an AESX file; a wrong password shows on the first read.
/// </para>
/// <para>
/// A read that returns 0 is the only proof that the file is complete: it is reached only after
/// the chunk marked as final has verified. A consumer that stops earlier has read authentic data
/// from a file that may still be truncated or damaged further on, so read to the end before
/// trusting what was read as a whole. The class is not thread-safe.
/// </para>
/// </remarks>
public sealed class AesxDecryptStream : Stream
{
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly CancellationToken _cancellationToken;
    private readonly ChunkCipher _cipher;
    private readonly byte[] _plain;
    private byte[] _current;
    private byte[] _next;
    private int _currentLength;
    private int _offset;
    private int _available;
    private bool _primed;
    private bool _finished;
    private bool _disposed;

    /// <summary>Opens the AESX file that starts at the current position of <paramref name="input"/>.</summary>
    /// <param name="input">The AESX file; read until it ends.</param>
    /// <param name="password">Password bytes, encoded exactly as they were for encryption.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves <paramref name="input"/> open.</param>
    /// <param name="cancellationToken">Checked before key derivation and before every chunk; a cancelled token makes the read in progress throw.</param>
    /// <exception cref="ArgumentException">The password is empty.</exception>
    /// <exception cref="InvalidDataException">The input does not start with a well-formed AESX header.</exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.</exception>
    public AesxDecryptStream(Stream input, ReadOnlySpan<byte> password, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (password.IsEmpty)
            throw new ArgumentException("The password must not be empty.", nameof(password));
        cancellationToken.ThrowIfCancellationRequested();

        Span<byte> header = stackalloc byte[AesxFormat.HeaderSize];
        Header = AesxFormat.ReadHeader(input, header);
        _input = input;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
        _cipher = new ChunkCipher(password, header);
        _plain = new byte[AesxFormat.ChunkSize];
        _current = new byte[AesxFormat.SealedChunkSize];
        _next = new byte[AesxFormat.SealedChunkSize];
    }

    /// <summary>What the file's header says; proven once the first read has succeeded.</summary>
    public AesxHeader Header { get; }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The file is truncated or structurally damaged.</exception>
    /// <exception cref="CryptographicException">The password is wrong or the data has been tampered with.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc cref="Read(byte[], int, int)"/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return 0;

        // Only the single chunk of an empty file is empty, but looping costs nothing.
        while (_available == 0)
        {
            if (!OpenNextChunk()) return 0;
        }
        int n = Math.Min(buffer.Length, _available);
        _plain.AsSpan(_offset, n).CopyTo(buffer);
        _offset += n;
        _available -= n;
        return n;
    }

    /// <summary>
    /// Writes the rest of the plaintext to <paramref name="destination"/>, one verified chunk at
    /// a time and without an intermediate copy.
    /// </summary>
    /// <inheritdoc cref="Read(byte[], int, int)" path="/exception"/>
    public override void CopyTo(Stream destination, int bufferSize)
    {
        ValidateCopyToArguments(destination, bufferSize);
        ObjectDisposedException.ThrowIf(_disposed, this);
        do
        {
            if (_available > 0)
            {
                destination.Write(_plain, _offset, _available);
                _available = 0;
            }
        }
        while (OpenNextChunk());
    }

    /// <summary>Verifies and decrypts the next chunk into the plaintext buffer; false once the final chunk has been consumed.</summary>
    private bool OpenNextChunk()
    {
        if (_finished) return false;
        _cancellationToken.ThrowIfCancellationRequested();
        if (!_primed)
        {
            _currentLength = AesxFormat.ReadFull(_input, _current);
            _primed = true;
        }

        // One chunk of look-ahead: if nothing follows, the chunk in hand must be the final one.
        int nextLength = AesxFormat.ReadFull(_input, _next);
        bool last = nextLength == 0;
        if (_currentLength < AesxFormat.TagSize || (!last && _currentLength != AesxFormat.SealedChunkSize))
            throw new InvalidDataException("file is truncated or corrupted");

        _cipher.Open(_current.AsSpan(0, _currentLength), _plain, last);
        _offset = 0;
        _available = _currentLength - AesxFormat.TagSize;
        (_current, _next) = (_next, _current);
        _currentLength = nextLength;
        _finished = last;
        return true;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            CryptographicOperations.ZeroMemory(_plain);
            _cipher.Dispose();
            if (!_leaveOpen) _input.Dispose();
        }
        base.Dispose(disposing);
    }
}
