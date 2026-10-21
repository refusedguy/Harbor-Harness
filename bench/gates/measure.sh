#!/usr/bin/env bash
# measure.sh -- time the gate suite, the way CI runs it.
#
# The number that matters is the SUM: the gates run one after another in one
# CI job, so what a language change buys is the sum, not the worst gate.
#
# Every gate is run RUNS times and the MINIMUM wall time is kept: this box
# is shared and its load is 18-26, so the minimum is the estimate that the
# least foreign work distorts. The peak RSS is the MAXIMUM over the runs,
# and it is the child's own /proc VmHWM, not ru_maxrss: on this host
# ru_maxrss carries a value the child never reached (see maxrss.c).
#
# The set is bench/gates/tools.tsv: one row per gate, with the arguments the
# workflow passes. The gates that judge an artifact the build produced are
# measured against an EMPTY directory of the right shape, so their number is
# the cost of walking the tree and finding nothing, not the cost of parsing
# a real artifact. The README says which artifact belongs to which row.
#
# usage:
#   TREE=/path/to/tree RUNS=5 ./measure.sh > gates.tsv
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
TREE="${TREE:-/mnt/projects/bench-tree}"
RUNS="${RUNS:-5}"
TOOLS="${TOOLS:-$HERE/tools.tsv}"
ONLY="${ONLY:-}"

MAXRSS="$HERE/maxrss"
if ! gcc -O2 -o "$MAXRSS" "$HERE/maxrss.c" 2>/dev/null; then
    echo "measure.sh: cannot build the measuring parent" >&2
    exit 2
fi

# The artifacts the gates judge, as EMPTY shapes: the TRX directory, the
# packed packages, the coverage report, the measurement directory, the AOT
# publish log and the analyzer's two files. A gate that reads one of them
# is measured on the walk, not on the parse.
mkdir -p "$TREE/TestResults" "$TREE/out/packages" "$TREE/Coverage" \
         "$TREE/meas" "$TREE/publish"
: > "$TREE/publish/aot-publish.log"
: > "$TREE/rt-warnings.log"
: > "$TREE/rt-coverage.txt"

cd "$TREE" || exit 2

printf 'gate\truns\tmin_wall_s\tmedian_wall_s\tmax_vmhwm_kb\texit\n'

total=0.0
while IFS=$'\t' read -r name cmd; do
    case "$name" in ''|'#'*) continue ;; esac
    if [ -n "$ONLY" ] && ! printf '%s' ",$ONLY," | grep -q ",$name,"; then
        continue
    fi
    if [ -z "$cmd" ]; then
        printf '%s\t%s\t-\t-\t-\t-\n' "$name" "$RUNS"
        continue
    fi

    walls=()
    peak=0
    rc=0
    for _ in $(seq 1 "$RUNS"); do
        # shellcheck disable=SC2086
        line=$("$MAXRSS" "$name" $cmd 2>/dev/null)
        rc="$(printf '%s' "$line" | cut -f6)"
        walls+=("$(printf '%s' "$line" | cut -f2)")
        v="$(printf '%s' "$line" | cut -f4)"
        if [ -n "$v" ] && [ "$v" -gt "$peak" ] 2>/dev/null; then peak="$v"; fi
    done
    min="$(printf '%s\n' "${walls[@]}" | sort -n | head -1)"
    med="$(printf '%s\n' "${walls[@]}" | sort -n | awk '{a[NR]=$1} END{if(NR%2==1) printf "%.3f", a[(NR+1)/2]; else printf "%.3f", (a[NR/2]+a[NR/2+1])/2}')"
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$RUNS" "$min" "$med" "$peak" "$rc"
    total="$(awk -v a="$total" -v b="$min" 'BEGIN{printf "%.3f", a+b}')"
done < "$TOOLS"

printf 'TOTAL\t%s\t%s\t-\t-\t-\n' "$RUNS" "$total"
