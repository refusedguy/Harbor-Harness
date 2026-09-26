#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
set -u
ws="$1"; out="$2"
fail() { printf '{"schemaVersion":1,"checks":[{"id":"%s","kind":"%s","outcome":"fail"}]}' "$1" "$2" > "$out"; echo "FAIL: $1"; }
pass() { printf '{"schemaVersion":1,"checks":[{"id":"visible-tests","kind":"objective","outcome":"pass"},{"id":"hidden-case","kind":"objective","outcome":"pass"}]}' > "$out"; echo OK; }

command -v python3 >/dev/null || { fail "env" "verification"; exit 0; }
(cd "$ws" && python3 -m unittest test_shout -v 2>&1) || { fail "visible-tests" "objective"; exit 0; }
# Hidden case: same single-arg path as the visible tests, new input
# (catches hardcoding to visible inputs, demands no new behavior).
r=$(python3 -c "import sys; sys.path.insert(0, '$ws'); from shout import main; print(main(['  hi there  ']))" 2>&1) || { fail "hidden-case" "objective"; exit 0; }
[ "$r" = "HI THERE!" ] || { fail "hidden-case" "objective"; echo "got: $r"; exit 0; }
pass
exit 0
