// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace Aesx.Tests;

/// <summary>
/// Mutation fuzzing of the decryptor. Every mutated file must either be rejected with one of the
/// two documented exception types, quickly, without leaking anything but whole verified chunks,
/// or (only when the mutation happened to be the identity) decrypt to the original plaintext.
/// The default round count keeps the suite fast; set AESX_FUZZ_ITERATIONS for a long run and
/// AESX_FUZZ_SEED to reproduce one.
/// </summary>
public class FuzzTests(ITestOutputHelper output)
{
    private const int C = AesxFormat.ChunkSize;
    private const int Iterations = 1_000;
    private static readonly byte[] Password = "fuzz password"u8.ToArray();

    [Fact]
    public void MutatedFilesAreRejectedCleanlyOrDecryptToTheOriginal()
    {
        int rounds = ReadEnvironment("AESX_FUZZ_ITERATIONS", 1_500);
        int seed = ReadEnvironment("AESX_FUZZ_SEED", 20261102);
        var rng = new Random(seed);
        output.WriteLine($"rounds={rounds} seed={seed}");

        // A small corpus keeps each attempt cheap; the multi-chunk entries exercise chunk structure.
        (byte[] Plain, byte[] Cipher)[] corpus = [.. new[] { 0, 1, 17, 1000, 4096, C, C + 1, 2 * C + 100 }
            .Select(size =>
            {
                byte[] plain = new byte[size];
                rng.NextBytes(plain);
                return (plain, Encrypt(plain));
            })];

        var rejected = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int identities = 0;
        double slowest = 0;
        for (int round = 0; round < rounds; round++)
        {
            (byte[] plain, byte[] cipher) = corpus[rng.Next(corpus.Length)];
            byte[] mutated = Mutate(rng, cipher, out string kind);

            using var result = new MemoryStream();
            Exception? error = null;
            long started = Stopwatch.GetTimestamp();
            try
            {
                using var input = new MemoryStream(mutated);
                AesxFormat.Decrypt(input, result, Password);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            slowest = Math.Max(slowest, seconds);
            Assert.True(seconds < 10, $"round {round} ({kind}) took {seconds:F1} s");

            if (error is null)
            {
                Assert.True(mutated.AsSpan().SequenceEqual(cipher), $"round {round} ({kind}): a modified file decrypted successfully");
                Assert.True(plain.AsSpan().SequenceEqual(result.ToArray()), $"round {round} ({kind}): wrong plaintext");
                identities++;
            }
            else
            {
                Assert.True(error is CryptographicException or InvalidDataException,
                    $"round {round} ({kind}) threw {error.GetType().Name}: {error.Message}");
                Assert.True(result.Length % C == 0, $"round {round} ({kind}): leaked a partial chunk ({result.Length} bytes)");
                Assert.True(result.Length <= plain.Length, $"round {round} ({kind}): emitted more than the plaintext");
                Assert.True(plain.AsSpan(0, (int)result.Length).SequenceEqual(result.ToArray()), $"round {round} ({kind}): emitted bytes differ from the plaintext");
                rejected[error.GetType().Name] = rejected.GetValueOrDefault(error.GetType().Name) + 1;
            }
        }

        output.WriteLine($"identity mutations: {identities}; slowest attempt: {slowest:F3} s");
        foreach ((string type, int count) in rejected) output.WriteLine($"rejected with {type}: {count}");
    }

    private static readonly (string Name, Func<Random, byte[], byte[]> Apply)[] Mutations =
    [
        ("flip-bit", (r, b) => b.Length == 0 ? [1] : Edit(b, c => c[r.Next(c.Length)] ^= (byte)(1 << r.Next(8)))),
        ("set-byte", (r, b) => b.Length == 0 ? [0] : Edit(b, c => c[r.Next(c.Length)] = (byte)r.Next(256))),
        ("truncate", (r, b) => b[..r.Next(b.Length + 1)]),
        ("insert", (r, b) => { int at = r.Next(b.Length + 1); return [.. b[..at], .. RandomBytes(r, 1 + r.Next(64)), .. b[at..]]; }),
        ("delete", (r, b) => { if (b.Length == 0) return b; int at = r.Next(b.Length); int n = Math.Min(1 + r.Next(64), b.Length - at); return [.. b[..at], .. b[(at + n)..]]; }),
        ("duplicate-range", (r, b) => { if (b.Length == 0) return b; int at = r.Next(b.Length); int n = Math.Min(1 + r.Next(2 * C), b.Length - at); int to = r.Next(b.Length + 1); return [.. b[..to], .. b[at..(at + n)], .. b[to..]]; }),
        ("iterations", (r, b) => b.Length < 9 ? b : Edit(b, c => BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(5, 4), PickIterations(r)))),
        ("version", (r, b) => b.Length < 5 ? b : Edit(b, c => c[4] = (byte)r.Next(256))),
        ("magic", (r, b) => b.Length < 4 ? b : Edit(b, c => c[r.Next(4)] = (byte)r.Next(256))),
        ("garbage-tail", (r, b) => { int n = Math.Min(1 + r.Next(64), b.Length); return [.. b[..^n], .. RandomBytes(r, n)]; }),
        ("swap-blocks", (r, b) => b.Length < 32 ? b : Edit(b, c => { int x = r.Next(c.Length / 16) * 16, y = r.Next(c.Length / 16) * 16; Span<byte> t = stackalloc byte[16]; c.AsSpan(x, 16).CopyTo(t); c.AsSpan(y, 16).CopyTo(c.AsSpan(x, 16)); t.CopyTo(c.AsSpan(y, 16)); })),
        ("zero-range", (r, b) => { if (b.Length == 0) return b; int at = r.Next(b.Length); int n = Math.Min(1 + r.Next(64), b.Length - at); return Edit(b, c => Array.Clear(c, at, n)); }),
        ("append", (r, b) => [.. b, .. RandomBytes(r, 1 + r.Next(64))]),
        ("prefix-garbage", (r, b) => [.. RandomBytes(r, 1 + r.Next(32)), .. b]),
        ("garbage-file", (r, _) => RandomBytes(r, r.Next(300))),
        ("chunk-boundary-shift", (r, b) => b.Length <= 29 + 16 ? b : Edit(b, c => { int at = 29 + r.Next(c.Length - 29); c[at] ^= 0x80; })),
    ];

    private static byte[] Mutate(Random rng, byte[] cipher, out string kind)
    {
        int steps = 1 + rng.Next(3);
        var names = new List<string>(steps);
        byte[] current = cipher;
        for (int i = 0; i < steps; i++)
        {
            (string name, Func<Random, byte[], byte[]> apply) = Mutations[rng.Next(Mutations.Length)];
            names.Add(name);
            current = apply(rng, current);
        }
        kind = string.Join("+", names);
        return current;
    }

    private static uint PickIterations(Random rng) => rng.Next(8) switch
    {
        0 => 0,
        1 => 1,
        2 => AesxFormat.MaxIterations + 1u,
        3 => uint.MaxValue,
        4 => 0x8000_0000u,
        _ => (uint)rng.Next(1, 5000),
    };

    private static byte[] Edit(byte[] source, Action<byte[]> edit)
    {
        byte[] copy = (byte[])source.Clone();
        edit(copy);
        return copy;
    }

    private static byte[] RandomBytes(Random rng, int n)
    {
        byte[] bytes = new byte[n];
        rng.NextBytes(bytes);
        return bytes;
    }

    private static byte[] Encrypt(byte[] plain)
    {
        using var input = new MemoryStream(plain);
        using var result = new MemoryStream();
        AesxFormat.Encrypt(input, result, Password, Iterations);
        return result.ToArray();
    }

    private static int ReadEnvironment(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : fallback;
}
