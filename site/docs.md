---
layout: docs
title: Documentation
permalink: /docs/
---
# Documentation

aesx is small enough to be documented completely. These pages are rendered from the
repository's `docs/` folder, so they always describe the version of the code they ship with.

<div class="cards cards--docs">
  <a class="card" href="{{ '/docs/usage/' | relative_url }}">
    <strong class="card__title">Usage</strong>
    <p>Commands and options, the four ways to supply a password, output files, folders, several files at once, pipes, checking a file, non-Latin scripts, the Windows Explorer menu, first-run warnings, verifying a download, scripting, and every error message.</p>
  </a>
  <a class="card" href="{{ '/docs/security/' | relative_url }}">
    <strong class="card__title">Security</strong>
    <p>How to report a vulnerability, what aesx protects and what it cannot, choosing a password, the reasoning behind each design decision, implementation notes, the supply chain, how it is tested, and the known limitations.</p>
  </a>
  <a class="card" href="{{ '/docs/format/' | relative_url }}">
    <strong class="card__title">File format</strong>
    <p>The byte-level specification of an <code>.aesx</code> file: header, key derivation, chunks, the normative decryption procedure, security properties, limits, a test vector for independent implementations, and one such implementation.</p>
  </a>
  <a class="card" href="{{ '/docs/building/' | relative_url }}">
    <strong class="card__title">Building and releasing</strong>
    <p>Build and test with one .NET SDK, produce the single-file executable for any platform, run the fuzzer and the attack scenarios in a container, and how releases and this website are produced.</p>
  </a>
</div>

## Quick reference

| I want to…                                   | Command                                                    |
| -------------------------------------------- | ---------------------------------------------------------- |
| encrypt a file                               | `aesx encrypt report.pdf` (asks for the password twice)     |
| decrypt it                                   | `aesx decrypt report.pdf.aesx`                              |
| let the extension decide                     | `aesx report.pdf` or `aesx report.pdf.aesx`                 |
| encrypt several files with one password      | `aesx encrypt a.jpg b.jpg c.md`                             |
| encrypt a folder, and get it back            | `aesx encrypt photos`, then `aesx decrypt photos.folder.aesx` |
| write somewhere else                         | `aesx decrypt vault.aesx --output /tmp/vault.sqlite`        |
| pipe a password from a script                | `printf '%s\n' "$PW" | aesx encrypt nightly.sql --delete`  |
| stream the plaintext instead of writing it   | `aesx decrypt notes.md.aesx -o - | less`                   |
| encrypt what another program produces        | `pg_dump mydb | aesx encrypt - -o mydb.sql.aesx` (password in `AESX_PASSWORD`) |
| know that a backup still opens               | `aesx verify backup.tar.aesx`                               |
| see what a file is, without the password     | `aesx info backup.tar.aesx`                                 |
| use the right-click menu on Windows          | `aesx register` (once)                                      |
| check the executable                         | `aesx selftest`                                             |

Exit codes: `0` success, `1` failure, `2` usage error, `130` interrupted.
