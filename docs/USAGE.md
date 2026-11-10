# Using aesx

`aesx --help` prints a one-screen summary; this page has the details.

## Commands

| Command                      | Effect                                                                                                                  |
| ---------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `aesx <path>...`             | Encrypts each file to `<file>.aesx` and each folder to `<folder>.folder.aesx`. A file that already ends in `.aesx` is decrypted instead. This is also what happens when files are dropped onto `aesx.exe`, or onto the aesx application that `aesx register` creates on a Mac. |
| `aesx encrypt <path>...`     | Encrypts files and [folders](#folders). Aliases: `e`, `enc`.                                                            |
| `aesx decrypt <file>...`     | Decrypts to `<file>` without its `.aesx` extension; a `.folder.aesx` file becomes the folder again. Aliases: `d`, `dec`. |
| `aesx verify <file>...`      | Decrypts and throws the result away: proof that the file is intact and the password right, without writing anything. See [Checking a file](#checking-a-file). |
| `aesx info <file>...`        | Prints what a file's header says: format version, iterations, chunks, decrypted size. No password involved.              |
| `aesx register`, `aesx unregister` | Windows and macOS: adds aesx to the right-click menu of Explorer or the Finder for the current user, or removes it. See [Windows Explorer](#windows-explorer) and [macOS Finder](#macos-finder). |
| `aesx selftest`              | Runs the checks built into the executable: round trips across chunk boundaries and a set of tampering attempts that must all be rejected. Takes about a second. |
| `aesx --version`, `aesx --help` | Print the version or the usage summary.                                                                              |

## Options

| Option                  | Effect                                                                                                      |
| ----------------------- | ----------------------------------------------------------------------------------------------------------- |
| `-o, --output <path>`   | Write to this path instead of the default name. If the path is an existing directory, the default name is used inside it, which also works with several inputs. `-o -` writes to standard output (see [Pipes](#pipes)). |
| `-p, --password <pw>`   | Take the password from the command line. Visible to other users in the process list and kept in shell history; prefer the prompt or a pipe. |
| `-i, --iterations <n>`  | PBKDF2 iterations used when encrypting (default 600000, maximum 10000000). Decryption reads the count from the file. |
| `-f, --force`           | Overwrite an existing output file. (A folder is never overwritten.)                                          |
| `--delete`              | Delete the input file after the output has been written. After encryption the new file is first decrypted end to end as a check; the original is kept if that fails. Folders are not deleted. |
| `-q, --quiet`           | Print nothing but errors (also disables the progress display).                                               |
| `--finder`              | macOS only: ask for the password in a dialog and report problems in one, instead of on a terminal. It is what the Finder entries use; see [macOS Finder](#macos-finder). |

Options and names may appear in any order. `--` ends option parsing, for file names that start
with a dash. A single `-` in place of a file name means standard input.

## Entering the password

The first of these sources that is present is used:

1. **`--password <pw>`** on the command line. Convenient for one-off commands, but the password is
   visible in process listings and shell history for as long as either exists.
2. **The `AESX_PASSWORD` environment variable.** The usual choice for scripts: it does not appear
   in the command line or in history. (Other processes of the same user can still read it, and
   `export` leaves it in the shell session; unset it afterwards.)
3. **Standard input, when it is not a terminal.** The first line of it is the password: its raw
   bytes, with the line ending (`\n` or `\r\n`) and a leading UTF-8 byte-order mark removed, and no
   confirmation prompt. A line longer than 1 MiB is refused, which catches a data file connected
   to stdin by mistake. A file saved by Notepad as UTF-16 is recognised by its byte-order mark and
   converted, so it matches the same password typed at a prompt.

   ```bash
   printf '%s\n' "$MY_SECRET" | aesx encrypt backup.tar
   ```

   PowerShell 7: `"$env:MY_SECRET" | aesx encrypt backup.tar`. Avoid `echo` in `cmd.exe`, which
   passes a trailing space before the pipe as part of the password, and note that Windows
   PowerShell 5.1 encodes piped text with the console code page, so non-ASCII passwords piped from
   it will not match the same password typed elsewhere.

   When standard input carries the data itself (`aesx encrypt -`), it cannot also carry the
   password, and one of the first two sources is required.
4. **The person at the keyboard.** In a terminal aesx asks for the password without echoing it,
   and asks a second time when encrypting so that a typo cannot lock you out of your own file.
   Backspace works (and removes a whole emoji, not half of one), Ctrl+U starts over, and keys that
   produce nothing visible (arrows, function keys, Ctrl combinations) are ignored rather than
   silently added. When Windows Explorer started aesx (a menu entry, a double-click, files dropped
   onto `aesx.exe`), the same questions are asked in a small window instead, with a box to show
   what was typed; closing that window cancels the run and nothing is written. On a Mac, what
   the Finder starts asks in a dialog of macOS's own: once to decrypt, and a second time when
   encrypting; Cancel ends the run and nothing is written.

**Encoding.** A typed, `--password` or `AESX_PASSWORD` password is used as UTF-8; a piped one is
used byte for byte. Any characters work, including Hebrew, Arabic, Chinese, Japanese and emoji,
but the same bytes must arrive on both ends; a password made of ASCII letters, digits and
punctuation behaves identically on every system, shell and keyboard layout. Spaces are never
trimmed. If a password contains characters that are invisible or only affect layout (zero-width
joiners, right-to-left marks, byte-order marks, stray control characters), aesx says so, with
their code points, because they are part of the password and are easy to lose when retyping it.

**Git Bash / mintty on Windows.** mintty connects programs to a pipe rather than a console, so
the prompt cannot hide what you type. Run `winpty aesx ...`, or use a pipe or `AESX_PASSWORD`.

## Output files

- `encrypt` writes `<file>.aesx` next to the input; `decrypt` strips the `.aesx` extension.
  Use `--output` to decrypt a file whose name does not end in `.aesx`, or to write somewhere
  else; an existing directory given to `--output` receives the default name inside it.
- An existing output is never overwritten unless you pass `--force`. An output path that is a
  directory is refused up front.
- Output goes to a uniquely named `<output>.<random>.part` file first and is renamed into place
  only after the whole file has been processed. A wrong password, a damaged input, a full disk,
  Ctrl+C (exit code 130) or a crash leaves nothing behind.
- `--delete` removes the input file afterwards; after encryption the new `.aesx` file is
  decrypted once as a check before the original goes. This is an ordinary delete, not a secure
  wipe; see [SECURITY.md](SECURITY.md#what-aesx-does-not-protect).
- On macOS and Linux, decrypted files are created with mode `0600` (owner only); encrypted files
  get the usual default permissions. For a single file the original permissions and timestamps
  are not stored.

## Folders

```bash
aesx encrypt photos                # -> photos.folder.aesx
aesx decrypt photos.folder.aesx    # -> photos/
```

A folder is packed into a tar archive and encrypted in one pass; the archive exists only inside
the encrypted file, never on disk in the clear. Decrypting a file whose name ends in
`.folder.aesx` unpacks it again.

**What goes in.** The folder itself, and every file and folder below it, with their names,
contents, modification times and, on macOS and Linux, permission bits. Hidden files are included.
Not included: owners, access control lists, extended attributes and alternate data streams.

- Symbolic links and junctions are neither followed nor stored. Each one is named in a warning,
  and the summary line counts them.
- Named pipes, sockets and devices are stored as empty files, without being opened.
- A file that cannot be read stops the run and nothing is written: an archive with a silent hole
  in it would be worse than none. A file that gets shorter while it is being read stops the run
  as well. One that grows is cut off at the length it had when aesx opened it, with a warning.
  Encrypt folders that nothing is writing to.
- `--delete` is refused for folders. Check the result (`aesx verify`) and remove the folder
  yourself.

**What comes out.** `aesx decrypt photos.folder.aesx` creates `photos` next to the file, named
after the file rather than after the folder that was packed, so a renamed archive unpacks under
its new name.

- An existing folder is never touched, with or without `--force`: move it away, or point
  `--output` somewhere else. `-o <existing directory>` puts the folder inside that directory;
  `-o <new path>` is the folder to create.
- It is all or nothing. Everything is unpacked into a private `<name>.<random>.part` folder that
  takes the real name only once the whole archive has been read and verified. A wrong password, a
  damaged file, a full disk or Ctrl+C leaves nothing behind.
- On macOS and Linux, restored files and folders belong to their owner only: a file keeps the
  owner's read, write and execute bits and loses the ones for group and others.
- Only archives that aesx itself writes are unpacked: one top-level folder with plain files and
  folders below it. Anything else in there (a link, a second top-level entry, a name that leads
  outside the folder or that this system cannot represent, such as `aux.txt` or a trailing dot on
  Windows) rejects the whole archive, and nothing is written.

**The name decides.** `.folder.aesx` means "unpack me"; every other name is a file. If you send a
folder to another name (`aesx encrypt photos -o backup.aesx`), decrypting gives the tar archive
`backup`, not the folder, and aesx says so when encrypting. In the other direction, the tar
archive inside any `.folder.aesx` file is available as it is:

```bash
aesx decrypt photos.folder.aesx -o - | tar -tvf -     # list it
aesx decrypt photos.folder.aesx -o - > photos.tar     # or keep it (not in Windows PowerShell 5.1,
                                                      # whose > rewrites binary data; rename the
                                                      # file to photos.tar.aesx and decrypt that)
```

It is an ordinary POSIX (pax) archive that any `tar` reads.

## Several files at once

```bash
aesx encrypt photo1.jpg photo2.jpg notes.md
```

The password is asked once. Inputs are processed in order; if one fails, the rest are still
processed, each failure is reported, and the exit code is 1.

**Wildcards.** On macOS and Linux the shell expands `*.jpg` before aesx starts. On Windows
neither `cmd.exe` nor PowerShell does that for programs, so aesx expands `*` and `?` itself, in
the file name only (not in the folders leading to it): matches are files, in alphabetical order,
without hidden and system files, as `dir` and `copy` would pick them. A pattern that matches
nothing is an error.

## Pipes

`-o -` sends the result to standard output instead of a file, and a single `-` as the input
reads the data from standard input, so plaintext never has to touch the disk on either side.
Status lines move to standard error.

```bash
aesx decrypt notes.md.aesx -o - | less
aesx decrypt db.sql.aesx -o - | psql mydb
aesx encrypt big.iso -o - | ssh backup-host 'cat > big.iso.aesx'

export AESX_PASSWORD=...                       # standard input is taken, see below
pg_dump mydb | aesx encrypt - -o mydb.sql.aesx
ssh backup-host 'cat big.iso.aesx' | aesx decrypt - -o big.iso
tar -cf - project | aesx encrypt - -o - | ssh backup-host 'cat > project.tar.aesx'
```

- One input only, in either direction.
- With `-` as the input, say `encrypt` or `decrypt` (there is no name to decide by) and give
  `-o`. The password comes from `AESX_PASSWORD` or `-p`, because standard input is busy.
- `--delete` is refused with `-` and with `-o -`: there is nothing to delete in the first case
  and nothing that could be verified or kept in the second.
- Encrypting onto a terminal is refused unless you pass `--force`, since binary data on a
  terminal helps nobody.

**What a pipe cannot take back.** A wrong password fails before a single byte is written. Damage
further into the file is different: aesx verifies each 1 MiB chunk before releasing it, so
everything up to the damaged chunk has already gone down the pipe when aesx stops with exit
code 1. What was written is genuine, but it is not the whole file. Let the exit code decide what
happens next (`set -o pipefail` in bash), or decrypt to a file first: that is all or nothing.

## Checking a file

```bash
aesx verify backup.tar.aesx photos.folder.aesx
aesx info backup.tar.aesx
```

`verify` asks for the password and decrypts every chunk without writing anything, which is the
only way to know that a file is complete, undamaged and opens with that password. For an
encrypted folder it also walks the archive inside, with the same checks that unpacking applies.
Run it on a backup before you rely on it, and after copying files to media you do not trust.

`info` needs no password. It reads the 29-byte header and the file's length and prints the
format version, the iteration count, the number of chunks and the decrypted size. That is what
the file claims: only `verify`, or decrypting, proves it.

## Non-Latin scripts and unusual characters

File names and passwords in Hebrew, Arabic, Chinese, Japanese, Cyrillic, emoji, or any mix of them
are ordinary input and are tested as such. Two things are worth knowing:

- aesx prints file names as they are, with one exception: control characters (which can carry
  terminal escape sequences) and the Unicode right-to-left override and isolate characters
  (which can make `report‮txt.exe` look like `reportexe.txt`) are shown as `\u{XXXX}`
  escapes. The file itself is processed under its real name.
- Some systems type accented characters as one code point and others as a base letter plus a
  combining mark; the two are different passwords. Typing the password on the same kind of system
  that encrypted the file, or sticking to ASCII for passwords that must travel, avoids the issue.

## Windows Explorer

```text
aesx register
```

adds aesx to the right-click menu of the current user:

- **Encrypt with aesx** on files and folders,
- **Decrypt with aesx** on `.aesx` files, which is also what a double-click on one then does.

Select as many items as you like: they all go to one aesx, which asks for the password once, in
a small window (twice when encrypting, to catch a typo). When it is done the new files are
simply there; a message appears only if something was left out or went wrong. On Windows 11 the
entries are under **Show more options** (or Shift+right-click), because the short menu is
reserved for packaged applications.

The entries point at the `aesx.exe` that registered them, so put it where it will stay before
registering, and run `aesx register` again if you move it. `aesx unregister` removes everything.
No administrator rights are involved and nothing is installed into Explorer: registering writes
a handful of values under `HKEY_CURRENT_USER\Software\Classes` (the `.aesx` file type, the two
menu entries, and the class through which Explorer hands the selection to `aesx.exe`).

**Drag and drop** needs no registration: drop one or more files or folders onto `aesx.exe`. A
console window opens, the password window appears, the files are encrypted (or decrypted, if
they end in `.aesx`) next to the originals, and the console waits for a key press so the result
can be read before it closes.

## macOS Finder

```text
aesx register
```

sets up the Finder for the current user:

- **Encrypt with aesx** in the right-click menu of files and folders,
- **Decrypt with aesx** in the right-click menu of `.aesx` files,
- a double-click on an `.aesx` file decrypts it,
- files and folders can be dropped onto **aesx** in the Applications folder of your home folder
  (`~/Applications`), which decrypts what ends in `.aesx` and encrypts the rest. Opened by
  itself, it lets you choose the files. Drag it into the Dock if you want it at hand.

Select as many items as you like: they all go to one aesx, which asks for the password once, in
a dialog (and a second time when encrypting, to catch a typo). When it is done the new files are
simply there; a message appears only if something was left out or went wrong. While aesx works
on what a menu entry started, macOS turns a gear in the menu bar, and that gear's menu can stop
it.

One exception concerns the application, not the menu entries: macOS hands it the files that
carry its "downloaded from the internet" mark separately from the rest. A selection that mixes
both kinds, dropped on aesx or opened by double-click, is therefore asked about twice, once for
each part. The right-click entries receive everything at once.

The two entries are services. macOS lists them at the bottom of the right-click menu, or one
level down under **Services** when several services apply to what is selected, which is common
for folders. System Settings → Keyboard → Keyboard Shortcuts → Services is where they can be
switched off, or given a keyboard shortcut.

Everything points at the `aesx` that registered it, so put it where it will stay before
registering, and run `aesx register` again if you move it. An aesx that is reached through a
link on your `PATH`, which is how Homebrew installs it, is registered under that link, so the
entries survive an upgrade. `aesx unregister` removes everything. No administrator rights are
involved and nothing is installed into the Finder. Registering writes three items into your
home folder, and deleting them by hand is all there is to unregistering, should aesx itself be
gone:

| Item                                               | What it is                                                           |
| -------------------------------------------------- | -------------------------------------------------------------------- |
| `~/Library/Services/Encrypt with aesx.workflow`    | An Automator workflow that runs one shell script with the selection. |
| `~/Library/Services/Decrypt with aesx.workflow`    | The same for `.aesx` files.                                          |
| `~/Applications/aesx.app`                          | An AppleScript droplet that macOS compiles on the spot; it owns the `.aesx` file type. |

All three only start aesx with `--finder` in front of the paths, which is what makes it ask in
a dialog instead of on a terminal that is not there. The switch works in an Automator workflow
or a Shortcut of your own as well: `aesx --finder encrypt -- "$@"`.

macOS guards the Desktop, Documents and Downloads folders. Should it ask whether "Automator
Workflow Runner" (the menu entries) or "aesx" (the application) may access files there, that is
aesx about to read what you selected; if you decline, aesx reports that it could not read the
file, and nothing is written.

## Installing

**Homebrew** (macOS and Linux) and **Scoop** (Windows) install the same executables that are
attached to the releases, check them against a recorded SHA-256, put `aesx` on your `PATH` and
keep it up to date:

```bash
brew install yohasacura/tap/aesx
```

```powershell
scoop bucket add yohasacura https://github.com/yohasacura/scoop-bucket
scoop install aesx
```

The two small repositories behind those commands,
[homebrew-tap](https://github.com/yohasacura/homebrew-tap) and
[scoop-bucket](https://github.com/yohasacura/scoop-bucket), follow new releases by themselves
and, from 1.1.0 on, refuse a release file whose build provenance does not verify.

**By hand:** unpack the archive from the release page and put `aesx` somewhere on your `PATH`
(for example `/usr/local/bin`, or any folder you add to `PATH` on Windows). macOS binaries are
ad-hoc signed by the release build.

Either way, `aesx register` then adds aesx to the right-click menu on [Windows](#windows-explorer)
and [macOS](#macos-finder).

## First-run warnings

The binaries are not signed with a paid certificate, so the first launch of a file you
downloaded yourself triggers a warning. (Homebrew and Scoop do not mark what they download as
coming from the internet, so an aesx installed through them starts without one.)

- **Windows SmartScreen:** "Windows protected your PC" → **More info** → **Run anyway**. Shown once
  per download.
- **macOS Gatekeeper:** either remove the quarantine flag once,
  `xattr -d com.apple.quarantine ./aesx`, or open **System Settings → Privacy & Security** and
  choose **Open Anyway** after the first refusal.

Verify the download first (next section). If you would rather not trust a downloaded binary at
all, [BUILDING.md](BUILDING.md) shows how to build the executable yourself with one command.

## Verifying a download

Every release includes `SHA256SUMS.txt` produced by the same workflow run that built the
binaries.

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing        # Linux, macOS (or: shasum -a 256 -c)
```

```powershell
Get-FileHash .\aesx-1.1.0-win-x64.zip -Algorithm SHA256   # compare with the line in SHA256SUMS.txt
```

A checksum shows that the file is the one on the release page. Releases from 1.1.0 on also carry
a build provenance attestation, which shows where it came from: a statement signed by GitHub and
recorded in a public transparency log that this exact file was built by this repository's
release workflow from the tagged commit. With the [GitHub CLI](https://cli.github.com):

```bash
gh attestation verify aesx-1.1.0-win-x64.zip --repo yohasacura/aesx
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
percentage is shown on the terminal while the file is processed; for a folder, whose size is not
known in advance, a running count of files and megabytes.

## Migrating from AES Crypt

aesx does not read `.aes` files. Decrypt them with the AES Crypt installation you already have,
then encrypt the results with aesx. The day-to-day workflow is the same: one password, one file
in, one file out, from the terminal or from the right-click menu. On a Mac, where AES Crypt is
an icon to drop files onto, `aesx register` gives you that as well: an aesx application that
takes what is dropped on it, and `.aesx` files that open with a double-click.

## If aesx is ever not at hand

An `.aesx` file does not depend on this program. The format is documented byte for byte in
[FORMAT.md](FORMAT.md), and the repository holds a second reader written from that page alone:
[`tests/interop/aesx-decrypt.mjs`](https://github.com/yohasacura/aesx/blob/main/tests/interop/aesx-decrypt.mjs),
about a hundred lines of JavaScript that need nothing but Node.js.

```bash
AESX_PASSWORD=... node aesx-decrypt.mjs backup.tar.aesx backup.tar
```

## Error messages

| Message                                                   | Meaning                                                                                   |
| --------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `wrong password, or the file is corrupted`                | The first chunk failed authentication. Nothing was written.                                |
| `authentication failed at chunk N: ...`                   | The file was modified or damaged after the point where chunk N starts.                     |
| `file is truncated or corrupted`                          | The file ends in the middle of a chunk or has nothing after the header.                    |
| `not an AESX file`                                        | Wrong magic bytes or shorter than a header.                                                |
| `unsupported AESX format version N`                       | Written by a newer aesx; update.                                                           |
| `output file already exists: ...`                         | Pass `--force` to overwrite, or `--output` to choose another name.                         |
| `output folder already exists: ...`                       | A folder is never overwritten. Move it away, or choose another place with `--output`.      |
| `output path is a directory: ...`                         | A directory with the output's name is in the way; choose another name with `--output`.     |
| `'...' is a directory; only files can be decrypted`       | `decrypt`, `verify` and `info` take files. (`encrypt` takes folders too.)                  |
| `--delete does not remove folders: ...`                   | Encrypt the folder without `--delete` and remove it yourself after checking the result.    |
| `not a folder archive: ...`, `the archive holds ...`      | The file is named `.folder.aesx` but does not contain a folder as aesx packs one. Nothing was written. `-o -` still gives you the decrypted bytes. |
| `cannot read '...'`, `'...' became shorter while it was being read` | A file inside a folder could not be read completely; nothing was written.        |
| `with several inputs, -o/--output must name an existing directory` | One output file cannot hold several inputs.                                       |
| `output file appeared while working: ...`                 | Something created the output while aesx was running; nothing was overwritten. Rerun with `--force` if that was intended. |
| `invalid output path '...'`                               | The path contains characters the operating system does not allow in names, or is malformed.  |
| `no files match '...'`                                    | Windows: the wildcard pattern matched no file.                                             |
| `refusing to write encrypted data to the terminal`        | `-o -` with standard output still attached to the terminal; redirect it or add `--force`.   |
| `data read from standard input needs -o <file>, ...`      | With `-` as the input there is no name to derive the output from.                          |
| `standard input carries the data, so the password has to come from ...` | Set `AESX_PASSWORD` or use `-p` when the input is `-`.                       |
| `the first line of standard input is longer than 1 MiB`   | Standard input is connected to a data file, not a password; use `-p` or `AESX_PASSWORD`, or fix the redirection. |
| `interrupted; the unfinished output was removed`          | Ctrl+C, a closed window or a termination signal stopped the run; nothing was left behind.  |
| `no dialog could be shown to ask for the password (...)`  | macOS: `--finder` was used where there is no desktop to show a dialog on (over SSH, for instance). Leave the switch out there and use the prompt, a pipe or `AESX_PASSWORD`. |
| `aesx is no longer at ...` (in a dialog)                  | macOS: a Finder entry points at an aesx that was moved or deleted. Run `aesx register` again from where it is now. |
| `AES-GCM is not available on this system`                 | The operating system's cryptography library lacks AES-GCM; aesx needs Windows 10+, macOS 13+, or Linux with OpenSSL. |
