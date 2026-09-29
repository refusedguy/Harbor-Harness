#!/usr/bin/env bash
# tools/pr-ops.sh — PR/CI operations helpers for Harbor-Harness.
#
# One-liners for the repetitive `gh` incantations used during PR triage:
# status sweeps, failure extraction, log downloads, mergeability.
# Read-only by default; only `pr-merge-if-green` mutates (merge only).
#
# Requires: gh CLI authenticated (gh auth login).
# Usage:
#   ./tools/pr-ops.sh pr-sweep                  # all open PRs + compact checks
#   ./tools/pr-ops.sh pr-sweep 130 132 135      # specific PRs
#   ./tools/pr-ops.sh pr-fails <run-id> <job-id>        # failed test names
#   ./tools/pr-ops.sh pr-build-errors <run-id> <job-id> # CS/MSB/analyzer errors
#   ./tools/pr-ops.sh pr-mergeable [N...]       # per-PR verdict: READY / WAIT / FAIL / BLOCKED
#   ./tools/pr-ops.sh log <run-id> <job-id>              # full failed-step log to stdout (ANSI stripped)
#   ./tools/pr-ops.sh job-log <job-id> [outfile]        # download job log
#   ./tools/pr-ops.sh pr-merge-if-green <N>     # merge only if fully green and not conflicting
set -euo pipefail

REPO="refusedguy/Harbor-Harness"
CHECKS_RE='^(test|build|bench|coverage|test-os|demo-gifs)'

cmd_sweep() {
  local prs=("$@")
  if [ "${#prs[@]}" -eq 0 ]; then
    mapfile -t prs < <(gh pr list --state open --json number --jq '.[].number')
  fi
  for p in "${prs[@]}"; do
    local state
    state=$(gh pr view "$p" --json state --jq .state 2>&1 | head -n 1)
    echo "== $p == $state"
    gh pr checks "$p" 2>&1 | grep -E "$CHECKS_RE" | head -n 9 || true
  done
}

# strip ANSI color codes; logs are full of them and they garble grep output
strip_ansi() {
  sed -e 's/\x1b\[[0-9;]*[a-zA-Z]//g'
}

# print failed test names from a completed job log
cmd_fails() {
  local run_id="${1:?usage: pr-fails <run-id> <job-id>}"
  local job_id="${2:?usage: pr-fails <run-id> <job-id>}"
  gh run view "$run_id" --job "$job_id" --log-failed 2>&1 \
    | strip_ansi | grep -aE "failed.*[0-9]+(ms|s)\)" | head -n 10 || true
}

# print unique compiler/analyzer errors from a failed build job log
cmd_build_errors() {
  local run_id="${1:?usage: pr-build-errors <run-id> <job-id>}"
  local job_id="${2:?usage: pr-build-errors <run-id> <job-id>}"
  gh run view "$run_id" --job "$job_id" --log-failed 2>&1 \
    | strip_ansi | grep -aE "error (CS|MSB|[A-Z]+[0-9]+)" | sort -u | head -n 10 || true
}

# dump the whole failed-step log of a job to stdout (no truncation, ANSI stripped)
cmd_log() {
  local run_id="${1:?usage: log <run-id> <job-id>}"
  local job_id="${2:?usage: log <run-id> <job-id>}"
  gh run view "$run_id" --job "$job_id" --log-failed 2>&1 | strip_ansi
}

cmd_mergeable() {
  local prs=("$@")
  if [ "${#prs[@]}" -eq 0 ]; then
    mapfile -t prs < <(gh pr list --state open --json number --jq '.[].number' | sort -rn)
  fi
  # Count check outcomes by the tab-delimited field, not by substring: a job
  # whose NAME contains "fail" is not a failing job.
  for p in "${prs[@]}"; do
    local raw checks fails pends passes verdict
    raw=$(gh pr view "$p" --json mergeable --jq .mergeable 2>/dev/null | head -n 1)
    case "$raw" in
      UNKNOWN|UNSET|"") raw="PENDING" ;;
    esac
    checks=$(gh pr checks "$p" 2>&1 | grep -E "$CHECKS_RE" || true)
    fails=$(printf '%s\n' "$checks" | awk -F'\t' '$2=="fail"{n++} END{print n+0}')
    pends=$(printf '%s\n' "$checks" | awk -F'\t' '$2=="pending"{n++} END{print n+0}')
    passes=$(printf '%s\n' "$checks" | awk -F'\t' '$2=="pass"{n++} END{print n+0}')

    # GitHub returns UNKNOWN while it is still COMPUTING the merge state. That
    # is not a verdict and must never be reported as one -- it is the reason
    # this command was previously useless for pending PRs.
    verdict="WAIT"
    [ "$fails" -gt 0 ] && verdict="FAIL"
    if [ "$fails" -eq 0 ] && [ "$pends" -eq 0 ]; then
      if [ "$raw" = "MERGEABLE" ]; then verdict="READY"; else verdict="BLOCKED"; fi
    fi
    printf '%s: %s mergeable=%s pass=%s fail=%s pending=%s\n' \
      "$p" "$verdict" "$raw" "$passes" "$fails" "$pends"
  done
}

# download a job log via the API (works when --log-failed returns empty)
cmd_job_log() {
  local job_id="${1:?usage: job-log <job-id> [outfile]}"
  local out="${2:-/tmp/opencode/job-$job_id.log}"
  mkdir -p "$(dirname "$out")"
  curl -sL -H "Authorization: Bearer $(gh auth token)" \
    "https://api.github.com/repos/$REPO/actions/jobs/$job_id/logs" -o "$out"
  echo "$out ($(wc -c < "$out") bytes)"
}

# merge only when every required check is pass (bench required for src PRs,
# coverage-allowed for yml/evals-only PRs which skip test jobs)
cmd_merge_if_green() {
  local p="${1:?usage: pr-merge-if-green <N>}"
  local checks raw
  checks=$(gh pr checks "$p" 2>&1 | grep -E "$CHECKS_RE" || true)
  echo "$checks"
  if printf '%s\n' "$checks" | awk -F'\t' '$2=="fail"{n++} END{exit !n}'; then
    echo "NOT GREEN (has failures) — refusing to merge #$p" >&2
    return 1
  fi
  if printf '%s\n' "$checks" | awk -F'\t' '$2=="pending"{n++} END{exit !n}'; then
    echo "NOT GREEN (still pending) — refusing to merge #$p" >&2
    return 1
  fi
  # A green PR can still CONFLICT. Refuse rather than let gh try and fail.
  # UNKNOWN is not a conflict verdict: GitHub returns it while it is still
  # computing, so it must not block.
  raw=$(gh pr view "$p" --json mergeable --jq .mergeable 2>/dev/null | head -n 1)
  if [ "$raw" = "CONFLICTING" ]; then
    echo "GREEN but CONFLICTING — the author must rebase, refusing to merge #$p" >&2
    return 1
  fi
  gh pr merge "$p" --merge
}

if [ "${1:-}" = pr-sweep ]; then shift; cmd_sweep "$@"
elif [ "${1:-}" = pr-fails ]; then shift; cmd_fails "$@"
elif [ "${1:-}" = pr-build-errors ]; then shift; cmd_build_errors "$@"
elif [ "${1:-}" = pr-mergeable ]; then shift; cmd_mergeable "$@"
elif [ "${1:-}" = log ]; then shift; cmd_log "$@"
elif [ "${1:-}" = job-log ]; then shift; cmd_job_log "$@"
elif [ "${1:-}" = pr-merge-if-green ]; then shift; cmd_merge_if_green "$@"
else echo "unknown command: ${1:-<empty>}" >&2; exit 2
fi
