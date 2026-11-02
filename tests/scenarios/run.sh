#!/usr/bin/env bash
# Adversarial and fool-proofing scenarios, run against a real aesx executable.
#
#   tests/scenarios/run.sh <path-to-aesx>
#
# Optional environment:
#   FULL_DIR   a directory on a nearly full filesystem (e.g. a 1 MiB tmpfs); enables the disk-full scenario
#
# Every scenario prints PASS or FAIL; the exit status is the number of failures.
# Written for bash 3.2 (macOS) and later.
set -u

AESX=${1:?usage: run.sh <path-to-aesx>}
case "$AESX" in /*) ;; *) AESX=$PWD/$AESX ;; esac
[ -x "$AESX" ] || { echo "not executable: $AESX" >&2; exit 2; }

WORK=$(mktemp -d "${TMPDIR:-/tmp}/aesx-scenarios.XXXXXX")
trap 'chmod -R u+rwX "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT
cd "$WORK" || exit 2

PW=scenario-password
FAST=(--iterations 1000)
failures=0
ROOT=0; [ "$(id -u)" = "0" ] && ROOT=1

pass()  { printf '  [PASS] %s\n' "$1"; }
flunk() { printf '  [FAIL] %s\n' "$1"; failures=$((failures + 1)); }
skip()  { printf '  [SKIP] %s\n' "$1"; }
section() { printf '%s\n' "$1"; }

# run <expected-exit> <stdin|-> <args...>   -> sets CODE, OUT, ERR; succeeds when CODE matches
run() {
    local expected=$1 input=$2
    shift 2
    if [ "$input" = "-" ]; then
        "$AESX" "$@" </dev/null >"$WORK/.out" 2>"$WORK/.err"; CODE=$?
    else
        printf '%s' "$input" | "$AESX" "$@" >"$WORK/.out" 2>"$WORK/.err"; CODE=$?
    fi
    OUT=$(cat "$WORK/.out"); ERR=$(cat "$WORK/.err")
    [ "$CODE" = "$expected" ]
}
expect() { # expect <name> <expected-exit> <stdin|-> <args...>
    local name=$1; shift
    if run "$@"; then pass "$name"; else flunk "$name (exit $CODE; stderr: $(printf '%s' "$ERR" | tr '\n' ' ' | head -c 300))"; fi
}
listing() { ls -A "$1" | head -8 | tr '\n' ' '; }
has() { printf '%s' "$2" | grep -q -- "$1"; }
no_leftovers() { [ -z "$(find "$1" -name '*.part' -print -quit 2>/dev/null)" ]; }
random_file() { head -c "$2" /dev/urandom > "$1"; }
mode_of() { stat -c %a "$1" 2>/dev/null || stat -f %Lp "$1"; }

section "Basics"
random_file plain.bin 3000000
expect "encrypt with -p" 0 - encrypt plain.bin -p "$PW" "${FAST[@]}"
expect "decrypt to -o" 0 - decrypt plain.bin.aesx -p "$PW" -o plain.out
cmp -s plain.bin plain.out && pass "round trip is byte-exact" || flunk "round trip differs"
expect "self-tests" 0 - selftest
has "All self-tests passed" "$OUT" && pass "self-test summary" || flunk "self-test summary"

section "Wrong password and damaged files leave nothing behind"
mkdir wp && cp plain.bin.aesx wp/f.aesx
expect "wrong password -> 1" 1 - decrypt wp/f.aesx -p nope
has "wrong password" "$ERR" && pass "wrong password message" || flunk "wrong password message: $ERR"
[ "$(ls wp)" = "f.aesx" ] && pass "no output after wrong password" || flunk "stray files: $(listing wp)"
cp plain.bin.aesx wp/t.aesx; printf '\x01' | dd of=wp/t.aesx bs=1 seek=1500000 conv=notrunc 2>/dev/null
expect "flipped byte -> 1" 1 - decrypt wp/t.aesx -p "$PW"
head -c 2000000 plain.bin.aesx > wp/trunc.aesx
expect "truncated -> 1" 1 - decrypt wp/trunc.aesx -p "$PW"
cp plain.bin.aesx wp/tail.aesx; printf 'x' >> wp/tail.aesx
expect "extra byte -> 1" 1 - decrypt wp/tail.aesx -p "$PW"
cp plain.bin.aesx wp/huge.aesx; printf '\xff\xff\xff\xff' | dd of=wp/huge.aesx bs=1 seek=5 conv=notrunc 2>/dev/null
start=$(date +%s)
expect "crafted 4-billion-iteration header -> 1" 1 - decrypt wp/huge.aesx -p "$PW"
[ $(( $(date +%s) - start )) -lt 5 ] && pass "rejected without deriving a key" || flunk "took too long"
printf 'AESX' > wp/short.aesx
expect "header-only garbage -> 1" 1 - decrypt wp/short.aesx -p "$PW"
: > wp/empty.aesx
expect "empty .aesx -> 1" 1 - decrypt wp/empty.aesx -p "$PW"
ls wp | grep -q -v '\.aesx$' && flunk "stray files in wp: $(listing wp)" || pass "no stray files after failures"
no_leftovers wp && pass "no .part files" || flunk ".part files left"

section "Paths and names"
mkdir folder
expect "directory as input -> 1" 1 - encrypt folder -p "$PW"
has "is a directory" "$ERR" && pass "directory message" || flunk "directory message: $ERR"
mkdir outdir
expect "-o existing directory" 0 - encrypt plain.bin -p "$PW" -o outdir "${FAST[@]}"
[ -f outdir/plain.bin.aesx ] && pass "default name inside -o directory" || flunk "missing outdir/plain.bin.aesx"
printf 'a' > a.txt; printf 'b' > b.txt
expect "two inputs into -o directory" 0 - encrypt a.txt b.txt -p "$PW" -o outdir "${FAST[@]}"
[ -f outdir/a.txt.aesx ] && [ -f outdir/b.txt.aesx ] && pass "both written" || flunk "not both written"
expect "two inputs with -o file -> 2" 2 - encrypt a.txt b.txt -p "$PW" -o out.aesx
expect "-o into missing directory -> 1" 1 - encrypt a.txt -p "$PW" -o missing/dir/a.aesx "${FAST[@]}"
[ ! -e missing ] && pass "missing directory not created" || flunk "directory was created"
expect "same file -> 1" 1 - encrypt a.txt -p "$PW" -f -o a.txt
has "same file" "$ERR" && pass "same-file message" || flunk "same-file message: $ERR"
mkdir a.txt.aesx
expect "output path is a directory -> 1" 1 - encrypt a.txt -p "$PW" "${FAST[@]}"
rmdir a.txt.aesx
expect "nonexistent input -> 1" 1 - encrypt nope.txt -p "$PW"
expect "-o '' -> 2" 2 - encrypt a.txt -p "$PW" -o ""
printf 'spaced' > "имя с пробелами (v2).txt"
expect "unicode + spaces" 0 - encrypt "имя с пробелами (v2).txt" -p "$PW" "${FAST[@]}"
printf 'dash' > ./-dash.txt
expect "name starting with dash after --" 0 - encrypt -p "$PW" "${FAST[@]}" -- -dash.txt
[ -f ./-dash.txt.aesx ] && pass "dash file encrypted" || flunk "dash file missing"

section "Overwrite protection and symbolic links"
expect "existing output refused" 1 - encrypt a.txt -p "$PW" -o outdir/a.txt.aesx "${FAST[@]}"
has "already exists" "$ERR" && pass "exists message" || flunk "exists message"
expect "existing output with -f" 0 - encrypt a.txt -p "$PW" -f -o outdir/a.txt.aesx "${FAST[@]}"
if ln -s plain.bin victim-link.aesx 2>/dev/null; then
    printf 'do not touch' > victim.txt; rm -f victim-link.aesx; ln -s victim.txt victim-link.aesx
    expect "-f onto a symlink output" 0 - encrypt a.txt -p "$PW" -f -o victim-link.aesx "${FAST[@]}"
    [ "$(cat victim.txt)" = "do not touch" ] && pass "symlink target untouched" || flunk "symlink target overwritten"
    [ ! -L victim-link.aesx ] && pass "link replaced by a regular file" || flunk "still a link"
    ln -s a.txt alias.txt
    expect "symlink alias of input as output -> 1" 1 - encrypt a.txt -p "$PW" -f -o alias.txt
else
    skip "symbolic links not available here"
fi

section "Permissions"
if [ "$ROOT" = "1" ]; then
    skip "running as root: permission scenarios are meaningless"
else
    mkdir ro && cp a.txt ro/ && chmod 555 ro
    expect "read-only directory -> 1" 1 - encrypt ro/a.txt -p "$PW" "${FAST[@]}"
    [ "$(ls ro)" = "a.txt" ] && pass "nothing created in read-only directory" || flunk "stray: $(listing ro)"
    chmod 755 ro
    cp a.txt unreadable.txt && chmod 000 unreadable.txt
    expect "unreadable input -> 1" 1 - encrypt unreadable.txt -p "$PW" "${FAST[@]}"
    [ ! -e unreadable.txt.aesx ] && pass "no output for unreadable input" || flunk "output created"
    chmod 644 unreadable.txt
fi
expect "decrypt for permission check" 0 - decrypt outdir/a.txt.aesx -p "$PW" -o perm.txt
[ "$(mode_of perm.txt)" = "600" ] && pass "decrypted file is owner-only (600)" || flunk "mode is $(mode_of perm.txt)"

section "Disk full"
if [ -n "${FULL_DIR:-}" ] && [ -d "$FULL_DIR" ]; then
    expect "encrypt onto a full filesystem -> 1" 1 - encrypt plain.bin -p "$PW" -o "$FULL_DIR/x.aesx" "${FAST[@]}"
    [ -z "$(ls -A "$FULL_DIR")" ] && pass "nothing left on the full filesystem" || flunk "left: $(ls -A "$FULL_DIR")"
else
    skip "FULL_DIR not set"
fi

section "Interrupts"
random_file big.bin 400000000
mkdir sig
# Job control must be on: without it, bash starts background jobs with SIGINT ignored, and the
# child inherits that, which would test bash rather than aesx.
set -m
for sig in INT TERM HUP; do
    "$AESX" encrypt big.bin -p "$PW" -o "sig/$sig.aesx" "${FAST[@]}" >/dev/null 2>"$WORK/.sigerr" &
    pid=$!
    # Strike while the temporary file is being written, however fast or slow the disk is.
    for _ in $(seq 1 100); do ls sig/"$sig".aesx.*.part >/dev/null 2>&1 && break; sleep 0.05; done
    kill -"$sig" "$pid" 2>/dev/null
    wait "$pid" 2>/dev/null; code=$?
    [ "$code" = "130" ] && pass "SIG$sig -> exit 130" || flunk "SIG$sig -> exit $code ($(tr '\n' ' ' < "$WORK/.sigerr" | head -c 200))"
    [ -z "$(ls -A sig)" ] && pass "SIG$sig left no output or .part" || flunk "SIG$sig leftovers: $(listing sig)"
done
set +m
rm -f big.bin

section "Passwords"
expect "empty password via stdin -> 1" 1 "" encrypt a.txt "${FAST[@]}"
expect "no stdin at all -> 1" 1 - encrypt a.txt "${FAST[@]}"
expect "-p '' -> 2" 2 - encrypt a.txt -p ""
printf 'utf' > u.txt
expect "UTF-8 password via pipe" 0 "пароль✓日本語
" encrypt u.txt "${FAST[@]}"
expect "same UTF-8 password with CRLF" 0 "пароль✓日本語
" decrypt u.txt.aesx -o u.out
cmp -s u.txt u.out && pass "UTF-8 password round trip" || flunk "UTF-8 round trip differs"
expect "BOM before password is ignored" 0 "$(printf '\xef\xbb\xbf')$PW
" encrypt u.txt -o u2.aesx "${FAST[@]}"
expect "...and decrypts without the BOM" 0 "$PW
" decrypt u2.aesx -o u2.out
expect "password with spaces kept verbatim" 0 "  spaced
" encrypt u.txt -o u3.aesx "${FAST[@]}"
expect "trimmed version fails" 1 "spaced
" decrypt u3.aesx -o u3.out
long=$(head -c 20000 /dev/zero | tr '\0' 'x')
expect "20000-character password" 0 "$long
" encrypt u.txt -o u4.aesx "${FAST[@]}"
expect "...decrypts" 0 "$long
" decrypt u4.aesx -o u4.out

section "Options"
expect "-i 10 warns" 0 - encrypt u.txt -p "$PW" -o i.aesx -i 10
has "low" "$ERR" && pass "low-iteration warning" || flunk "no warning"
expect "-i on decrypt warns" 0 - decrypt i.aesx -p "$PW" -o i.out -i 5000
has "ignored" "$ERR" && pass "ignored-iteration warning" || flunk "no warning"
expect "-i 0 -> 2" 2 - encrypt u.txt -p "$PW" -i 0
expect "-i abc -> 2" 2 - encrypt u.txt -p "$PW" -i abc
expect "-i above the cap -> 2" 2 - encrypt u.txt -p "$PW" -i 10000001
expect "unknown option -> 2" 2 - encrypt u.txt --bogus
expect "no arguments -> 2" 2 -
has "Usage:" "$ERR" && pass "usage on stderr" || flunk "no usage"
expect "--version" 0 - --version
has "aesx " "$OUT" && pass "version string" || flunk "version string"
expect "--delete verifies then removes" 0 - encrypt plain.out -p "$PW" --delete "${FAST[@]}"
has "verified" "$OUT" && [ ! -e plain.out ] && [ -f plain.out.aesx ] && pass "original gone, output verified" || flunk "delete flow: $OUT"
printf 'auto' > auto.txt
expect "auto mode decides by extension" 0 - plain.out.aesx auto.txt -p "$PW" "${FAST[@]}"
[ -f plain.out ] && [ -f auto.txt.aesx ] && pass "one decrypted, one encrypted" || flunk "auto mode: $(listing .)"
: > zero.bin
expect "empty file" 0 - encrypt zero.bin -p "$PW" "${FAST[@]}"
[ "$(wc -c < zero.bin.aesx | tr -d ' ')" = "45" ] && pass "empty file is 45 bytes" || flunk "size $(wc -c < zero.bin.aesx)"

section "Hostile environment"
env -i HOME=/nonexistent PATH=/usr/bin:/bin "$AESX" --version >/dev/null 2>&1 && pass "runs with empty environment and bogus HOME" || flunk "fails with empty environment"
env -i "$AESX" selftest >/dev/null 2>&1 && pass "self-tests with empty environment" || flunk "self-tests fail with empty environment"
( cd / && "$AESX" encrypt "$WORK/u.txt" -p "$PW" -o "$WORK/cwd.aesx" "${FAST[@]}" >/dev/null 2>&1 ) && pass "unwritable working directory is fine" || flunk "needs writable cwd"

section "Concurrency"
for i in 1 2 3 4; do "$AESX" encrypt plain.bin -p "$PW" -o "par$i.aesx" "${FAST[@]}" >/dev/null 2>&1 & done
wait
ok=1; for i in 1 2 3 4; do "$AESX" decrypt "par$i.aesx" -p "$PW" -o "par$i.out" >/dev/null 2>&1 && cmp -s plain.bin "par$i.out" || ok=0; done
[ "$ok" = "1" ] && pass "four parallel encryptions all decrypt" || flunk "parallel run broke"
no_leftovers . && pass "no .part files anywhere" || flunk ".part files left: $(find . -name '*.part')"

printf '\n%s\n' "$([ "$failures" = "0" ] && echo "All scenarios passed." || echo "$failures scenario(s) FAILED.")"
exit "$failures"
