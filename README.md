# aesx

**Free, offline, open-source file encryption for Windows, macOS and Linux.**
One password, one file in, one `.aesx` file out: the AES Crypt workflow, without the licence key.

[![CI](https://github.com/yohasacura/aesx/actions/workflows/ci.yml/badge.svg)](https://github.com/yohasacura/aesx/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/yohasacura/aesx?display_name=tag&sort=semver)](https://github.com/yohasacura/aesx/releases/latest)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

```text
$ aesx tax-return-2025.pdf
Password:
Confirm password:
Encrypting tax-return-2025.pdf -> tax-return-2025.pdf.aesx
Done: 1,048,621 bytes in 0.1 s
```

## Why aesx

- **Modern, authenticated cryptography.** AES-256-GCM with a key derived by PBKDF2-HMAC-SHA256
  (600 000 iterations, random 128-bit salt for every file). A wrong password, a flipped bit, a
  missing chunk or a truncated file is detected and rejected; nothing is ever silently decrypted
  into garbage.
- **No third-party code.** The product is this repository plus the .NET base class library,
  nothing else. Every cryptographic primitive comes from the operating system (Windows CNG,
  Apple CryptoKit, OpenSSL on Linux): hardware-accelerated, widely reviewed, not reimplemented
  here.
- **Completely offline.** No network code, no telemetry, no update checks, no activation. The
  whole product is six small source files; read them.
- **Any file size, constant memory.** Data is processed in 1 MiB chunks, each authenticated on
  its own, so a 100 GB disk image needs the same few megabytes of RAM as a text file.
- **Hard to misuse.** Never overwrites without `--force`, never leaves a half-written file behind
  (not on a wrong password, a full disk or Ctrl+C), verifies an encrypted file before `--delete`
  removes the original, and refuses directories, same-file output and other foot-guns up front.
  Fuzzed and attacked in CI on every change.
- **One executable, about 11 MB.** Download, unpack, run. On Windows you can also drop files onto
  `aesx.exe`.
- **Apache 2.0.** Free for personal and commercial use.

## Install

Download the archive for your platform from the
[latest release](https://github.com/yohasacura/aesx/releases/latest), check it against
`SHA256SUMS.txt`, and unpack it anywhere, ideally somewhere on your `PATH`.

| Platform                    | Archive                                                              |
| --------------------------- | -------------------------------------------------------------------- |
| Windows x64 / ARM64         | `aesx-<version>-win-x64.zip`, `aesx-<version>-win-arm64.zip`         |
| macOS Apple Silicon / Intel | `aesx-<version>-osx-arm64.tar.gz`, `aesx-<version>-osx-x64.tar.gz`   |
| Linux x64 / ARM64           | `aesx-<version>-linux-x64.tar.gz`, `aesx-<version>-linux-arm64.tar.gz` |

The binaries are not signed with a paid certificate, so Windows SmartScreen and macOS Gatekeeper
warn once; [docs/USAGE.md](docs/USAGE.md#first-run-warnings) shows the two clicks (or the one
`xattr` command) that get past it, and [docs/BUILDING.md](docs/BUILDING.md) shows how to build
the identical executable yourself with one command.

## Usage

```text
aesx <file>...                    encrypt; files ending in .aesx are decrypted instead
aesx encrypt <file>... [options]  encrypt file(s) to <file>.aesx
aesx decrypt <file>... [options]  decrypt file(s) to <file> without .aesx
aesx selftest                     run the built-in self-tests

-o, --output <path>    output file, or an existing directory to write into
-p, --password <pw>    password on the command line (prefer the prompt or stdin)
-i, --iterations <n>   PBKDF2 iterations when encrypting (default 600000)
-f, --force            overwrite an existing output file
    --delete           delete the input after the output is written (and, for
                       encryption, verified)
-q, --quiet            no progress output
```

```bash
aesx encrypt backup.tar                      # -> backup.tar.aesx, password asked twice
aesx decrypt backup.tar.aesx                 # -> backup.tar
aesx photo1.jpg photo2.jpg notes.md          # several files, one password
aesx decrypt vault.aesx --output /tmp/vault.sqlite
printf '%s\n' "$PASSWORD" | aesx encrypt nightly.sql --delete    # from a script
```

The password is read from the terminal without echo; when stdin is a pipe, its first line is
used. Exit codes: 0 success, 1 failure, 2 usage error, 130 interrupted. Everything else is in
[docs/USAGE.md](docs/USAGE.md).

## How it works

```text
file    = header (29 bytes) || chunk_0 || chunk_1 || ... || chunk_last
header  = "AESX" || 0x01 || iterations (u32 LE) || salt (16 bytes) || nonce_prefix (4 bytes)
key     = PBKDF2-HMAC-SHA256(password, salt, iterations) -> 32 bytes
chunk_i = AES-256-GCM(key, nonce = nonce_prefix || i, aad = header || i || is_last)(plaintext_i) || tag_i
```

Each 1 MiB chunk carries its own tag, bound to its index, to the header and to an end-of-file
flag, so reordering, duplicating, truncating or splicing chunks is caught, and plaintext is only
released after its tag verifies. The complete specification with a test vector is in
[docs/FORMAT.md](docs/FORMAT.md); the threat model and the reasoning behind each choice are in
[docs/SECURITY.md](docs/SECURITY.md).

## Relationship to AES Crypt

aesx is an independent project with no affiliation to AES Crypt, and it does not read or write
`.aes` files. To migrate, decrypt with the AES Crypt you already have and encrypt with aesx.

## Repository layout

```text
src/Aesx.Core/     the format: encrypt and decrypt streams (one file, no dependencies)
src/Aesx.Cli/      the aesx command-line tool
tests/Aesx.Tests/  xUnit suite: format, tamper cases, fuzzing, spec conformance, test vector, CLI end to end
tests/scenarios/   attack and misuse scenarios run against the published binary
docs/              format specification, security, usage, building and releasing
.github/           CI on Linux, Windows and macOS; releases built from tags
```

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md). Report security issues privately as described in
[docs/SECURITY.md](docs/SECURITY.md#reporting-a-vulnerability).

## License

[Apache License 2.0](LICENSE). Copyright (c) 2026 yohasacura.
