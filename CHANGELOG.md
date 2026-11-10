# Changelog

All notable changes to aesx are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org). The on-disk format has its own version byte,
documented in [docs/FORMAT.md](docs/FORMAT.md).

## [Unreleased]

The file format is unchanged: every 1.x release reads what every other one writes.

### Added

- **Folders.** `aesx encrypt photos` writes `photos.folder.aesx`: the folder is packed into a tar
  archive and encrypted in one pass, without a plaintext archive ever touching the disk, and its
  names and structure are hidden with its contents. `aesx decrypt photos.folder.aesx` restores
  the folder into a private temporary one that takes the real name only after the whole archive
  has been read and verified; an existing folder is never touched, with or without `--force`.
  Modification times and, on macOS and Linux, permission bits are kept (restored for the owner
  only). Symbolic links and junctions are left out and reported, and only archives that aesx
  itself writes are unpacked.
- **Data on standard input.** A single `-` as the input reads the data from a pipe, the
  counterpart of `-o -`: `pg_dump mydb | aesx encrypt - -o mydb.sql.aesx`. The password then comes
  from `AESX_PASSWORD` or `-p`.
- **`aesx verify`**, which decrypts and discards: proof that a file is complete, undamaged and
  opens with the password, without writing anything. For an encrypted folder it also walks the
  archive inside.
- **`aesx info`**, which prints what a file's header says (format version, iterations, chunks,
  decrypted size) without a password.
- **Windows Explorer integration.** `aesx register` adds "Encrypt with aesx" for files and
  folders and "Decrypt with aesx" for `.aesx` files to the right-click menu of the current user,
  and `aesx unregister` removes them. A selection of any size goes to a single aesx, which asks
  for the password once. No administrator rights, and nothing installed into Explorer.
- **A password window on Windows.** When Explorer started aesx (a menu entry, a double-click,
  files dropped onto `aesx.exe`), the password is typed into a small window with a confirmation
  field and a "Show the password" box, instead of blind into a console.
- **Installation through Homebrew and Scoop.** `brew install yohasacura/tap/aesx` on macOS and
  Linux; `scoop bucket add yohasacura https://github.com/yohasacura/scoop-bucket` and
  `scoop install aesx` on Windows. Both install the release executables by checksum and follow
  new releases by themselves.
- **Build provenance for releases.** The release workflow attests every published file;
  `gh attestation verify <file> --repo yohasacura/aesx` shows that it was built by this
  repository's release workflow from the tagged commit.
- **A second, independent reader**, `tests/interop/aesx-decrypt.mjs`: the format implemented from
  its specification in about a hundred lines of JavaScript, with Node.js and no packages. CI runs
  it against what each build writes, and it is a way to your data that does not depend on aesx.
- `AesxEncryptStream` and `AesxDecryptStream` in `Aesx.Core`, for data that is produced or
  consumed piece by piece, and `AesxFormat.ReadHeader` and `TryGetLayout`.

### Fixed

- On Windows, `aesx encrypt *.txt` failed with a message about invalid characters, because no
  shell expands wildcards for programs there. aesx now expands `*` and `?` in the file name
  itself (files only, in alphabetical order, without hidden and system files).
- The usage guide said that with `-o -` a damaged file fails before any byte is written. That
  holds for a wrong password and for damage in the first chunk only: chunks before the damaged
  one are verified and written as they come. The guide now says so, and what to do about it.
- The README and the website promised that building from source gives "the identical
  executable", which the building guide rightly does not; the wording is gone, and provenance
  attestations now do the job that sentence pretended to.

### Changed

- All four ways into the format (`Encrypt`, `Decrypt` and the two stream classes) share one
  implementation of the chunk cryptography. The bytes on disk, the exceptions and the test vector
  are as before.
- `--delete` is refused for a folder; it removes files only.
- A name ending in `.folder.aesx` now means an encrypted folder. A plain file that happened to
  be called `something.folder` and was encrypted with an earlier version is no folder archive, and
  `aesx decrypt` says so instead of writing it: rename the `.aesx` file, or take its bytes with
  `-o -`.
- Some messages are reworded: an interrupted run reports "the unfinished output was removed", a
  directory given to `decrypt` is "a directory; only files can be decrypted", and the error for
  several inputs with a single output file speaks of "inputs".
- In the release workflow only the job that publishes has write permissions; the jobs that build
  and test can read the source and nothing else.

## [1.0.1] - 2026-11-09

A maintenance release. The program, its options and the file format are unchanged, and files are
interchangeable between 1.0.0 and 1.0.1.

### Added

- The website, <https://yohasacura.github.io/aesx>: a landing page whose download links follow
  the latest release, and the documentation and this changelog rendered from the repository.
- Issue and pull request templates and a code of conduct.

### Changed

- `global.json` pins .NET SDK 10.0.403 (was 10.0.100), so building from source needs that SDK or
  a later 10.0 one.
- `main` and the release tags are protected by repository rulesets that nobody can bypass, and
  only GitHub-authored actions pinned to full commit SHAs may run; the security and building
  guides describe both.
- CI and the release workflow use `actions/setup-dotnet` 6.1.0 and CodeQL Action 4.39.3.

## [1.0.0] - 2026-11-02

First release.

### Added

- `aesx encrypt` and `aesx decrypt`, plus a command-free mode that decides by file extension so
  files can simply be dropped onto the executable on Windows.
- AESX format version 1: AES-256-GCM in independently authenticated 1 MiB chunks, with the key
  derived by PBKDF2-HMAC-SHA256 (600 000 iterations by default) from a random 128-bit salt per
  file. Reordering, duplication, truncation and header tampering are all detected.
- Atomic output through a uniquely named, exclusively created temporary file; overwrite
  protection (`--force`, enforced by the final rename itself); several inputs per run;
  `--output` accepting a directory or `-` for standard output; password entry from an echo-free
  prompt, the `AESX_PASSWORD` environment variable, a pipe (raw bytes; UTF-8 and UTF-16
  byte-order marks and the line ending handled; lines over 1 MiB refused), or `--password`.
- File names and passwords in any script. Control characters and bidirectional overrides in
  file names are shown as `\u{XXXX}` escapes in output; the prompt warns about invisible
  characters in a password and deletes whole emoji on Backspace.
- `--delete`, which after encryption decrypts the new file end to end before removing the
  original.
- Clean handling of Ctrl+C, a closed console window, SIGTERM and SIGHUP: the unfinished output is
  removed and the process exits with code 130.
- Up-front refusal of directories as input, directories in the way of the output, same-file
  input and output (following symbolic links), and an empty `--output`; warnings for a low or
  irrelevant `--iterations` and for encrypting an `.aesx` file again.
- Decrypted files are created owner-only (`0600`) on Unix. A progress percentage is shown for
  files of 64 MiB and more when standard error is a terminal.
- The iteration count accepted in a header is capped at 10 000 000, bounding the work a hostile
  file can demand.
- `aesx selftest`: round-trip and tamper-detection checks built into the executable.
- Self-contained single-file builds for win-x64, win-arm64, osx-x64, osx-arm64, linux-x64 and
  linux-arm64, produced from tags by GitHub Actions together with `SHA256SUMS.txt`.
- An xUnit suite covering the format (chunk boundaries, tamper cases, cancellation, a mutation
  fuzzer, an independent re-implementation of the specification, a fixed test vector) and the
  command-line tool end to end, plus `tests/scenarios/run.sh`, which attacks a published binary
  with symbolic links, permission problems, a full disk, signals, hostile names and environments.
- Supply-chain checks: NuGet vulnerability audit on every restore, CodeQL analysis, GitHub Actions
  pinned to commit SHAs, weekly scheduled runs, and Dependabot for actions, test packages and the
  .NET SDK.

[Unreleased]: https://github.com/yohasacura/aesx/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/yohasacura/aesx/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/yohasacura/aesx/releases/tag/v1.0.0
