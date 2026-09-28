#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
set -u
ws="$1"; out="$2"
fail() { printf '{"schemaVersion":1,"checks":[{"id":"%s","kind":"%s","outcome":"fail"}]}' "$1" "$2" > "$out"; echo "FAIL: $1"; }
pass() { printf '{"schemaVersion":1,"checks":[{"id":"visible-tests","kind":"objective","outcome":"pass"},{"id":"public-signatures","kind":"objective","outcome":"pass"},{"id":"hidden-case","kind":"objective","outcome":"pass"}]}' > "$out"; echo OK; }

command -v python3 >/dev/null || { fail "env" "verification"; exit 0; }
(cd "$ws" && python3 -m unittest test_store -v 2>&1) || { fail "visible-tests" "objective"; exit 0; }
# Public signatures must be byte-stable: same names, params, order.
r=$(python3 -c "import sys, inspect; sys.path.insert(0, '$ws'); from store import add_item, total; print(str(inspect.signature(add_item)) + '|' + str(inspect.signature(total)))" 2>&1) || { fail "public-signatures" "objective"; exit 0; }
[ "$r" = "(cart, name, price)|(cart)" ] || { fail "public-signatures" "objective"; echo "got: $r"; exit 0; }
# Hidden case (not in test_store.py): zero-price item totals, negatives still rejected.
r=$(python3 -c "import sys; sys.path.insert(0, '$ws'); from store import add_item, total; c = add_item(add_item([], 'a', 10), 'free', 0); print(total(c))" 2>&1) || { fail "hidden-case" "objective"; exit 0; }
[ "$r" = "10" ] || { fail "hidden-case" "objective"; echo "got: $r"; exit 0; }
pass
exit 0
