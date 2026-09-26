#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
set -u
ws="$1"; out="$2"
fail() { printf '{"schemaVersion":1,"checks":[{"id":"%s","kind":"%s","outcome":"fail"}]}' "$1" "$2" > "$out"; echo "FAIL: $1"; }
pass() { printf '{"schemaVersion":1,"checks":[{"id":"normal-sum","kind":"objective","outcome":"pass"},{"id":"negative-guard","kind":"objective","outcome":"pass"}]}' > "$out"; echo OK; }

command -v python3 >/dev/null || { fail "env" "verification"; exit 0; }
r=$(python3 -c "import sys; sys.path.insert(0, '$ws'); from shop import total; print(total([10, 20, 30]))" 2>&1) || { fail "normal-sum" "objective"; exit 0; }
[ "$r" = "60" ] || { fail "normal-sum" "objective"; exit 0; }
r=$(python3 -c "import sys; sys.path.insert(0, '$ws'); from shop import total; print(total([10, -5, 20, -30]))" 2>&1) || { fail "negative-guard" "objective"; exit 0; }
[ "$r" = "30" ] || { fail "negative-guard" "objective"; exit 0; }
r=$(python3 -c "import sys; sys.path.insert(0, '$ws'); from shop import total; print(total([]))" 2>&1) || { fail "normal-sum" "objective"; exit 0; }
[ "$r" = "0" ] || { fail "normal-sum" "objective"; exit 0; }
pass
exit 0
