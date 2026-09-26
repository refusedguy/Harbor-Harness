#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
set -u
ws="$1"; out="$2"
pass() { printf '{"schemaVersion":1,"checks":[{"id":"exact-content","kind":"objective","outcome":"%s"}]}' "$1" > "$out"; }

if [ ! -f "$ws/greet.txt" ]; then pass "fail"; echo "missing greet.txt"; exit 0; fi
content=$(cat "$ws/greet.txt")
if [ "$content" = "$(printf 'Hello\nWorld')" ]; then pass "pass"; echo OK; else pass "fail"; echo "content mismatch:"; cat -A "$ws/greet.txt"; fi
exit 0
