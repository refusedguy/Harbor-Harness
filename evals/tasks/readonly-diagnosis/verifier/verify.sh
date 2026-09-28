#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
# Read-only diagnosis: source files must be untouched (enforced by the
# manifest constraint checker); here we only judge the diagnosis artifact.
set -u
ws="$1"; out="$2"
fail() { printf '{"schemaVersion":1,"checks":[{"id":"%s","kind":"%s","outcome":"fail"}]}' "$1" "$2" > "$out"; echo "FAIL: $1"; }
pass() { printf '{"schemaVersion":1,"checks":[{"id":"diagnosis-present","kind":"objective","outcome":"pass"},{"id":"names-culprit","kind":"objective","outcome":"pass"}]}' > "$out"; echo OK; }

[ -s "$ws/DIAGNOSIS.md" ] || { fail "diagnosis-present" "objective"; exit 0; }
grep -qi "apply_discount" "$ws/DIAGNOSIS.md" || { fail "names-culprit" "objective"; exit 0; }
pass
exit 0
