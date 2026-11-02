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

    private const int IterationsOffset = 5;
    private const int SaltOffset = 9;
    private const int NoncePrefixOffset = 25;
    private const int AadSize = HeaderSize + 8 + 1;

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
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);
        if (password.IsEmpty)
            throw new ArgumentException("The password must not be empty.", nameof(password));
        if (salt.Length != SaltSize)
            throw new ArgumentException($"The salt must be {SaltSize} bytes.", nameof(salt));
        if (noncePrefix.Length != NoncePrefixSize)
            throw new ArgumentException($"The nonce prefix must be {NoncePrefixSize} bytes.", nameof(noncePrefix));
        cancellationToken.ThrowIfCancellationRequested();

        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(IterationsOffset, 4), (uint)iterations);
        salt.CopyTo(header.Slice(SaltOffset, SaltSize));
        noncePrefix.CopyTo(header.Slice(NoncePrefixOffset, NoncePrefixSize));

        byte[] key = DeriveKey(password, salt, iterations);
        byte[] bufA = new byte[ChunkSize];
        byte[] bufB = new byte[ChunkSize];
        byte[] cipher = new byte[ChunkSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            output.Write(header);

            Span<byte> nonce = stackalloc byte[NonceSize];
            Span<byte> aad = stackalloc byte[AadSize];
            Span<byte> tag = stackalloc byte[TagSize];
            noncePrefix.CopyTo(nonce);
            header.CopyTo(aad);

            // One chunk of look-ahead tells whether the chunk in hand is the final one.
            int lenA = ReadFull(input, bufA);
            ulong index = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int lenB = ReadFull(input, bufB);
                bool last = lenB == 0;
                SetChunk(nonce, aad, index, last);
                aes.Encrypt(nonce, bufA.AsSpan(0, lenA), cipher.AsSpan(0, lenA), tag, aad);
                output.Write(cipher, 0, lenA);
                output.Write(tag);
                if (last) break;
                (bufA, bufB) = (bufB, bufA);
                lenA = lenB;
                index = NextIndex(index);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
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
        if (password.IsEmpty)
            throw new ArgumentException("The password must not be empty.", nameof(password));
        cancellationToken.ThrowIfCancellationRequested();

        Span<byte> header = stackalloc byte[HeaderSize];
        if (ReadFull(input, header) < HeaderSize || !header[..4].SequenceEqual(Magic))
            throw new InvalidDataException("not an AESX file");
        if (header[4] != Version)
            throw new InvalidDataException($"unsupported AESX format version {header[4]} (this build supports version {Version})");
        uint iterations = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(IterationsOffset, 4));
        if (iterations == 0 || iterations > MaxIterations)
            throw new InvalidDataException("invalid key-derivation parameters in header");

        byte[] key = DeriveKey(password, header.Slice(SaltOffset, SaltSize), (int)iterations);
        byte[] bufA = new byte[ChunkSize + TagSize];
        byte[] bufB = new byte[ChunkSize + TagSize];
        byte[] plain = new byte[ChunkSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);

            Span<byte> nonce = stackalloc byte[NonceSize];
            Span<byte> aad = stackalloc byte[AadSize];
            header.Slice(NoncePrefixOffset, NoncePrefixSize).CopyTo(nonce);
            header.CopyTo(aad);

            int lenA = ReadFull(input, bufA);
            ulong index = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int lenB = ReadFull(input, bufB);
                bool last = lenB == 0;
                if (lenA < TagSize || (!last && lenA != ChunkSize + TagSize))
                    throw new InvalidDataException("file is truncated or corrupted");

                int ct = lenA - TagSize;
                SetChunk(nonce, aad, index, last);
                try
                {
                    aes.Decrypt(nonce, bufA.AsSpan(0, ct), bufA.AsSpan(ct, TagSize), plain.AsSpan(0, ct), aad);
                }
                catch (CryptographicException)
                {
                    throw new CryptographicException(index == 0
                        ? "wrong password, or the file is corrupted"
                        : $"authentication failed at chunk {index}: the file is corrupted or has been tampered with");
                }
                output.Write(plain, 0, ct);
                if (last) break;
                (bufA, bufB) = (bufB, bufA);
                lenA = lenB;
                index = NextIndex(index);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private static void SetChunk(Span<byte> nonce, Span<byte> aad, ulong index, bool last)
    {
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(NoncePrefixSize, 8), index);
        BinaryPrimitives.WriteUInt64BigEndian(aad.Slice(HeaderSize, 8), index);
        aad[HeaderSize + 8] = last ? (byte)1 : (byte)0;
    }

    private static ulong NextIndex(ulong index)
    {
        index++;
        if (index >= MaxChunks)
            throw new InvalidDataException("the data exceeds the AESX size limit of 2^32 chunks (4 PiB)");
        return index;
    }

    /// <summary>Fills the buffer unless the stream ends first; returns the number of bytes read.</summary>
    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
