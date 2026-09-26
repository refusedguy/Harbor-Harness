#!/usr/bin/env bash
# verify.sh <workspace> <verificationOutput>
set -u
ws="$1"; out="$2"
fail() { printf '{"schemaVersion":1,"checks":[{"id":"%s","kind":"%s","outcome":"fail"}]}' "$1" "$2" > "$out"; echo "FAIL: $1"; }
pass() { printf '{"schemaVersion":1,"checks":[{"id":"port-value","kind":"objective","outcome":"pass"},{"id":"keys-intact","kind":"objective","outcome":"pass"}]}' > "$out"; echo OK; }

command -v python3 >/dev/null || { fail "env" "verification"; exit 0; }
[ -f "$ws/config.json" ] || { fail "port-value" "objective"; exit 0; }
r=$(python3 -c "import json; d=json.load(open('$ws/config.json')); print(d.get('port'), d.get('host'), d.get('debug'), sorted(d.keys()))" 2>&1) || { fail "port-value" "objective"; exit 0; }
[ "$r" = "9090 localhost False ['debug', 'host', 'port']" ] || { fail "port-value" "objective"; echo "got: $r"; exit 0; }
pass
exit 0
