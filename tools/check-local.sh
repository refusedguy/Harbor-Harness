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
# WHY THE PYTHON GATES RUN A FEW AT A TIME
#
# They are independent, and two of them are slow: the citation gate reads
# every normative document, and the merge-loss gate walks the git history.
# Run one after another they cost minutes; run a few at a time they cost
# about as much as the slowest of them. The output of each gate is
# collected separately and printed in the order of the list, so a parallel
# run reads the same as a sequential one. This part disappears by itself
# when the last python gate is ported.
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

# How many python gates run at the same time. Four is half of the cores of
# a developer machine, and the two slow gates are not CPU-bound.
PARALLEL=4

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

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

PENDING="$WORK/pending"
"$GATES" all --pending >"$PENDING" 2>/dev/null

# run_pending runs one gate into its own file and leaves its exit code and
# its time beside it. The output is printed later, in the order of the
# list, so that a parallel run reads the same as a sequential one.
run_pending() {
    local index="$1" command="$2"
    local argv started ended
    read -r -a argv <<<"$command"
    started=$(date +%s%N)
    "${argv[@]}" >"$WORK/$index.out" 2>&1
    echo "$?" >"$WORK/$index.rc"
    ended=$(date +%s%N)
    echo "$(( (ended - started) / 1000000 ))" >"$WORK/$index.ms"
}

index=0
running=0
oldest=1
pids=()
names=()
while IFS=$'\t' read -r name command; do
    [ -n "$name" ] || continue
    index=$((index + 1))
    names[$index]="$name"
    run_pending "$index" "$command" &
    pids[$index]=$!
    running=$((running + 1))
    if [ "$running" -ge "$PARALLEL" ]; then
        # Wait for the oldest of the running gates and free its slot. The
        # exit code is read from the file the gate wrote, so nothing here
        # depends on WHICH gate finished first.
        wait "${pids[$oldest]}"
        running=$((running - 1))
        oldest=$((oldest + 1))
    fi
done <"$PENDING"

# The gates that are still running.
for ((i = oldest; i <= index; i++)); do
    wait "${pids[$i]}" 2>/dev/null
done

# The report: every gate, in the order of the list, with its time and its
# output.
for ((i = 1; i <= index; i++)); do
    elapsed=""
    if [ -f "$WORK/$i.ms" ]; then
        elapsed="$(cat "$WORK/$i.ms") ms"
    fi
    echo
    echo "-- ${names[$i]}${elapsed:+ ($elapsed)}"
    if [ -f "$WORK/$i.out" ]; then
        cat "$WORK/$i.out"
    fi
    if [ -f "$WORK/$i.rc" ]; then
        rc="$(cat "$WORK/$i.rc")"
        if [ "$rc" -ne 0 ] && [ "$overall" -eq 0 ]; then
            overall=$rc
        fi
    fi
done

if [ "$index" -eq 0 ]; then
    echo "(none: every tree gate is compiled)"
fi

echo
if [ "$overall" -eq 0 ]; then
    echo "check-local.sh: the tree gates are green"
else
    echo "check-local.sh: the tree gates are NOT green (exit $overall)"
fi
exit "$overall"
