# Changelog

All notable changes to aesx are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org). The on-disk format has its own version byte,
documented in [docs/FORMAT.md](docs/FORMAT.md).

## [Unreleased]

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
