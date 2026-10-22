#!/usr/bin/env bash
# equivalence.sh -- run a python original and a compiled gate on the SAME tree
# and compare their output, byte for byte.
#
#   equivalence.sh <python-original-in-git>[,<sibling-in-git>...] <python-args...> -- <gate> <gate-args...>
#
# The python original is taken from git (the tree it was deleted from), and it
# is copied into `tools/` under a temporary name so that its REPO_ROOT -- the
# parent of `tools/` -- is the checkout the gate runs in, and so that the
# sibling modules it imports are importable. A gate whose python original
# imported a sibling module that has moved too names that sibling after a
# comma.
#
# The exit codes are compared too: the gate must have the same verdict.
set -u

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 2

PY_PATHS="$1"; shift
PY_ARGS=()
while [ $# -gt 0 ] && [ "$1" != "--" ]; do PY_ARGS+=("$1"); shift; done
[ "${1:-}" = "--" ] && shift
GATE="$1"; shift
GATE_ARGS=("$@")

TMP="$(mktemp -d)"
TMP_FILES=()
cleanup() { rm -rf "$TMP"; rm -f "${TMP_FILES[@]}"; }
trap cleanup EXIT

IFS=',' read -r -a PATHS <<< "$PY_PATHS"
SCRIPT=""
INDEX=0
for entry in "${PATHS[@]}"; do
    name="$(basename "${entry#*:}")"
    if [ "$INDEX" = "0" ]; then
        target="$REPO/tools/_equiv_tmp_$name"
    else
        # A sibling module keeps its own name: the original imports it by
        # that name, and sys.path[0] is the directory of the script.
        target="$REPO/tools/$name"
    fi
    git show "$entry" > "$target" || exit 2
    TMP_FILES+=("$target")
    if [ -z "$SCRIPT" ]; then
        SCRIPT="$target"
    fi
    INDEX=$((INDEX + 1))
done

python3 "$SCRIPT" "${PY_ARGS[@]}" > "$TMP/py.out" 2> "$TMP/py.err"
PY_CODE=$?

"$REPO/tools/bin/harbor-gates-linux-x64" "$GATE" "${GATE_ARGS[@]}" > "$TMP/go.out" 2> "$TMP/go.err"
GO_CODE=$?

STATUS=0
if diff -u "$TMP/py.out" "$TMP/go.out" > "$TMP/out.diff"; then
    echo "stdout: IDENTICAL"
else
    echo "stdout: DIFFERS"; cat "$TMP/out.diff"; STATUS=1
fi
if diff -u "$TMP/py.err" "$TMP/go.err" > "$TMP/err.diff"; then
    echo "stderr: IDENTICAL"
else
    echo "stderr: DIFFERS"; cat "$TMP/err.diff"; STATUS=1
fi
if [ "$PY_CODE" = "$GO_CODE" ]; then
    echo "exit code: $PY_CODE (same)"
else
    echo "exit code: python=$PY_CODE go=$GO_CODE (DIFFERS)"; STATUS=1
fi
exit $STATUS
