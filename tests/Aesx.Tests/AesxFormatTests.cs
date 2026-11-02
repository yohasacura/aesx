// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Aesx.Tests;

public class AesxFormatTests
{
    private const int C = AesxFormat.ChunkSize;
    private const int Header = AesxFormat.HeaderSize;
    private const int Tag = AesxFormat.TagSize;
    private const int ChunkLen = C + Tag;
    private const int Iterations = 1_000;
    private static readonly byte[] Password = "correct horse battery staple"u8.ToArray();
    private static readonly byte[] OtherPassword = "correct horse battery stapl3"u8.ToArray();

    public static TheoryData<int> Sizes => [0, 1, 15, 16, 17, 1000, C - 1, C, C + 1, 2 * C, 3 * C + 12345];

    [Fact]
    public void AesGcmIsAvailableOnThisPlatform() => Assert.True(AesxFormat.IsSupported);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RoundTripsAtEveryChunkBoundary(int size)
    {
        byte[] plain = RandomNumberGenerator.GetBytes(size);
        byte[] ct = Encrypt(plain);
        int chunks = size == 0 ? 1 : (size + C - 1) / C;
        Assert.Equal(Header + size + chunks * Tag, ct.Length);
        Assert.True(plain.AsSpan().SequenceEqual(Decrypt(ct)));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RoundTripsThroughNonSeekableStreamsWithShortReads(int size)
    {
        byte[] plain = RandomNumberGenerator.GetBytes(size);

        using var ctStream = new MemoryStream();
        using (var input = new TrickleStream(new MemoryStream(plain), maxRead: 1021))
        {
            AesxFormat.Encrypt(input, ctStream, Password, Iterations);
        }

        using var ptStream = new MemoryStream();
        using (var input = new TrickleStream(new MemoryStream(ctStream.ToArray()), maxRead: 1021))
        {
            AesxFormat.Decrypt(input, ptStream, Password);
        }

        Assert.True(plain.AsSpan().SequenceEqual(ptStream.ToArray()));
    }

    [Fact]
    public void SameInputEncryptsDifferentlyEveryTime()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(100);
        Assert.False(Encrypt(plain).AsSpan().SequenceEqual(Encrypt(plain)));
    }

    [Fact]
    public void WrongPasswordIsRejectedBeforeAnyOutput()
    {
        byte[] ct = Encrypt(RandomNumberGenerator.GetBytes(2 * C + 5));
        using var input = new MemoryStream(ct);
        using var output = new MemoryStream();

        var ex = Assert.Throws<CryptographicException>(() => AesxFormat.Decrypt(input, output, OtherPassword));

        Assert.Contains("wrong password", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void DamageInALaterChunkStopsOutputAtTheLastGoodChunk()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(2 * C + 5);
        byte[] ct = Encrypt(plain);
        ct[Header + 2 * ChunkLen + 1] ^= 0x01; // inside chunk 2
        using var input = new MemoryStream(ct);
        using var output = new MemoryStream();

        var ex = Assert.Throws<CryptographicException>(() => AesxFormat.Decrypt(input, output, Password));

        Assert.Contains("chunk 2", ex.Message, StringComparison.Ordinal);
        // Chunks 0 and 1 were authentic and were streamed out; the CLI discards such partial results.
        Assert.Equal(2 * C, output.Length);
        Assert.True(plain.AsSpan(0, 2 * C).SequenceEqual(output.ToArray()));
    }

    [Theory]
    [InlineData("flip magic")]
    [InlineData("flip version")]
    [InlineData("flip iterations")]
    [InlineData("flip salt")]
    [InlineData("flip nonce prefix")]
    [InlineData("flip chunk 0 body")]
    [InlineData("flip chunk 0 tag")]
    [InlineData("flip chunk 1 body")]
    [InlineData("flip last byte")]
    [InlineData("truncate 1 byte")]
    [InlineData("drop last chunk")]
    [InlineData("keep first chunk only")]
    [InlineData("header only")]
    [InlineData("empty")]
    [InlineData("append 1 byte")]
    [InlineData("swap chunks 0 and 1")]
    [InlineData("duplicate chunk 0")]
    [InlineData("splice last chunk from another file")]
    [InlineData("splice header from another file")]
    public void TamperingIsDetected(string tampering)
    {
        byte[] plain = RandomNumberGenerator.GetBytes(2 * C + 100); // three chunks
        byte[] good = Encrypt(plain);
        byte[] other = Encrypt(plain);
        byte[] bad = tampering switch
        {
            "flip magic" => Flip(good, 0),
            "flip version" => Flip(good, 4),
            "flip iterations" => Flip(good, 5),
            "flip salt" => Flip(good, 9),
            "flip nonce prefix" => Flip(good, 25),
            "flip chunk 0 body" => Flip(good, Header),
            "flip chunk 0 tag" => Flip(good, Header + C),
            "flip chunk 1 body" => Flip(good, Header + ChunkLen + 7),
            "flip last byte" => Flip(good, good.Length - 1),
            "truncate 1 byte" => good[..^1],
            "drop last chunk" => good[..(Header + 2 * ChunkLen)],
            "keep first chunk only" => good[..(Header + ChunkLen)],
            "header only" => good[..Header],
            "empty" => [],
            "append 1 byte" => [.. good, 0],
            "swap chunks 0 and 1" => SwapChunks(good),
            "duplicate chunk 0" => [.. good[..(Header + ChunkLen)], .. good[Header..]],
            "splice last chunk from another file" => [.. good[..(Header + 2 * ChunkLen)], .. other[(Header + 2 * ChunkLen)..]],
            "splice header from another file" => [.. other[..Header], .. good[Header..]],
            _ => throw new ArgumentOutOfRangeException(nameof(tampering)),
        };

        Exception? ex = Record.Exception(() => Decrypt(bad));

        Assert.NotNull(ex);
        Assert.True(ex is CryptographicException or InvalidDataException, $"unexpected {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void HeaderLayoutMatchesTheSpecification()
    {
        byte[] salt = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        byte[] prefix = [0xA1, 0xB2, 0xC3, 0xD4];

        byte[] ct = EncryptDeterministic([1, 2, 3], 12345, salt, prefix);

        Assert.True(ct.AsSpan(0, 4).SequenceEqual("AESX"u8));
        Assert.Equal(1, ct[4]);
        Assert.Equal(12345u, BinaryPrimitives.ReadUInt32LittleEndian(ct.AsSpan(5, 4)));
        Assert.True(ct.AsSpan(9, 16).SequenceEqual(salt));
        Assert.True(ct.AsSpan(25, 4).SequenceEqual(prefix));
        Assert.Equal(Header + 3 + Tag, ct.Length);
    }

    [Fact]
    public void MatchesAnIndependentImplementationOfTheSpecification()
    {
        // docs/FORMAT.md transcribed from scratch with nothing but the BCL primitives.
        byte[] plain = RandomNumberGenerator.GetBytes(2 * C + 777);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] prefix = RandomNumberGenerator.GetBytes(4);
        const int iterations = 777;

        byte[] iterationBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(iterationBytes, iterations);
        byte[] header = [.. "AESX"u8, 1, .. iterationBytes, .. salt, .. prefix];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Password, salt, iterations, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, 16);
        using var expected = new MemoryStream();
        expected.Write(header);
        int chunks = (plain.Length + C - 1) / C;
        for (int i = 0; i < chunks; i++)
        {
            int offset = i * C;
            int length = Math.Min(C, plain.Length - offset);
            byte[] nonce = new byte[12];
            prefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)i);
            byte[] aad = new byte[29 + 8 + 1];
            header.CopyTo(aad, 0);
            BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(29), (ulong)i);
            aad[37] = (byte)(i == chunks - 1 ? 1 : 0);
            byte[] body = new byte[length];
            byte[] tag = new byte[16];
            aes.Encrypt(nonce, plain.AsSpan(offset, length), body, tag, aad);
            expected.Write(body);
            expected.Write(tag);
        }

        byte[] actual = EncryptDeterministic(plain, iterations, salt, prefix);

        Assert.True(expected.ToArray().AsSpan().SequenceEqual(actual));
    }

    [Fact]
    public void EmptyPasswordIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AesxFormat.Encrypt(Stream.Null, Stream.Null, [], Iterations));
        Assert.Throws<ArgumentException>(() => AesxFormat.Decrypt(Stream.Null, Stream.Null, []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(AesxFormat.MaxIterations + 1)]
    public void IterationCountOutOfRangeIsRejected(int iterations)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AesxFormat.Encrypt(Stream.Null, Stream.Null, Password, iterations));

    [Fact]
    public void UnsupportedVersionIsRejected()
    {
        byte[] ct = Encrypt([1, 2, 3]);
        ct[4] = 2;

        var ex = Assert.Throws<InvalidDataException>(() => Decrypt(ct));

        Assert.Contains("version 2", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(AesxFormat.MaxIterations + 1u)]
    [InlineData(uint.MaxValue)]
    public void HostileIterationCountInHeaderIsRejectedWithoutDerivingAKey(uint iterations)
    {
        byte[] ct = Encrypt([1, 2, 3]);
        BinaryPrimitives.WriteUInt32LittleEndian(ct.AsSpan(5, 4), iterations);

        long started = Environment.TickCount64;
        Assert.Throws<InvalidDataException>(() => Decrypt(ct));
        Assert.True(Environment.TickCount64 - started < 5_000, "rejection must not wait for a huge PBKDF2 run");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(100)]
    public void RandomBytesAreNotAnAesxFile(int length)
    {
        byte[] junk = RandomNumberGenerator.GetBytes(length);
        if (length >= 4) junk[0] = (byte)'X'; // never accidentally spell the magic

        var ex = Assert.Throws<InvalidDataException>(() => Decrypt(junk));

        Assert.Contains("not an AESX file", ex.Message, StringComparison.Ordinal);
    }

    private static byte[] Encrypt(byte[] plain)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        AesxFormat.Encrypt(input, output, Password, Iterations);
        return output.ToArray();
    }

    private static byte[] EncryptDeterministic(byte[] plain, int iterations, byte[] salt, byte[] prefix)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        AesxFormat.Encrypt(input, output, Password, iterations, salt, prefix);
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] ct)
    {
        using var input = new MemoryStream(ct);
        using var output = new MemoryStream();
        AesxFormat.Decrypt(input, output, Password);
        return output.ToArray();
    }

    private static byte[] Flip(byte[] source, int offset)
    {
        byte[] copy = (byte[])source.Clone();
        copy[offset] ^= 0x01;
        return copy;
    }

    private static byte[] SwapChunks(byte[] source)
    {
        byte[] copy = (byte[])source.Clone();
        Array.Copy(source, Header + ChunkLen, copy, Header, ChunkLen);
        Array.Copy(source, Header, copy, Header + ChunkLen, ChunkLen);
        return copy;
    }
}
