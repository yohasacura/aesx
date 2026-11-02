# Using aesx

`aesx --help` prints a one-screen summary; this page has the details.

## Commands

| Command                      | Effect                                                                                                                  |
| ---------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `aesx <file>...`             | Encrypts each file to `<file>.aesx`. A file that already ends in `.aesx` is decrypted instead. This is also what happens when files are dropped onto `aesx.exe`. |
| `aesx encrypt <file>...`     | Encrypts to `<file>.aesx`. Aliases: `e`, `enc`.                                                                         |
| `aesx decrypt <file>...`     | Decrypts to `<file>` without its `.aesx` extension. Aliases: `d`, `dec`.                                                |
| `aesx selftest`              | Runs the checks built into the executable: round trips across chunk boundaries and a set of tampering attempts that must all be rejected. Takes about a second. |
| `aesx --version`, `aesx --help` | Print the version or the usage summary.                                                                              |

## Options

| Option                  | Effect                                                                                                      |
| ----------------------- | ----------------------------------------------------------------------------------------------------------- |
| `-o, --output <path>`   | Write to this path instead of the default name. If the path is an existing directory, the default name is used inside it, which also works with several inputs. |
| `-p, --password <pw>`   | Take the password from the command line. Visible to other users in the process list and kept in shell history; prefer the prompt or a pipe. |
| `-i, --iterations <n>`  | PBKDF2 iterations used when encrypting (default 600000, maximum 10000000). Decryption reads the count from the file. |
| `-f, --force`           | Overwrite an existing output file.                                                                           |
| `--delete`              | Delete the input after the output has been written. After encryption the new file is first decrypted end to end as a check; the original is kept if that fails. |
| `-q, --quiet`           | Print nothing but errors (also disables the progress display).                                               |

Options and file names may appear in any order. `--` ends option parsing, for file names that
start with a dash.

## Entering the password

**At the terminal.** aesx asks for the password without echoing it, and asks a second time when
encrypting so that a typo cannot lock you out of your own file.

**Through a pipe.** When standard input is not a terminal, the first line of it is the password:
its raw bytes, with the line ending (`\n` or `\r\n`) and a leading UTF-8 byte-order mark removed,
and no confirmation prompt:

```bash
printf '%s\n' "$AESX_PASSWORD" | aesx encrypt backup.tar
```

PowerShell 7: `"$env:AESX_PASSWORD" | aesx encrypt backup.tar`. Avoid `echo` in `cmd.exe`, which
passes a trailing space before the pipe as part of the password, and note that Windows PowerShell
5.1 encodes piped text with the console code page, so non-ASCII passwords piped from it will not
match the same password typed elsewhere.

**On the command line** with `--password`. Convenient for one-off scripts, but the password is
visible in process listings and shell history for as long as either exists.

**Encoding.** A typed or `--password` password is used as UTF-8; a piped one is used byte for
byte. Any characters work, but the same bytes must arrive on both ends; a password made of ASCII
letters, digits and punctuation behaves identically on every system, shell and keyboard layout.
Spaces are never trimmed.

## Output files

- `encrypt` writes `<file>.aesx` next to the input; `decrypt` strips the `.aesx` extension.
  Use `--output` to decrypt a file whose name does not end in `.aesx`, or to write somewhere
  else; an existing directory given to `--output` receives the default name inside it.
- An existing output is never overwritten unless you pass `--force`. A directory given as input,
  or an output path that is a directory, is refused up front.
- Output goes to a uniquely named `<output>.<random>.part` file first and is renamed into place
  only after the whole file has been processed. A wrong password, a damaged input, a full disk,
  Ctrl+C (exit code 130) or a crash leaves nothing behind.
- `--delete` removes the input afterwards; after encryption the new `.aesx` file is decrypted
  once as a check before the original goes. This is an ordinary delete, not a secure wipe; see
  [SECURITY.md](SECURITY.md#what-aesx-does-not-protect).
- On macOS and Linux, decrypted files are created with mode `0600` (owner only); encrypted files
  get the usual default permissions. The original permissions and timestamps are not stored.

## Several files at once

```bash
aesx encrypt photo1.jpg photo2.jpg notes.md
```

The password is asked once. Files are processed in order; if one fails, the rest are still
processed, each failure is reported, and the exit code is 1.

## Windows: drag and drop

Drop one or more files onto `aesx.exe` in Explorer. A console window opens, asks for the
password, encrypts the files (or decrypts them, if they end in `.aesx`) next to the originals,
and waits for a key press so the result can be read before the window closes.

For a right-click menu entry, press `Win+R`, run `shell:sendto`, and place a shortcut to
`aesx.exe` in the folder that opens. Files can then be sent to aesx from **Send to**.

## macOS and Linux

Unpack the archive, put `aesx` somewhere on your `PATH` (for example `/usr/local/bin`), and use
it from the terminal. macOS binaries are ad-hoc signed by the release build.

## First-run warnings

The binaries are not signed with a paid certificate, so the first launch triggers a warning:

- **Windows SmartScreen:** "Windows protected your PC" → **More info** → **Run anyway**. Shown once
  per download.
- **macOS Gatekeeper:** either remove the quarantine flag once,
  `xattr -d com.apple.quarantine ./aesx`, or open **System Settings → Privacy & Security** and
  choose **Open Anyway** after the first refusal.

Verify the download first (next section). If you would rather not trust a downloaded binary at
all, [BUILDING.md](BUILDING.md) shows how to produce the same executable yourself with one
command.

## Verifying a download

Every release includes `SHA256SUMS.txt` produced by the same workflow run that built the
binaries.

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing        # Linux, macOS (or: shasum -a 256 -c)
```

```powershell
Get-FileHash .\aesx-1.0.0-win-x64.zip -Algorithm SHA256   # compare with the line in SHA256SUMS.txt
```

## Scripting

Exit codes: `0` success, `1` failure (wrong password, damaged file, I/O error), `2` usage error,
`130` interrupted. Status lines go to standard output, errors and prompts to standard error;
`--quiet` silences status. Set `AESX_DEBUG=1` to get a stack trace with an error message.

```bash
#!/bin/sh
set -eu
printf '%s\n' "$BACKUP_PASSWORD" | aesx encrypt --quiet --delete "nightly-$(date +%F).sql"
```

## Performance

Key derivation with the default 600 000 iterations takes a fraction of a second on current
hardware, once per file. Encryption and decryption themselves run at several hundred megabytes
per second on processors with AES instructions, so large files are limited by disk speed. Memory
use is constant (a few megabytes) regardless of file size. For files of 64 MiB and more a
percentage is shown on the terminal while the file is processed.

## Migrating from AES Crypt

aesx does not read `.aes` files. Decrypt them with the AES Crypt installation you already have,
then encrypt the results with aesx. The day-to-day workflow is the same: one password, one file
in, one file out.

## Error messages

| Message                                                   | Meaning                                                                                   |
| --------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `wrong password, or the file is corrupted`                | The first chunk failed authentication. Nothing was written.                                |
| `authentication failed at chunk N: ...`                   | The file was modified or damaged after the point where chunk N starts.                     |
| `file is truncated or corrupted`                          | The file ends in the middle of a chunk or has nothing after the header.                    |
| `not an AESX file`                                        | Wrong magic bytes or shorter than a header.                                                |
| `unsupported AESX format version N`                       | Written by a newer aesx; update.                                                           |
| `output file already exists: ...`                         | Pass `--force` to overwrite, or `--output` to choose another name.                         |
| `'...' is a directory; aesx works on files`               | Put the folder in a `.zip` or `.tar` archive first, then encrypt the archive.              |
| `output path is a directory: ...`                         | A directory with the output's name is in the way; choose another name with `--output`.     |
| `with several input files, -o/--output must name an existing directory` | One output file cannot hold several inputs.                                  |
| `interrupted; the unfinished output file was removed`     | Ctrl+C, a closed window or a termination signal stopped the run; nothing was left behind.  |
| `AES-GCM is not available on this system`                 | The operating system's cryptography library lacks AES-GCM; aesx needs Windows 10+, macOS 13+, or Linux with OpenSSL. |
