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

## Robustness and attack scenarios

Two layers go beyond the unit tests:

- **Fuzzing.** `FuzzTests` mutates valid files (bit flips, truncation, insertion, deletion,
  duplication, boundary shifts, hostile header fields, random garbage) and checks that every
  result is rejected cleanly and quickly, or decrypts to the original when the mutation was a
  no-op. The suite runs 1 500 rounds; set `AESX_FUZZ_ITERATIONS` for more and `AESX_FUZZ_SEED`
  to reproduce a run.
- **Scenarios against the real binary.** `tests/scenarios/run.sh <path-to-aesx>` exercises
  misuse and hostile conditions from the outside: directories and symbolic links, read-only and
  unreadable paths, a full filesystem (`FULL_DIR=<dir on a tiny tmpfs>`), SIGINT and SIGTERM in
  the middle of a write, empty environments, unusual passwords, concurrency. It prints PASS/FAIL
  per scenario. CI runs it on Linux and macOS after publishing the binary.

To run everything in a disposable Linux container (nothing is installed on the host):

```bash
# Build, test with a long fuzz run, publish the Linux binary into ./out
docker run --rm -v "$PWD:/src:ro" -v "$PWD/out:/out" -e AESX_FUZZ_ITERATIONS=30000 \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    cp -r /src /work && cd /work && rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj &&
    dotnet test Aesx.sln -c Release &&
    dotnet publish src/Aesx.Cli/Aesx.Cli.csproj -c Release -r linux-x64 -o /out'

# Attack the published binary as an unprivileged user, with 96 MB of memory, a read-only root
# filesystem and a 1 MiB "full" disk, on a minimal image without the SDK
docker run --rm --user 1000:1000 --memory=96m --read-only --tmpfs /tmp:size=1g --tmpfs /full:size=1m \
  -v "$PWD/out:/out:ro" -v "$PWD/tests/scenarios:/scenarios:ro" -e FULL_DIR=/full -e HOME=/tmp \
  mcr.microsoft.com/dotnet/runtime-deps:10.0 bash /scenarios/run.sh /out/aesx
```

On Windows, run these from Git Bash with `MSYS_NO_PATHCONV=1` in front of `docker`, or from
PowerShell with `${PWD}` in place of `$PWD`.

## Continuous integration

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs on every push to `main` and every
pull request, and weekly on a schedule, on Linux, Windows and macOS: restore (which fails on a
package with a known vulnerability), build with warnings as errors and the security analyzers at
their strictest level, a formatting check, an explicit vulnerability listing including transitive
packages, the full test suite, a single-file publish for that platform, a smoke test of the
published binary (version, self-tests, an encrypt/decrypt round trip of random data, and a
wrong-password check that must fail without leaving files behind), and, on Linux and macOS, the
scenario script above. [`codeql.yml`](../.github/workflows/codeql.yml) runs GitHub's CodeQL
analysis with the security-extended queries on the same triggers.

All actions are pinned to commit SHAs, and the repository's Actions settings allow only actions
authored by GitHub, require the full-SHA pin, give workflows a read-only `GITHUB_TOKEN` unless a
workflow asks for more, and hold workflows for pull requests from forks until the maintainer
approves them. Dependabot keeps the pins, the test packages and the .NET SDK version in
`global.json` current; merging an SDK update may require installing that SDK locally
(`dotnet --version` must satisfy `global.json`).

### Repository rules

`main` changes only through pull requests, with no bypass for anyone. A ruleset requires the
three CI jobs (`Build and test (ubuntu-latest)`, `(windows-latest)`, `(macos-latest)`) and the
`Analyze C#` CodeQL job to pass on a branch that is up to date with `main`, blocks merging while
CodeQL reports a high-severity alert, requires review threads to be resolved, allows squash and
rebase merges only (linear history), and blocks force pushes and deletion. Renaming one of those
jobs means updating the ruleset in the same change (Settings → Rules → Rulesets), or the pull
request cannot be merged. Two more rulesets cover `v*` tags: they can never be moved or deleted,
and only a repository admin can create one, so only an admin can trigger a release.

## Releasing

1. Move the `Unreleased` entries in [`CHANGELOG.md`](../CHANGELOG.md) under a new version heading
   with today's date, and set `<Version>` in [`Directory.Build.props`](../Directory.Build.props).
2. Open a pull request with that change, wait for the checks, and merge it.
3. As a repository admin, tag the merge commit on `main` and push the tag (the tag rulesets
   refuse tags from anyone else, and refuse moving or deleting a tag afterwards):

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
