# AESX file format, version 1

Status: **stable**. Every aesx 1.x release reads and writes this format unchanged. A future
format will carry a different version byte and arrive with a major release that still reads
version 1.

## Overview

An AESX file is a fixed 29-byte header followed by a sequence of chunks. Each chunk is the
AES-256-GCM encryption of up to 1 MiB of plaintext and carries its own 16-byte authentication
tag. The key is derived from a password with PBKDF2-HMAC-SHA256 and a random per-file salt.

There is no padding, no alignment and no trailer. Byte order is given per field.

## Header

| Offset | Size | Field        | Value                                                                       |
| -----: | ---: | ------------ | --------------------------------------------------------------------------- |
|      0 |    4 | magic        | the ASCII bytes `AESX` (`41 45 53 58`)                                      |
|      4 |    1 | version      | `01`                                                                        |
|      5 |    4 | iterations   | PBKDF2 iteration count, unsigned 32-bit little-endian, `1 ≤ n ≤ 100 000 000` |
|      9 |   16 | salt         | random bytes, fresh for every file                                          |
|     25 |    4 | nonce prefix | random bytes, fresh for every file                                          |

Total: 29 bytes. Readers MUST reject a file whose magic or version differs, and MUST reject an
iteration count of 0 or above 100 000 000 before deriving a key, so that a hostile header cannot
force minutes of computation.

## Key derivation

```text
key = PBKDF2-HMAC-SHA256(password, salt, iterations, dkLen = 32)
```

`password` is the byte string the application supplies. The aesx command-line tool uses the
UTF-8 encoding of the characters typed, without a byte-order mark and without Unicode
normalization; other implementations MUST do the same for files to be interchangeable. An empty
password is not permitted. Writers SHOULD use 600 000 iterations unless the user asks otherwise.

## Body

Let `C = 1 048 576` (1 MiB) and `len` be the plaintext length. The plaintext is split into
`n = max(1, ⌈len / C⌉)` chunks; chunk `i` (counting from 0) holds plaintext bytes
`[i·C, min((i+1)·C, len))`. Every chunk but the last holds exactly `C` bytes; the last holds
`1..C` bytes, or 0 bytes only when the whole plaintext is empty.

Chunk `i` is stored as `ciphertext_i || tag_i`, where

```text
nonce_i = nonce_prefix (4 bytes) || i as unsigned 64-bit big-endian (8 bytes)            (12 bytes)
aad_i   = header (29 bytes) || i as unsigned 64-bit big-endian (8 bytes) || last_i (1 byte) (38 bytes)
last_i  = 0x01 for the final chunk, 0x00 for every other chunk

(ciphertext_i, tag_i) = AES-256-GCM(key, nonce_i, plaintext_i, aad_i)   with a 128-bit tag
```

A non-final chunk therefore occupies exactly `C + 16` bytes of the file; the final chunk occupies
`r + 16` bytes with `0 ≤ r ≤ C`. The file length is `29 + len + 16·n`.

## Decryption (normative)

1. Read 29 bytes. Reject the file if fewer are available, if the magic or version is wrong, or
   if the iteration count is out of range.
2. Derive the key.
3. Read chunks with one chunk of look-ahead: read up to `C + 16` bytes, then read up to `C + 16`
   more. If the second read returns nothing, the first chunk is the final one; otherwise it is a
   non-final chunk and MUST be exactly `C + 16` bytes long. A chunk shorter than 16 bytes is a
   structural error.
4. Form `nonce_i` and `aad_i` from the header, the running index `i` and the final flag, and
   verify-and-decrypt the chunk. A tag mismatch rejects the file.
5. Release the chunk's plaintext only after its tag has verified; continue until the final chunk
   has been processed.
6. On any failure reject the whole file. Implementations SHOULD NOT leave partial output behind;
   the aesx tool writes to a temporary file and renames it into place after the final chunk.

## Security properties

- **Confidentiality** rests on AES-256-GCM with a key unique to the file (random 128-bit salt)
  and a nonce unique to each chunk within the file (counter). Nonces may coincide across files
  without harm because the keys differ; the random prefix is defence in depth.
- **Integrity** covers every byte after the header through the tags, and the header indirectly:
  it is part of every chunk's associated data, and its salt and iteration count also determine
  the key, so any change to it fails the first tag check.
- **Position binding.** The index in the nonce and in the associated data defeats reordering,
  duplication and removal of interior chunks. The `last` flag defeats truncation at a chunk
  boundary and appending. The fixed size of non-final chunks defeats truncation inside a chunk:
  the shortened chunk becomes "final" with the wrong flag and length.
- **No splicing.** A chunk or header taken from another AESX file fails because that file's key
  differs, even when both were encrypted with the same password.
- **Not hidden:** that the file is an AESX file, the plaintext length to within the overhead
  above, and the iteration count.

## Limits

| Quantity            | Limit                                             |
| ------------------- | ------------------------------------------------- |
| Plaintext per chunk | 1 048 576 bytes                                   |
| Chunks per file     | 2^32 (the NIST SP 800-38D invocation limit per key) |
| Plaintext per file  | 2^52 bytes (4 PiB)                                |
| Iterations          | 1 … 100 000 000                                   |
| Nonce / tag / key   | 96 / 128 / 256 bits                               |

## Test vector

Inputs:

```text
password     = "aesx test vector"                            (UTF-8, 16 bytes)
iterations   = 1000
salt         = 000102030405060708090a0b0c0d0e0f
nonce prefix = 01020304
plaintext    = "The quick brown fox jumps over the lazy dog"  (43 bytes)
```

Output, 88 bytes:

```text
41455358 01 e8030000 000102030405060708090a0b0c0d0e0f 01020304        header (29 bytes)
4a1c50e26e9863c67f7204ef37d91203f95df97cef603b50f223a7388303829043bfe01e82564366ca1618   ciphertext (43 bytes)
11fc7e244395ade905caacee554d63fa                                     tag (16 bytes)
```

`tests/Aesx.Tests/GoldenVectorTests.cs` checks this vector in both directions, and
`AesxFormatTests.MatchesAnIndependentImplementationOfTheSpecification` checks a multi-chunk
file against a from-scratch transcription of this document.

## Relationship to the AES Crypt `.aes` format

None. aesx does not read or write `.aes` files; see
[SECURITY.md](SECURITY.md#design-decisions) for the reasoning.
