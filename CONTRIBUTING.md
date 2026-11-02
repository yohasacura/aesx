# Contributing

Thank you for taking the time. aesx is deliberately small; the most valuable contributions keep
it that way.

## Ground rules

- **No runtime dependencies.** The product must keep building from the .NET base class library
  alone. Test-only packages are fine when they earn their place.
- **No home-made cryptography.** Primitives come from `System.Security.Cryptography`, which
  delegates to the operating system. Pull requests that implement a cipher, hash or KDF by hand
  will not be merged.
- **Warnings are errors**, and the analyzers run at `latest-recommended`. `dotnet build` must be
  clean and `dotnet test` must pass on your machine before you open a pull request; CI repeats
  both on Linux, Windows and macOS.
- **Format changes are special.** Anything that alters the bytes on disk needs a new format
  version, a reader for the old one, an update to [docs/FORMAT.md](docs/FORMAT.md) with a new
  test vector, and an entry in the CHANGELOG. Open an issue to discuss it first.

## Workflow

1. Open an issue for anything beyond a small fix, so the scope is agreed before the work.
2. Fork, branch from `main`, make the change with tests.
3. Add a line under `Unreleased` in [CHANGELOG.md](CHANGELOG.md).
4. Open a pull request. Commit messages follow the
   [Conventional Commits](https://www.conventionalcommits.org) style (`feat:`, `fix:`, `docs:`,
   `test:`, `ci:`, `chore:`).

## Security issues

Do not report them in public issues or pull requests. See
[docs/SECURITY.md](docs/SECURITY.md#reporting-a-vulnerability).

## License

By contributing you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE), the same license as the project.
