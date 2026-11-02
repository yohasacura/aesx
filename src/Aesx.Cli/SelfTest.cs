// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Security.Cryptography;

namespace Aesx.Cli;

/// <summary>
/// Built-in self-tests (<c>aesx selftest</c>): round trips across chunk boundaries and a battery
/// of tampering attempts that must all be rejected. They live inside the executable so that a
/// downloaded binary can be checked on the very machine that is about to trust it, with no
/// test framework involved. The full suite is in <c>tests/Aesx.Tests</c>.
/// </summary>
internal static class SelfTest
{
    private const int Iterations = 1_000; // speed; key-stretching strength is not what is under test here
    private static readonly byte[] Password = "correct horse battery staple"u8.ToArray();
    private static readonly byte[] WrongPassword = "correct horse battery stapl3"u8.ToArray();

    public static int Run()
    {
        if (!AesxFormat.IsSupported)
        {
            Console.WriteLine("AES-GCM is not available on this system.");
            return Program.ExitFailure;
        }

        int failed = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            if (!ok) failed++;
        }

        const int C = AesxFormat.ChunkSize;
        const int header = AesxFormat.HeaderSize;
        const int chunkLen = C + AesxFormat.TagSize;

        Console.WriteLine("Round trips:");
        foreach (int size in new[] { 0, 1, 15, 16, 17, 1000, C - 1, C, C + 1, 2 * C, 3 * C + 12345 })
        {
            byte[] data = RandomNumberGenerator.GetBytes(size);
            byte[] ct = Encrypt(data);
            int chunks = size == 0 ? 1 : (size + C - 1) / C;
            int expectedLength = header + size + chunks * AesxFormat.TagSize;
            byte[]? pt = TryDecrypt(ct, Password);
            Check($"{size} bytes ({chunks} chunk{(chunks == 1 ? "" : "s")})",
                pt is not null && pt.AsSpan().SequenceEqual(data) && ct.Length == expectedLength);
        }

        Console.WriteLine("Tamper detection (every case must be rejected):");
        byte[] plain = RandomNumberGenerator.GetBytes(2 * C + 100); // three chunks
        byte[] good = Encrypt(plain);
        byte[] other = Encrypt(plain);
        Check("sanity: untampered file decrypts", TryDecrypt(good, Password) is { } p && p.AsSpan().SequenceEqual(plain));
        Check("sanity: same input encrypts differently each time", !other.AsSpan().SequenceEqual(good));
        Check("wrong password", Fails(good, WrongPassword));
        Check("bit flip in magic", Fails(Flip(good, 0)));
        Check("bit flip in version", Fails(Flip(good, 4)));
        Check("bit flip in iteration count", Fails(Flip(good, 5)));
        Check("bit flip in salt", Fails(Flip(good, 9)));
        Check("bit flip in nonce prefix", Fails(Flip(good, 25)));
        Check("bit flip in chunk 0 ciphertext", Fails(Flip(good, header)));
        Check("bit flip in chunk 0 tag", Fails(Flip(good, header + C)));
        Check("bit flip in chunk 1 ciphertext", Fails(Flip(good, header + chunkLen + 7)));
        Check("bit flip in last byte of file", Fails(Flip(good, good.Length - 1)));
        Check("truncated by one byte", Fails(good[..^1]));
        Check("last chunk removed", Fails(good[..(header + 2 * chunkLen)]));
        Check("truncated to first chunk", Fails(good[..(header + chunkLen)]));
        Check("truncated to header only", Fails(good[..header]));
        Check("empty input", Fails([]));
        Check("one extra byte appended", Fails([.. good, 0]));
        Check("chunks 0 and 1 swapped", Fails(SwapChunks(good, header, chunkLen)));
        Check("chunk 0 duplicated", Fails([.. good[..(header + chunkLen)], .. good[header..]]));
        Check("last chunk taken from another encryption", Fails([.. good[..(header + 2 * chunkLen)], .. other[(header + 2 * chunkLen)..]]));
        Check("header taken from another encryption", Fails([.. other[..header], .. good[header..]]));

        Console.WriteLine(failed == 0 ? "All self-tests passed." : $"{failed} self-test(s) FAILED.");
        return failed == 0 ? Program.ExitSuccess : Program.ExitFailure;
    }

    private static byte[] Encrypt(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        AesxFormat.Encrypt(input, output, Password, Iterations);
        return output.ToArray();
    }

    private static byte[]? TryDecrypt(byte[] ct, byte[] password)
    {
        try
        {
            using var input = new MemoryStream(ct);
            using var output = new MemoryStream();
            AesxFormat.Decrypt(input, output, password);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool Fails(byte[] ct, byte[]? password = null) => TryDecrypt(ct, password ?? Password) is null;

    private static byte[] Flip(byte[] source, int offset)
    {
        byte[] copy = (byte[])source.Clone();
        copy[offset] ^= 0x01;
        return copy;
    }

    private static byte[] SwapChunks(byte[] source, int body, int chunkLen)
    {
        byte[] copy = (byte[])source.Clone();
        Array.Copy(source, body + chunkLen, copy, body, chunkLen);
        Array.Copy(source, body, copy, body + chunkLen, chunkLen);
        return copy;
    }
}
