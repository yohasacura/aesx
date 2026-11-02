// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura

using System.Security.Cryptography;

namespace Aesx.Tests;

/// <summary>
/// A fixed input/output pair, also printed in docs/FORMAT.md. If this test fails, the bytes on
/// disk have changed: that is a format break and needs a new format version, not a new vector.
/// </summary>
public class GoldenVectorTests
{
    private const int Iterations = 1000;
    private static readonly byte[] Password = "aesx test vector"u8.ToArray();
    private static readonly byte[] Salt = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] NoncePrefix = Convert.FromHexString("01020304");
    private static readonly byte[] Plaintext = "The quick brown fox jumps over the lazy dog"u8.ToArray();

    private const string ExpectedHex =
        "4145535801e8030000000102030405060708090a0b0c0d0e0f01020304" +        // header
        "4a1c50e26e9863c67f7204ef37d91203f95df97cef603b50f223a7388303829043bfe01e82564366ca1618" + // ciphertext (43 bytes)
        "11fc7e244395ade905caacee554d63fa";                                   // tag

    [Fact]
    public void EncryptionReproducesTheVector()
    {
        using var input = new MemoryStream(Plaintext);
        using var output = new MemoryStream();

        AesxFormat.Encrypt(input, output, Password, Iterations, Salt, NoncePrefix);

        Assert.Equal(ExpectedHex, Convert.ToHexStringLower(output.ToArray()));
    }

    [Fact]
    public void TheVectorDecrypts()
    {
        using var input = new MemoryStream(Convert.FromHexString(ExpectedHex));
        using var output = new MemoryStream();

        AesxFormat.Decrypt(input, output, Password);

        Assert.Equal(Plaintext, output.ToArray());
    }

    [Fact]
    public void TheVectorRejectsAnotherPassword()
    {
        using var input = new MemoryStream(Convert.FromHexString(ExpectedHex));
        using var output = new MemoryStream();

        Assert.Throws<CryptographicException>(() => AesxFormat.Decrypt(input, output, "aesx test vector!"u8));
        Assert.Equal(0, output.Length);
    }
}
