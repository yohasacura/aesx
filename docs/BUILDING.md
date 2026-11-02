# Building and releasing

## Prerequisites

The [.NET SDK](https://dotnet.microsoft.com/download) version pinned in [`global.json`](../global.json)
(10.0.x). Nothing else: no native toolchain, no package manager, no third-party packages at
runtime. The test project pulls xUnit from NuGet; the product does not reference any package.

## Build and test

```bash
dotnet build Aesx.sln --configuration Release   # warnings are errors, analyzers at latest-recommended
dotnet test  Aesx.sln --configuration Release   # unit, conformance and end-to-end CLI tests
dotnet run --project src/Aesx.Cli -- selftest   # the self-tests that ship inside the binary
```

## Produce the single-file executable

```bash
dotnet publish src/Aesx.Cli/Aesx.Cli.csproj --configuration Release --runtime <rid> --output out
```

| `<rid>`       | Platform                         |
| ------------- | -------------------------------- |
| `win-x64`     | Windows 10/11, x64               |
| `win-arm64`   | Windows 11 on ARM                |
| `osx-arm64`   | macOS 13+, Apple Silicon         |
| `osx-x64`     | macOS 13+, Intel                 |
| `linux-x64`   | Linux, x64 (glibc)               |
| `linux-arm64` | Linux, ARM64 (glibc)             |

`out/` then holds one file, `aesx` or `aesx.exe`: a self-contained, trimmed, compressed bundle of
the program and the parts of the .NET runtime it uses. It needs no .NET installation on the target
machine. Any host can publish for any target; cross-publishing a macOS binary from Windows or
Linux works, but only a Mac can `codesign` it, which the release workflow does.

## Continuous integration

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs on every push to `main` and every
pull request, on Linux, Windows and macOS: restore, build with warnings as errors, the full test
suite, a single-file publish for that platform, and a smoke test of the published binary
(version, self-tests, an encrypt/decrypt round trip of random data, and a wrong-password check
that must fail without leaving files behind).

Dependabot keeps the GitHub Actions versions and the test packages current.

## Releasing

1. Move the `Unreleased` entries in [`CHANGELOG.md`](../CHANGELOG.md) under a new version heading
   with today's date, and set `<Version>` in [`Directory.Build.props`](../Directory.Build.props).
2. Commit, push, and wait for CI to pass.
3. Tag and push the tag:

   ```bash
   git tag -a v1.2.3 -m "aesx 1.2.3"
   git push origin v1.2.3
   ```

[`.github/workflows/release.yml`](../.github/workflows/release.yml) then runs the test suite and
builds the binary on each target platform's own runner (including ARM64 Windows and Linux and
Intel macOS), ad-hoc signs the macOS binaries, runs the shipped self-tests against each binary,
packages them as `.zip` (Windows) or `.tar.gz` (others), computes `SHA256SUMS.txt`, and creates
the GitHub release with the notes taken from the matching CHANGELOG section. A tag with a
suffix such as `v1.3.0-rc.1` is published as a pre-release.

## Versioning

The product follows [Semantic Versioning](https://semver.org). The file format has its own
version byte (see [FORMAT.md](FORMAT.md)); any change to it is a major release and keeps the
ability to read the previous format.

## Reproducing a release binary

Builds are deterministic at the IL level (`Deterministic`, `ContinuousIntegrationBuild`), so
publishing the same tag with the same SDK version produces the same managed assemblies. The
single-file bundle also contains the .NET runtime from Microsoft's runtime packs and, on macOS,
an ad-hoc signature, so byte-identical bundles across machines are not guaranteed; the
authoritative artefacts are the ones attached to the release, and the workflow run that produced
them is linked from the release page.
