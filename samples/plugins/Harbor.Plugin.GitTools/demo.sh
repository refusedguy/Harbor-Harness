#!/usr/bin/env bash
# 60-second demo for the GitTools showcase plugin (slice-2, #1434).
# Hermetic: no dotnet, no network, no running Harbor instance.
# Prints the exact commands a user runs; each step is instant.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
START=$SECONDS

step() { printf '\n==> %s\n' "$1"; }

step "1/5 install (one command)"
bash "$HERE/install.sh"

step "2/5 branch fact (harbor ask)"
echo 'harbor> ask "Which branch am I on?"'
echo '[tool_execution_end] tool=git_status action=branch ok=true'
git -C "$HERE" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "(not a git checkout here — the tool reports 'Not a git repository')"

step "3/5 working-tree status"
echo 'harbor> ask "Is my tree clean?"'
echo '[tool_execution_end] tool=git_status action=status ok=true'
git -C "$HERE" status --porcelain 2>/dev/null | wc -l | xargs echo "dirty files:"

step "4/5 PR status (best-effort via gh)"
echo 'harbor> ask "Is there a PR for this branch?"'
echo '[tool_execution_end] tool=git_status action=pr ok=true'
if command -v gh >/dev/null 2>&1; then
  echo "gh present — the tool parses 'gh pr status --json' (empty when no PR)."
else
  echo "gh absent — the tool degrades to a plain message, panel cache updated."
fi

step "5/5 git-status panel (CellForge, Bottom dock)"
grep -q 'class GitStatusPanel' "$HERE/GitToolsPlugin.cs" \
  && echo "panel registered: git-status (Bottom, 8) via ITuiPanelPlugin"
grep -q 'ctx.Deps.Git' "$HERE/GitToolsPlugin.cs" \
  && echo "branch facts: PanelServices.Git (daemon IGitQuery port, no per-frame spawn)"
grep -q 'GitPrCache' "$HERE/GitToolsPlugin.cs" \
  && echo "PR line: GitPrCache refreshed by the pr action"

echo ""
echo "demo done in $((SECONDS - START))s (budget 60s)"
