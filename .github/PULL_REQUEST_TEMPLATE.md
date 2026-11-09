<!-- Keep it short: what changes, why, and how you verified it. -->

## What and why

## Checklist

- [ ] `dotnet build` is warning-free and `dotnet test` passes locally
- [ ] No new runtime dependency (the product references no packages) and no home-made cryptography
- [ ] The bytes on disk are unchanged, or this is a format change agreed in an issue first
      (new version byte, reader for the old format, `docs/FORMAT.md` and the test vector updated)
- [ ] `CHANGELOG.md` has a line under `Unreleased` for every user-visible change
- [ ] `docs/` updated where behaviour changed
