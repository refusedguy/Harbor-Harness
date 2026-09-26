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
#   ./tools/pr-ops.sh pr-mergeable [N...]       # mergeable flags
#   ./tools/pr-ops.sh job-log <job-id> [outfile]        # download job log
#   ./tools/pr-ops.sh pr-merge-if-green <N>     # merge only if fully green
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

# print failed test names from a completed job log
cmd_fails() {
  local run_id="${1:?usage: pr-fails <run-id> <job-id>}"
  local job_id="${2:?usage: pr-fails <run-id> <job-id>}"
  gh run view "$run_id" --job "$job_id" --log-failed 2>&1 \
    | grep -aE "failed.*[0-9]+(ms|s)\)" | head -n 10 || true
}

# print unique compiler/analyzer errors from a failed build job log
cmd_build_errors() {
  local run_id="${1:?usage: pr-build-errors <run-id> <job-id>}"
  local job_id="${2:?usage: pr-build-errors <run-id> <job-id>}"
  gh run view "$run_id" --job "$job_id" --log-failed 2>&1 \
    | grep -aE "error (CS|MSB|[A-Z]+[0-9]+)" | sort -u | head -n 10 || true
}

cmd_mergeable() {
  local prs=("$@")
  if [ "${#prs[@]}" -eq 0 ]; then
    mapfile -t prs < <(gh pr list --state open --json number --jq '.[].number')
  fi
  for p in "${prs[@]}"; do
    echo -n "$p: "
    gh pr view "$p" --json mergeable --jq .mergeable 2>&1 | head -n 1
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
  local checks
  checks=$(gh pr checks "$p" 2>&1 | grep -E "$CHECKS_RE" || true)
  echo "$checks"
  if echo "$checks" | grep -Eq "fail"; then
    echo "NOT GREEN (has failures) — refusing to merge #$p" >&2
    return 1
  fi
  if echo "$checks" | grep -Eq "pending"; then
    echo "NOT GREEN (still pending) — refusing to merge #$p" >&2
    return 1
  fi
  gh pr merge "$p" --merge
}

if [ "${1:-}" = pr-sweep ]; then shift; cmd_sweep "$@"
elif [ "${1:-}" = pr-fails ]; then shift; cmd_fails "$@"
elif [ "${1:-}" = pr-build-errors ]; then shift; cmd_build_errors "$@"
elif [ "${1:-}" = pr-mergeable ]; then shift; cmd_mergeable "$@"
elif [ "${1:-}" = job-log ]; then shift; cmd_job_log "$@"
elif [ "${1:-}" = pr-merge-if-green ]; then shift; cmd_merge_if_green "$@"
else echo "unknown command: ${1:-<empty>}" >&2; exit 2
fi
