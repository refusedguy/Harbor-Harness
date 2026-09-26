#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
set -u
ws="$1"; out="$2"
fail() { printf '{"schemaVersion":1,"checks":[{"id":"%s","kind":"%s","outcome":"fail"}]}' "$1" "$2" > "$out"; echo "FAIL: $1"; }
pass() { printf '{"schemaVersion":1,"checks":[{"id":"manifest-sorted","kind":"objective","outcome":"pass"}]}' > "$out"; echo OK; }

[ -f "$ws/manifest.txt" ] || { fail "manifest-sorted" "objective"; echo "missing manifest.txt"; exit 0; }
[ "$(cat "$ws/manifest.txt")" = "$(printf 'apple.txt\nmango.txt\nzebra.txt')" ] || { fail "manifest-sorted" "objective"; echo "content mismatch:"; cat -A "$ws/manifest.txt"; exit 0; }
pass
exit 0
