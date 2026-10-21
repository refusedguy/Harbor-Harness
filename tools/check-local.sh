#!/usr/bin/env bash
# check-local.sh -- the tree gates, on this machine, in one command.
#
# WHAT THIS IS
#
# Every check of this repository that needs nothing but the tree -- no
# build, no SDK, no network -- runs with ONE command, here and in CI:
#
#   tools/bin/harbor-gates-linux-x64 all
#
# That command runs the gates that are compiled into the binary. The gates
# that are still python scripts are run here too, so that the local command
# covers the same gates the CI command covers; the list of them comes from
# the binary itself (`all --pending`), so there is one list and not two.
#
# WHY THE LIST LIVES IN THE BINARY
#
# A second list in this script is a second thing to keep in step with the
# first. The list of the tree gates is in tools/gates/all/gates.go, and the
# binary prints the pending ones out of it, so the two can never disagree
# about which gate is still python.
#
# WHAT THIS NEEDS
#
#   the compiled binary (tools/bin/), bash, and -- for the gates that are
#   still python -- python3. No dotnet, no SDK, no network, no build.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
cd "$REPO"

# The binary of this machine's architecture. The repository ships one binary
# per architecture, and the wrong one cannot run here at all.
case "$(uname -m)" in
    aarch64 | arm64) BINARY_NAME="harbor-gates-linux-arm64" ;;
    *) BINARY_NAME="harbor-gates-linux-x64" ;;
esac
GATES="$HERE/bin/$BINARY_NAME"
if [ ! -x "$GATES" ] && [ -x "$HERE/bin/harbor-gates-linux-x64" ]; then
    GATES="$HERE/bin/harbor-gates-linux-x64"
fi
if [ ! -x "$GATES" ]; then
    echo "check-local.sh: no compiled gate binary in $HERE/bin" >&2
    echo "check-local.sh: build it with tools/build-gates.sh" >&2
    exit 2
fi

overall=0

# 1. The compiled gates: one command, and nothing else.
echo "== the compiled gates =="
"$GATES" all
rc=$?
if [ "$rc" -ne 0 ]; then
    overall=$rc
fi

# 2. The gates that are still python. The binary prints them as
# "<name> TAB <command>", one per line.
echo
echo "== the gates that are still python =="
pending=0
while IFS=$'\t' read -r name command; do
    [ -n "$name" ] || continue
    pending=$((pending + 1))
    echo
    echo "-- $name"
    read -r -a argv <<<"$command"
    "${argv[@]}"
    rc=$?
    if [ "$rc" -ne 0 ]; then
        if [ "$overall" -eq 0 ]; then
            overall=$rc
        fi
    fi
done < <("$GATES" all --pending 2>/dev/null)

if [ "$pending" -eq 0 ]; then
    echo "(none: every tree gate is compiled)"
fi

echo
if [ "$overall" -eq 0 ]; then
    echo "check-local.sh: the tree gates are green"
else
    echo "check-local.sh: the tree gates are NOT green (exit $overall)"
fi
exit "$overall"
