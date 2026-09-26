#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
set -u
ws="$1"; out="$2"
fail() { printf '{"schemaVersion":1,"checks":[{"id":"%s","kind":"%s","outcome":"fail"}]}' "$1" "$2" > "$out"; echo "FAIL: $1"; }
pass() { printf '{"schemaVersion":1,"checks":[{"id":"all-files-done","kind":"objective","outcome":"pass"}]}' > "$out"; echo OK; }

for f in a.txt b.txt c.txt; do
  [ -f "$ws/$f" ] || { fail "all-files-done" "objective"; echo "missing $f"; exit 0; }
  [ "$(cat "$ws/$f")" = "DONE" ] || { fail "all-files-done" "objective"; echo "bad content in $f:"; cat -A "$ws/$f"; exit 0; }
done
pass
exit 0
