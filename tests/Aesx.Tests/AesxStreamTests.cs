// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Aesx.Tests;

/// <summary>
/// The push and pull stream classes must be indistinguishable from <see cref="AesxFormat.Encrypt(Stream, Stream, ReadOnlySpan{byte}, int, CancellationToken)"/>
/// and <see cref="AesxFormat.Decrypt"/>: same bytes on disk, same rejections, however the data
/// is cut into writes and reads.
/// </summary>
public class AesxStreamTests
{
    private const int C = AesxFormat.ChunkSize;
    private const int Header = AesxFormat.HeaderSize;
    private const int Tag = AesxFormat.TagSize;
    private const int ChunkLen = C + Tag;
    private const int Iterations = 1_000;
    private static readonly byte[] Password = "correct horse battery staple"u8.ToArray();
    private static readonly byte[] OtherPassword = "correct horse battery stapl3"u8.ToArray();
    private static readonly byte[] Salt = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] NoncePrefix = Convert.FromHexString("01020304");

    public static TheoryData<int, int> SizesAndPieces
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (int size in new[] { 0, 1, 15, 16, 17, 1000, C - 1, C, C + 1, 2 * C, 3 * C + 12345 })
            {
                foreach (int piece in new[] { 1021, 65_536, C, C + 7, 4 * C })
                {
                    data.Add(size, piece);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SizesAndPieces))]
    public void WritingInPiecesProducesTheSameFileAsEncrypt(int size, int piece)
    {
        byte[] plain = RandomNumberGenerator.GetBytes(size);
        using var expected = new MemoryStream();
        AesxFormat.Encrypt(new MemoryStream(plain), expected, Password, Iterations, Salt, NoncePrefix);

        using var actual = new MemoryStream();
        using (var encryptor = new AesxEncryptStream(actual, Password, Iterations, Salt, NoncePrefix, leaveOpen: true, CancellationToken.None))
        {
            for (int offset = 0; offset < plain.Length; offset += piece)
            {
                encryptor.Write(plain, offset, Math.Min(piece, plain.Length - offset));
            }
            encryptor.Complete();
        }

        Assert.True(expected.ToArray().AsSpan().SequenceEqual(actual.ToArray()));
    }

    [Theory]
    [MemberData(nameof(SizesAndPieces))]
    public void ReadingInPiecesReturnsThePlaintext(int size, int piece)
    {
        byte[] plain = RandomNumberGenerator.GetBytes(size);
        byte[] ct = Encrypt(plain);

        using var decryptor = new AesxDecryptStream(new TrickleStream(new MemoryStream(ct), maxRead: 70_001), Password);
        using var restored = new MemoryStream();
        byte[] buffer = new byte[piece];
        int n;
        while ((n = decryptor.Read(buffer, 0, buffer.Length)) > 0)
        {
            restored.Write(buffer, 0, n);
        }

        Assert.True(plain.AsSpan().SequenceEqual(restored.ToArray()));
        Assert.Equal(0, decryptor.Read(buffer, 0, buffer.Length)); // and the end stays the end
    }

    [Fact]
    public void TheEncryptStreamReproducesTheTestVector()
    {
        // The same vector as GoldenVectorTests and docs/FORMAT.md, written a byte at a time.
        byte[] plain = "The quick brown fox jumps over the lazy dog"u8.ToArray();
        using var output = new MemoryStream();
        using (var encryptor = new AesxEncryptStream(output, "aesx test vector"u8, 1000, Salt, NoncePrefix, leaveOpen: true, CancellationToken.None))
        {
            foreach (byte b in plain) encryptor.WriteByte(b);
            encryptor.Complete();
        }

        Assert.Equal(
            "4145535801e8030000000102030405060708090a0b0c0d0e0f01020304" +
            "4a1c50e26e9863c67f7204ef37d91203f95df97cef603b50f223a7388303829043bfe01e82564366ca1618" +
            "11fc7e244395ade905caacee554d63fa",
            Convert.ToHexStringLower(output.ToArray()));
    }

    [Fact]
    public void AFileThatWasNeverCompletedDoesNotDecrypt()
    {
        using var output = new MemoryStream();
        using (var encryptor = new AesxEncryptStream(output, Password, Iterations, leaveOpen: true))
        {
            encryptor.Write(RandomNumberGenerator.GetBytes(2 * C + 5)); // two chunks go out, the third waits
        }

        Assert.Equal(Header + 2 * ChunkLen, output.Length);
        Exception? ex = Record.Exception(() => Decrypt(output.ToArray()));
        Assert.True(ex is CryptographicException, $"unexpected {ex?.GetType().Name}: {ex?.Message}");
    }

    [Fact]
    public void NothingIsWrittenUntilThereIsAChunkToWrite()
    {
        using var output = new MemoryStream();
        using (var encryptor = new AesxEncryptStream(output, Password, Iterations, leaveOpen: true))
        {
            encryptor.Write(new byte[C]); // exactly one chunk: it may still turn out to be the last
            Assert.Equal(0, output.Length);
        }
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void ACompletedStreamRefusesMoreData()
    {
        using var encryptor = new AesxEncryptStream(new MemoryStream(), Password, Iterations);
        encryptor.Write([1, 2, 3]);
        encryptor.Complete();

        Assert.False(encryptor.CanWrite);
        Assert.Throws<InvalidOperationException>(() => encryptor.Write([4]));
        Assert.Throws<InvalidOperationException>(encryptor.Complete);
    }

    [Fact]
    public void DisposingClosesTheUnderlyingStreamUnlessAskedNotTo()
    {
        var closed = new MemoryStream();
        new AesxEncryptStream(closed, Password, Iterations).Dispose();
        Assert.False(closed.CanWrite);

        var open = new MemoryStream();
        new AesxEncryptStream(open, Password, Iterations, leaveOpen: true).Dispose();
        Assert.True(open.CanWrite);

        var source = new MemoryStream(Encrypt([1, 2, 3]));
        new AesxDecryptStream(source, Password).Dispose();
        Assert.False(source.CanRead);

        source = new MemoryStream(Encrypt([1, 2, 3]));
        new AesxDecryptStream(source, Password, leaveOpen: true).Dispose();
        Assert.True(source.CanRead);
    }

    [Fact]
    public void TheStreamsRejectTheSameParametersAsTheFunctions()
    {
        Assert.Throws<ArgumentException>(() => new AesxEncryptStream(new MemoryStream(), [], Iterations));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AesxEncryptStream(new MemoryStream(), Password, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AesxEncryptStream(new MemoryStream(), Password, AesxFormat.MaxIterations + 1));
        Assert.Throws<ArgumentException>(() => new AesxDecryptStream(new MemoryStream(Encrypt([1])), []));
        Assert.Throws<ArgumentNullException>(() => new AesxEncryptStream(null!, Password, Iterations));
        Assert.Throws<ArgumentNullException>(() => new AesxDecryptStream(null!, Password));
    }

    [Fact]
    public void SomethingThatIsNotAnAesxFileIsRejectedWhenTheStreamIsOpened()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new AesxDecryptStream(new MemoryStream(new byte[100]), Password));
        Assert.Contains("not an AESX file", ex.Message, StringComparison.Ordinal);

        byte[] newer = Encrypt([1, 2, 3]);
        newer[4] = 2;
        ex = Assert.Throws<InvalidDataException>(() => new AesxDecryptStream(new MemoryStream(newer), Password));
        Assert.Contains("version 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongPasswordShowsOnTheFirstReadAndReleasesNothing()
    {
        using var decryptor = new AesxDecryptStream(new MemoryStream(Encrypt(RandomNumberGenerator.GetBytes(2 * C + 5))), OtherPassword);
        byte[] buffer = new byte[100];
        Array.Fill(buffer, (byte)0xEE);

        var ex = Assert.Throws<CryptographicException>(() => decryptor.Read(buffer, 0, buffer.Length));

        Assert.Contains("wrong password", ex.Message, StringComparison.Ordinal);
        Assert.All(buffer, b => Assert.Equal(0xEE, b));
    }

    [Fact]
    public void DamageInALaterChunkIsReportedWhenThatChunkIsReached()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(2 * C + 5);
        byte[] ct = Encrypt(plain);
        ct[Header + 2 * ChunkLen + 1] ^= 0x01; // inside chunk 2
        using var decryptor = new AesxDecryptStream(new MemoryStream(ct), Password);
        using var released = new MemoryStream();
        byte[] buffer = new byte[300_000];

        var ex = Assert.Throws<CryptographicException>(() =>
        {
            int n;
            while ((n = decryptor.Read(buffer, 0, buffer.Length)) > 0) released.Write(buffer, 0, n);
        });

        Assert.Contains("chunk 2", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2 * C, released.Length); // chunks 0 and 1 were authentic; nothing of chunk 2 got out
        Assert.True(plain.AsSpan(0, 2 * C).SequenceEqual(released.ToArray()));
    }

    [Fact]
    public void ATruncatedFileIsOnlyProvenCompleteByReadingToTheEnd()
    {
        byte[] ct = Encrypt(RandomNumberGenerator.GetBytes(2 * C + 5));
        byte[] truncated = ct[..(Header + 2 * ChunkLen)]; // the final chunk is gone
        using var decryptor = new AesxDecryptStream(new MemoryStream(truncated), Password);
        byte[] buffer = new byte[C];

        Assert.Equal(C, decryptor.Read(buffer, 0, C)); // chunk 0 is genuine, and says nothing about the rest
        Exception? ex = Record.Exception(() =>
        {
            while (decryptor.Read(buffer, 0, C) > 0)
            {
            }
        });

        Assert.True(ex is CryptographicException or InvalidDataException, $"unexpected {ex?.GetType().Name}: {ex?.Message}");
    }

    [Fact]
    public void CancellationStopsBothStreamsAtAChunkBoundary()
    {
        using var cts = new CancellationTokenSource();
        using var output = new MemoryStream();
        using var encryptor = new AesxEncryptStream(output, Password, Iterations, leaveOpen: true, cts.Token);
        encryptor.Write(new byte[C + 1]); // chunk 0 goes out
        long written = output.Length;
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => encryptor.Write(new byte[C]));
        Assert.Throws<OperationCanceledException>(encryptor.Complete);
        Assert.Equal(written, output.Length);

        using var cts2 = new CancellationTokenSource();
        using var decryptor = new AesxDecryptStream(new MemoryStream(Encrypt(new byte[3 * C])), Password, leaveOpen: false, cts2.Token);
        byte[] buffer = new byte[C];
        Assert.Equal(C, decryptor.Read(buffer, 0, C));
        cts2.Cancel();
        Assert.Throws<OperationCanceledException>(() => decryptor.Read(buffer, 0, C));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new AesxEncryptStream(new MemoryStream(), Password, Iterations, leaveOpen: false, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => new AesxDecryptStream(new MemoryStream(Encrypt([1])), Password, leaveOpen: false, cancelled.Token));
    }

    [Fact]
    public void CopyToStartsWhereReadingStopped()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(2 * C + 99);
        using var decryptor = new AesxDecryptStream(new MemoryStream(Encrypt(plain)), Password);
        byte[] head = new byte[1000];
        Assert.Equal(1000, decryptor.Read(head, 0, 1000));
        using var rest = new MemoryStream();

        decryptor.CopyTo(rest);

        Assert.True(plain.AsSpan(0, 1000).SequenceEqual(head));
        Assert.True(plain.AsSpan(1000).SequenceEqual(rest.ToArray()));
    }

    [Theory]
    [InlineData(600_000)]
    [InlineData(1)]
    [InlineData(AesxFormat.MaxIterations)]
    public void TheHeaderCanBeReadWithoutAPassword(int iterations)
    {
        // Only the header is built here, so even the largest count costs nothing.
        byte[] header = [.. "AESX"u8, 1, 0, 0, 0, 0, .. Salt, .. NoncePrefix];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5, 4), (uint)iterations);

        AesxHeader read = AesxFormat.ReadHeader(new MemoryStream(header));

        Assert.Equal(1, read.Version);
        Assert.Equal(iterations, read.Iterations);
    }

    [Fact]
    public void ReadingTheHeaderAppliesTheSameChecksAsDecryption()
    {
        byte[] good = Encrypt([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => AesxFormat.ReadHeader(new MemoryStream(good[..28])));
        Assert.Throws<InvalidDataException>(() => AesxFormat.ReadHeader(new MemoryStream([.. "AESY"u8, .. good[4..]])));
        Assert.Throws<InvalidDataException>(() => AesxFormat.ReadHeader(new MemoryStream([.. good[..4], 9, .. good[5..]])));
        Assert.Throws<InvalidDataException>(() => AesxFormat.ReadHeader(new MemoryStream([.. good[..5], 0, 0, 0, 0, .. good[9..]])));
        Assert.Throws<InvalidDataException>(() => AesxFormat.ReadHeader(new MemoryStream([.. good[..5], 0xFF, 0xFF, 0xFF, 0xFF, .. good[9..]])));
        Assert.Throws<ArgumentNullException>(() => AesxFormat.ReadHeader(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(C - 1)]
    [InlineData(C)]
    [InlineData(C + 1)]
    [InlineData(2 * C)]
    [InlineData(3 * C + 12345)]
    public void TheLayoutFollowsFromTheFileLengthAlone(int size)
    {
        long fileLength = Encrypt(new byte[size]).Length;

        Assert.True(AesxFormat.TryGetLayout(fileLength, out long chunks, out long plaintextLength));

        Assert.Equal(size == 0 ? 1 : (size + C - 1) / C, chunks);
        Assert.Equal(size, plaintextLength);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(Header)]                    // nothing after the header
    [InlineData(Header + Tag - 1)]          // not even a tag
    [InlineData(Header + ChunkLen + 1)]     // a second chunk too short to hold its tag
    [InlineData(Header + 2 * ChunkLen + 15)]
    public void LengthsNoAesxFileCanHaveAreRecognised(long fileLength)
    {
        Assert.False(AesxFormat.TryGetLayout(fileLength, out long chunks, out long plaintextLength));
        Assert.Equal(0, chunks);
        Assert.Equal(0, plaintextLength);
    }

    [Fact]
    public void HugeLengthsDoNotOverflow()
    {
        const long fourBillionChunks = 4_000_000_000L;
        long fileLength = Header + fourBillionChunks * ChunkLen + 5 + Tag; // far beyond what the format allows, but arithmetic must hold

        Assert.True(AesxFormat.TryGetLayout(fileLength, out long chunks, out long plaintextLength));
        Assert.Equal(fourBillionChunks + 1, chunks);
        Assert.Equal(fourBillionChunks * C + 5, plaintextLength);

        if (AesxFormat.TryGetLayout(long.MaxValue, out chunks, out plaintextLength))
        {
            Assert.True(chunks > 0 && plaintextLength > 0 && plaintextLength < long.MaxValue);
        }
    }

    private static byte[] Encrypt(byte[] plain)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        AesxFormat.Encrypt(input, output, Password, Iterations);
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] ct)
    {
        using var input = new MemoryStream(ct);
        using var output = new MemoryStream();
        AesxFormat.Decrypt(input, output, Password);
        return output.ToArray();
    }
}
