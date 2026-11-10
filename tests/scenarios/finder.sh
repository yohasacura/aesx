#!/usr/bin/env bash
# The Finder integration on macOS, for real: registers a published aesx in the account this
# runs in, uses what was registered the way the Finder does, and unregisters again.
#
#   AESX_REGISTER_FOR_REAL=1 tests/scenarios/finder.sh <path-to-aesx>
#
# This changes the right-click menu and the owner of .aesx files for the account, and it types
# into dialogs on the screen. It is meant for a Mac nobody is sitting at (CI runs it), so it
# refuses to start without the variable above, and on an account where aesx is registered.
#
# Optional environment:
#   SCREENSHOTS   a directory that receives pictures of the Finder's menu and of the dialogs
#
# Typing into a dialog needs the Accessibility permission for whatever runs this script
# (System Settings > Privacy & Security); GitHub's runners have it.
# Every scenario prints PASS or FAIL; the exit status is the number of failures.
# Written for bash 3.2 (macOS).
set -u

AESX=${1:?usage: finder.sh <path-to-aesx>}
case "$AESX" in /*) ;; *) AESX=$PWD/$AESX ;; esac
[ -x "$AESX" ] || { echo "not executable: $AESX" >&2; exit 2; }
[ "$(uname)" = "Darwin" ] || { echo "this is about the Finder; it needs a Mac" >&2; exit 2; }
[ "${AESX_REGISTER_FOR_REAL:-}" = "1" ] || { echo "this registers aesx for real in this account; set AESX_REGISTER_FOR_REAL=1 to say you mean it" >&2; exit 2; }

SERVICES=$HOME/Library/Services
APPLICATIONS=$HOME/Applications
APP=$APPLICATIONS/aesx.app
ENCRYPT="Encrypt with aesx"
DECRYPT="Decrypt with aesx"
if [ -e "$APP" ] || [ -e "$SERVICES/$ENCRYPT.workflow" ] || [ -e "$SERVICES/$DECRYPT.workflow" ]; then
    echo "aesx is registered in this account already; this script would replace and then remove that" >&2
    exit 2
fi
HAD_APPLICATIONS=0; [ -d "$APPLICATIONS" ] && HAD_APPLICATIONS=1

case "${SCREENSHOTS:-}" in "") ;; /*) ;; *) SCREENSHOTS=$PWD/$SCREENSHOTS ;; esac
[ -n "${SCREENSHOTS:-}" ] && mkdir -p "$SCREENSHOTS"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/aesx-finder.XXXXXX")
WORK=$(cd "$WORK" && pwd -P)   # the name the Finder will use for it
# However this ends, the account is left as it was found.
REGISTERED=0
cleanup() {
    [ -e "$AESX.moved" ] && [ ! -e "$AESX" ] && mv "$AESX.moved" "$AESX"
    [ "$REGISTERED" = 1 ] && "$AESX" unregister >/dev/null 2>&1
    rm -rf "$WORK"
}
trap cleanup EXIT
cd "$WORK" || exit 2

PW='finder scenario ✓ пароль'
PBS=/System/Library/CoreServices/pbs
failures=0

pass()  { printf '  [PASS] %s\n' "$1"; }
flunk() { printf '  [FAIL] %s\n' "$1"; failures=$((failures + 1)); }
skip()  { printf '  [SKIP] %s\n' "$1"; }
section() { printf '%s\n' "$1"; }
within() { perl -e 'alarm shift; exec @ARGV' "$@"; }   # within <seconds> <command...>
shot() { [ -n "${SCREENSHOTS:-}" ] && screencapture -x "$SCREENSHOTS/$1.png" 2>/dev/null; return 0; }
key() { within 20 osascript -e "tell application \"System Events\" to key code $1" >/dev/null 2>&1; }
typed() { within 20 osascript -e "tell application \"System Events\" to keystroke \"$1\"" >/dev/null 2>&1; }

# until_ <seconds> <command...>: repeats the command until it succeeds
until_() {
    local deadline=$(( $(date +%s) + $1 )); shift
    while ! "$@" >/dev/null 2>&1; do
        [ "$(date +%s)" -ge "$deadline" ] && return 1
        sleep 0.5
    done
}
exists() { [ -e "$1" ]; }
idle() { ! pgrep -f "$AESX --finder" >/dev/null; }     # no aesx that an entry started is running
left() { ! pgrep -f "$APP/Contents/MacOS" >/dev/null; } # the droplet has quit

# answer <seconds> <text to type, or ""> <button, or ""> <what the dialog has to say>...
# Finds a dialog of aesx by its words, fills in its field and presses the button. The dialogs
# belong to the osascript that aesx runs, or to the droplet, which is called aesx as well.
# Without a button it only waits for the dialog to be there, which is when to take its picture.
answer() {
    local deadline=$(( $(date +%s) + $1 )); shift
    while :; do
        LOOKED=$(within 30 osascript - "$@" <<'EOF' 2>&1
on run argv
    set typed to item 1 of argv
    set pressed to item 2 of argv
    set wanted to items 3 thru -1 of argv
    -- This script runs in an osascript of its own, which is left out: asked about its own
    -- windows while it waits for the answer, it would wait for itself.
    set myself to (do shell script "echo $PPID") as integer
    tell application "System Events"
        repeat with shower in (every process whose (name is "osascript" or name is "aesx" or name is "droplet") and unix id is not myself)
            repeat with shown in (every window of shower)
                set said to ""
                repeat with line_ in (every static text of shown)
                    set said to said & (value of line_ as text) & linefeed
                end repeat
                set matches to true
                considering case
                    repeat with part in wanted
                        if said does not contain (part as text) then set matches to false
                    end repeat
                end considering
                if matches then
                    if typed is not "" then set value of text field 1 of shown to typed
                    if pressed is not "" then click button pressed of shown
                    return "answered"
                end if
            end repeat
        end repeat
    end tell
    return "nothing there"
end run
EOF
)
        [ "$LOOKED" = "answered" ] && return 0
        [ "$(date +%s)" -ge "$deadline" ] && return 1
        sleep 0.3
    done
}

# perform <service> <paths...>: asks the system for a service by its menu title, with files on
# the pasteboard, which is what choosing the entry in the Finder's menu does.
perform() {
    within 60 osascript -l JavaScript - "$@" <<'EOF' 2>&1
ObjC.import('AppKit');
function run(argv) {
    var board = $.NSPasteboard.pasteboardWithUniqueName;
    board.clearContents;
    var urls = $.NSMutableArray.alloc.init, paths = $.NSMutableArray.alloc.init;
    for (var i = 1; i < argv.length; i++) { urls.addObject($.NSURL.fileURLWithPath(argv[i])); paths.addObject($(argv[i])); }
    board.writeObjects(urls);
    board.setPropertyListForType(paths, 'NSFilenamesPboardType');
    var done = $.NSPerformService($(argv[0]), board);
    // The request is handed over asynchronously; leaving at once would drop it.
    $.NSRunLoop.currentRunLoop.runUntilDate($.NSDate.dateWithTimeIntervalSinceNow(4));
    return done ? 'performed' : 'refused';
}
EOF
}

# What opens a file when it is double-clicked, and what kind of file the system takes it for.
opener() {
    within 30 osascript -l JavaScript - "$1" <<'EOF' 2>&1
ObjC.import('AppKit');
function run(argv) {
    var app = $.NSWorkspace.sharedWorkspace.URLForApplicationToOpenURL($.NSURL.fileURLWithPath(argv[0]));
    return app.isNil() ? 'nothing' : app.path.js;
}
EOF
}
kind() {
    within 30 osascript -l JavaScript - "$1" <<'EOF' 2>&1
ObjC.import('AppKit');
function run(argv) {
    var value = Ref();
    $.NSURL.fileURLWithPath(argv[0]).getResourceValueForKeyError(value, $.NSURLTypeIdentifierKey, null);
    return value[0].js;
}
EOF
}
listed() { "$PBS" -dump_pboard 2>&1 | grep -q "default = \"$1\""; }
unlisted() { ! listed "$1"; }
opens_with_aesx() { [ "$(opener "$WORK/probe.aesx")" = "$APP" ]; }
opens_with_nothing() { [ "$(opener "$WORK/probe.aesx")" != "$APP" ]; }
verified() { AESX_PASSWORD="$PW" "$AESX" verify "$1" -q >/dev/null 2>&1; }

sw_vers | tr '\n' ' '; echo
printf 'x' > probe.aesx

section "Registering"
REGISTERED=1
if "$AESX" register > register.out 2> register.err; then pass "aesx register"; else flunk "aesx register: $(cat register.err)"; fi
cat register.out
grep -q "$ENCRYPT" register.out && grep -q "$DECRYPT" register.out && pass "it says what it added" || flunk "the summary does not name the entries"
[ -d "$SERVICES/$ENCRYPT.workflow" ] && [ -d "$SERVICES/$DECRYPT.workflow" ] && [ -d "$APP" ] && pass "two services and the application are there" || flunk "something was not written: $(ls "$SERVICES" "$APPLICATIONS" 2>&1 | tr '\n' ' ')"
lint=0
for file in "$SERVICES/$ENCRYPT.workflow/Contents/Info.plist" "$SERVICES/$ENCRYPT.workflow/Contents/document.wflow" "$SERVICES/$DECRYPT.workflow/Contents/Info.plist" "$SERVICES/$DECRYPT.workflow/Contents/document.wflow" "$APP/Contents/Info.plist"; do
    plutil -lint "$file" >/dev/null 2>&1 || { lint=1; echo "    $(plutil -lint "$file" 2>&1)"; }
done
[ "$lint" = 0 ] && pass "every property list is well formed" || flunk "a property list is not well formed"
codesign --verify --deep --strict "$APP" 2> codesign.err && pass "the application's signature is valid" || flunk "codesign: $(cat codesign.err)"
"$PBS" -read_bundle "$SERVICES/$ENCRYPT.workflow" 2>&1 | grep -q "default = \"$ENCRYPT\"" && pass "macOS reads the bundle as a service" || flunk "pbs does not read the bundle as a service: $("$PBS" -read_bundle "$SERVICES/$ENCRYPT.workflow" 2>&1 | head -5 | tr '\n' ' ')"
until_ 30 listed "$ENCRYPT" && until_ 30 listed "$DECRYPT" && pass "both services are known to the system" || flunk "the services are not listed by pbs"
until_ 30 opens_with_aesx && pass ".aesx files open with the application" || flunk ".aesx files open with: $(opener "$WORK/probe.aesx")"
[ "$(kind "$WORK/probe.aesx")" = "io.github.yohasacura.aesx.encrypted-file" ] && pass ".aesx files have a type of their own" || flunk "the type of an .aesx file is $(kind "$WORK/probe.aesx")"
"$AESX" register > again.out 2> again.err && codesign --verify --deep --strict "$APP" 2>/dev/null && pass "registering again replaces what is there" || flunk "registering twice: $(cat again.err)"

section "The right-click entries, asked for the way the Finder asks"
printf 'one' > one.txt; printf 'two' > "два 🔐.txt"; mkdir -p photos/inner; printf 'a' > photos/a.txt; printf 'b' > photos/inner/b.txt
done_=$(perform "$ENCRYPT" "$WORK/one.txt" "$WORK/два 🔐.txt" "$WORK/photos")
[ "$done_" = "performed" ] && pass "the system accepts \"$ENCRYPT\" for two files and a folder" || flunk "the service was not performed: $done_"
if answer 60 "" "" "Encrypt 3 items" "Password:"; then shot dialog-password; fi
if answer 60 "$PW" OK "Encrypt 3 items" "Password:"; then
    pass "one dialog asks for the password of all three"
    answer 60 "$PW" OK "Encrypt 3 items" "once more" && pass "and once more, to catch a typo" || flunk "no second dialog: $LOOKED"
else
    flunk "no password dialog appeared: $LOOKED"
fi
until_ 60 exists one.txt.aesx && until_ 60 exists "два 🔐.txt.aesx" && until_ 60 exists photos.folder.aesx && until_ 60 idle \
    && pass "the three encrypted files are there" || flunk "missing output: $(ls | tr '\n' ' ')"
verified one.txt.aesx && verified "два 🔐.txt.aesx" && verified photos.folder.aesx && pass "they open with the password that was typed" || flunk "an output does not verify"
ls | grep -q '\.part$' && flunk "a .part file was left" || pass "nothing unfinished was left"

rm -rf one.txt photos
done_=$(perform "$DECRYPT" "$WORK/one.txt.aesx" "$WORK/photos.folder.aesx")
[ "$done_" = "performed" ] && pass "the system accepts \"$DECRYPT\" for two .aesx files" || flunk "the service was not performed: $done_"
answer 60 "$PW" OK "Decrypt 2 items" "Password:" && pass "decrypting asks once" || flunk "no password dialog: $LOOKED"
until_ 60 exists one.txt && until_ 60 exists photos/inner/b.txt && until_ 60 idle
[ "$(cat one.txt 2>/dev/null)" = "one" ] && [ "$(cat photos/inner/b.txt 2>/dev/null)" = "b" ] && pass "the file and the folder are back" || flunk "not restored: $(ls | tr '\n' ' ')"

rm -f "два 🔐.txt"
perform "$DECRYPT" "$WORK/два 🔐.txt.aesx" >/dev/null
if answer 60 "not the password" OK "Decrypt два 🔐.txt.aesx" "Password:"; then
    if answer 60 "" "" "wrong password"; then shot dialog-wrong-password; fi
    if answer 60 "" OK "wrong password"; then pass "a wrong password is reported in a dialog"; else flunk "no report of the wrong password: $LOOKED"; fi
else
    flunk "no password dialog: $LOOKED"
fi
until_ 30 idle
[ ! -e "два 🔐.txt" ] && ! ls | grep -q '\.part$' && pass "and nothing was written" || flunk "something was written: $(ls | tr '\n' ' ')"

printf 'c' > cancel.txt
perform "$ENCRYPT" "$WORK/cancel.txt" >/dev/null
if answer 60 "$PW" OK "Encrypt cancel.txt" "Password:" && answer 60 "$PW with a typo" OK "Encrypt cancel.txt" "once more"; then
    answer 60 "" Cancel "were not the same" "Password:" && pass "passwords that differ are asked for again; Cancel ends it" || flunk "no dialog after the mismatch: $LOOKED"
else
    flunk "no password dialog: $LOOKED"
fi
until_ 30 idle
[ ! -e cancel.txt.aesx ] && pass "cancelling writes nothing" || flunk "cancel.txt.aesx exists"

section "Double-click and drop"
printf 'note' > note.txt
AESX_PASSWORD="$PW" "$AESX" encrypt note.txt --delete --iterations 1000 -q >/dev/null 2>&1
within 30 open "$WORK/note.txt.aesx" && pass "the system opens an .aesx file" || flunk "open failed"
answer 60 "$PW" OK "Decrypt note.txt.aesx" "Password:" && pass "which asks for its password" || flunk "no password dialog after opening: $LOOKED"
until_ 60 exists note.txt && until_ 60 idle
[ "$(cat note.txt 2>/dev/null)" = "note" ] && pass "and decrypts it" || flunk "note.txt was not restored"

printf 'd' > drop.txt; mkdir dropped; printf 'e' > dropped/e.txt
rm -f note.txt
within 30 open -a "$APP" "$WORK/drop.txt" "$WORK/dropped" "$WORK/note.txt.aesx" && pass "the application takes a file, a folder and an .aesx file together" || flunk "open -a failed"
if answer 60 "$PW" OK "Encrypt and decrypt 3 items" "Password:" && answer 60 "$PW" OK "Encrypt and decrypt 3 items" "once more"; then
    pass "one question for all of it"
else
    flunk "no password dialog after the drop: $LOOKED"
fi
until_ 60 exists drop.txt.aesx && until_ 60 exists dropped.folder.aesx && until_ 60 exists note.txt && until_ 60 idle \
    && pass "each was encrypted or decrypted by its name" || flunk "after the drop: $(ls | tr '\n' ' ')"
until_ 30 left && pass "the application leaves by itself when it is done" || flunk "the application is still running: $(pgrep -lf "$APP" | tr '\n' ' ')"

# macOS hands over a file that carries its "downloaded from the internet" mark in an event of its
# own, apart from the rest of the selection. Neither part may get lost on the way; how many
# times the password is asked for is up to macOS (twice, when the parts arrive separately).
printf 'p' > mixed-plain.txt; printf 'q' > mixed-downloaded.txt
xattr -w com.apple.quarantine "0081;$(printf '%x' "$(date +%s)");Safari;" mixed-downloaded.txt
within 30 open -a "$APP" "$WORK/mixed-plain.txt" "$WORK/mixed-downloaded.txt"
deadline=$(( $(date +%s) + 120 ))
while { [ ! -e mixed-plain.txt.aesx ] || [ ! -e mixed-downloaded.txt.aesx ]; } && [ "$(date +%s)" -lt "$deadline" ]; do
    answer 2 "$PW" OK "Encrypt " >/dev/null 2>&1
done
[ -e mixed-plain.txt.aesx ] && [ -e mixed-downloaded.txt.aesx ] && until_ 60 idle \
    && pass "a drop that macOS delivers in two parts is encrypted in full" || flunk "part of the drop was lost: $(ls | grep mixed | tr '\n' ' ')"
until_ 30 left

# Opened by itself, the application offers a file chooser. A chooser is steered from the keyboard
# (Command-Shift-G takes a path), which only works while it has the keyboard, so this one may
# give up instead of failing.
printf 'chosen' > chosen.txt
within 30 open "$APP"
sleep 3
shot application-file-chooser
within 20 osascript -e 'tell application "System Events" to keystroke "g" using {command down, shift down}' >/dev/null 2>&1
sleep 2
typed "$WORK/chosen.txt"; sleep 1; key 36; sleep 2; key 36
if answer 20 "$PW" OK "Encrypt chosen.txt" "Password:" && answer 60 "$PW" OK "Encrypt chosen.txt" "once more" && until_ 60 exists chosen.txt.aesx; then
    pass "opened by itself, the application lets a file be chosen and encrypts it"
else
    key 53; sleep 1; key 53
    skip "the application's file chooser could not be steered from here"
fi
until_ 30 idle
until_ 30 left || pkill -f "$APP/Contents/MacOS"

section "In the Finder itself"
# Opening the Finder's own menu from the keyboard is new in macOS 15, and a menu is a fragile
# thing to steer blind. What can be checked without it has been checked above; this adds the
# look of the real menu, and a picture of it.
printf 'm' > menu.txt
within 30 osascript - "$WORK" <<'EOF' >/dev/null 2>&1
on run argv
    tell application "Finder"
        activate
        set dir to (POSIX file (item 1 of argv) as alias)
        set w to make new Finder window to dir
        set bounds of w to {40, 40, 900, 620}
        set current view of w to list view
        select item "menu.txt" of folder dir
    end tell
end run
EOF
sleep 2
within 20 osascript -e 'tell application "System Events" to tell process "Finder" to key code 36 using control down' >/dev/null 2>&1
sleep 2
shot finder-menu-file
typed "$ENCRYPT"; sleep 1; key 36
if answer 20 "$PW" OK "Encrypt menu.txt" "Password:" && answer 60 "$PW" OK "Encrypt menu.txt" "once more" && until_ 60 exists menu.txt.aesx; then
    pass "chosen from the Finder's right-click menu, the entry encrypts the selected file"
else
    key 53; key 53
    skip "the Finder's menu could not be steered from here (see the pictures, if any were taken)"
fi
until_ 30 idle
within 30 osascript - "$WORK" <<'EOF' >/dev/null 2>&1
on run argv
    tell application "Finder"
        activate
        set dir to (POSIX file (item 1 of argv) as alias)
        select item "one.txt.aesx" of folder dir
    end tell
end run
EOF
sleep 2
within 20 osascript -e 'tell application "System Events" to tell process "Finder" to key code 36 using control down' >/dev/null 2>&1
sleep 2
shot finder-menu-aesx-file
key 53; sleep 1
within 20 osascript -e 'tell application "Finder" to close every Finder window' >/dev/null 2>&1

section "When aesx is no longer where it was"
mv "$AESX" "$AESX.moved"
perform "$ENCRYPT" "$WORK/one.txt" >/dev/null
answer 60 "" OK "aesx is no longer at" "aesx register" && pass "the entry says so, and what to do" || flunk "no dialog from the entry: $LOOKED"
within 30 open -a "$APP" "$WORK/one.txt"
answer 60 "" OK "aesx is no longer at" "aesx register" && pass "so does the application" || flunk "no dialog from the application: $LOOKED"
mv "$AESX.moved" "$AESX"

section "Unregistering"
if "$AESX" unregister > unregister.out 2> unregister.err; then REGISTERED=0; pass "aesx unregister"; else flunk "aesx unregister: $(cat unregister.err)"; fi
[ ! -e "$SERVICES/$ENCRYPT.workflow" ] && [ ! -e "$SERVICES/$DECRYPT.workflow" ] && [ ! -e "$APP" ] && pass "the services and the application are gone" || flunk "something is left: $(ls "$SERVICES" "$APPLICATIONS" 2>&1 | tr '\n' ' ')"
if [ "$HAD_APPLICATIONS" = 0 ]; then
    [ ! -e "$APPLICATIONS" ] && pass "so is the Applications folder that registering had to create" || flunk "$APPLICATIONS is left: $(ls -A "$APPLICATIONS" | tr '\n' ' ')"
fi
until_ 30 unlisted "$ENCRYPT" && until_ 30 unlisted "$DECRYPT" && pass "the system no longer knows the services" || flunk "pbs still lists a service"
until_ 30 opens_with_nothing && pass ".aesx files no longer open with it" || flunk ".aesx files still open with $(opener "$WORK/probe.aesx")"
"$AESX" unregister >/dev/null 2>&1 && pass "unregistering once more is harmless" || flunk "a second unregister failed"
[ -s unregister.err ] && flunk "unregister complained: $(cat unregister.err)" || pass "nothing of somebody else's was in the way"

printf '\n%s\n' "$([ "$failures" = "0" ] && echo "All Finder scenarios passed." || echo "$failures Finder scenario(s) FAILED.")"
exit "$failures"
