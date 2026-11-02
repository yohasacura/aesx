# Changelog

All notable changes to aesx are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org). The on-disk format has its own version byte,
documented in [docs/FORMAT.md](docs/FORMAT.md).

## [Unreleased]

## [1.0.0] - 2026-11-02

First release.

### Added

- `aesx encrypt` and `aesx decrypt`, plus a command-free mode that decides by file extension so
  files can simply be dropped onto the executable on Windows.
- AESX format version 1: AES-256-GCM in independently authenticated 1 MiB chunks, with the key
  derived by PBKDF2-HMAC-SHA256 (600 000 iterations by default) from a random 128-bit salt per
  file. Reordering, duplication, truncation and header tampering are all detected.
- Atomic output through a temporary file, overwrite protection (`--force`), optional removal of
  the input (`--delete`), several inputs per run, and password entry from an echo-free prompt,
  from a pipe, or with `--password`.
- `aesx selftest`: round-trip and tamper-detection checks built into the executable.
- Self-contained single-file builds for win-x64, win-arm64, osx-x64, osx-arm64, linux-x64 and
  linux-arm64, produced from tags by GitHub Actions together with `SHA256SUMS.txt`.
- An xUnit suite covering the format (chunk boundaries, tamper cases, an independent
  re-implementation of the specification, a fixed test vector) and the command-line tool end to
  end.

[Unreleased]: https://github.com/yohasacura/aesx/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/yohasacura/aesx/releases/tag/v1.0.0
