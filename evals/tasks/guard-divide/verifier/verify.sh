#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
set -u
ws="$1"; out="$2"
fail() { printf '{"schemaVersion":1,"checks":[{"id":"%s","kind":"%s","outcome":"fail"}]}' "$1" "$2" > "$out"; echo "FAIL: $1"; }
pass() { printf '{"schemaVersion":1,"checks":[{"id":"normal-division","kind":"objective","outcome":"pass"},{"id":"zero-guard","kind":"objective","outcome":"pass"}]}' > "$out"; echo OK; }

command -v python3 >/dev/null || { fail "env" "verification"; exit 0; }
r=$(python3 -c "import sys; sys.path.insert(0, '$ws'); from calc import divide; print(divide(7, 2))" 2>&1) || { fail "normal-division" "objective"; exit 0; }
[ "$r" = "3.5" ] || { fail "normal-division" "objective"; exit 0; }
r=$(python3 -c "import sys; sys.path.insert(0, '$ws'); from calc import divide; print(divide(1, 0))" 2>&1) || { fail "zero-guard" "objective"; exit 0; }
[ "$r" = "0" ] || [ "$r" = "0.0" ] || { fail "zero-guard" "objective"; exit 0; }
pass
exit 0
