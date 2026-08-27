#!/usr/bin/env bash
set -euo pipefail

REPO="${1:-.}"
cd "$REPO"

CHAIN_FILE=".kilo-docs/sprint-chain.md"
DISPATCH="$HOME/.hermes/skills/autonomous-ai-agents/kilo-dispatch/scripts/kilo-dispatch.sh"
BASE_SHA="$(git rev-parse HEAD)"
LOG_FILE=".kilo-docs/scripts/sprint-chain.log"

# ── helpers ──────────────────────────────────────────────────────────
log() { echo "[chain] $(date '+%d.%m %H:%M:%S') $*" | tee -a "$LOG_FILE"; }
last_commits() { git log --oneline -5 --no-decorate 2>/dev/null || echo "no commits yet"; }
kill_duplicates() {
  # kill duplicate kilo-dispatch processes for the same prompt, keep oldest
  local pids
  pids=$(ps aux | grep -E "kilo-dispatch|kilo run" | grep -v grep | awk '{print $2}' | sort -u)
  for pid in $pids; do
    local cmdline
    cmdline=$(ps -p "$pid" -o args= 2>/dev/null || true)
    if [[ -z "$cmdline" ]]; then continue; fi
    # if there are multiple processes with the same prompt, kill all but the oldest
    local count
    count=$(ps aux | grep -F "$cmdline" | grep -v grep | wc -l)
    if [[ $count -gt 1 ]]; then
      log "WARN: duplicate kilo process detected ($count instances): $cmdline"
      # kill all but the first (oldest by PID)
      ps aux | grep -F "$cmdline" | grep -v grep | awk '{print $2}' | sort -n | tail -n +2 | xargs -r kill 2>/dev/null || true
      log "killed duplicates, kept oldest"
    fi
  done
}

# ── detect current sprint from branch name ───────────────────────────
detect_current_sprint() {
  local branch
  branch="$(git branch --show-current 2>/dev/null || echo "")"
  if [[ -n "$branch" ]]; then
    # map branch → sprint name if there's a convention
    case "$branch" in
      *ui-final*|*ui-final*) echo "UI-FINAL" ;;
      *ui-v2*|*ui-v2*)     echo "UI-V2" ;;
      *multi-agent*)       echo "Multi-Agent" ;;
      *perf*)              echo "Performance" ;;
      *ide*)               echo "IDE-Integration" ;;
      *security*)          echo "Security" ;;
      *release*)           echo "Release-Engineering" ;;
      *testing*)           echo "Testing-Strategy" ;;
      *)                   echo "" ;;
    esac
  fi
}

# ── preflight ────────────────────────────────────────────────────────
if [[ ! -f "$CHAIN_FILE" ]]; then
  log "FATAL: $CHAIN_FILE not found"
  exit 1
fi

# clean up duplicate kilo processes on startup
kill_duplicates

# show what we're working with
CURRENT_SPRINT="$(detect_current_sprint)"
log "=== sprint-chain starting ==="
log "repo: $REPO"
log "branch: $(git branch --show-current)"
log "HEAD: $(git rev-parse --short HEAD)"
log "last commits:"
last_commits | while read -r line; do log "  $line"; done
if [[ -n "$CURRENT_SPRINT" ]]; then
  log "detected current sprint from branch: $CURRENT_SPRINT"
fi

# ── parse chain and run sprints ──────────────────────────────────────
IN_CHAIN=false
SKIP_UNTIL_CURRENT=false

if [[ -n "$CURRENT_SPRINT" ]]; then
  # skip sprints until we find the current one in the chain
  SKIP_UNTIL_CURRENT=true
fi

while IFS= read -r line; do
  [[ -z "$line" ]] && continue
  [[ "$line" =~ ^# ]] && continue
  [[ "$line" =~ ^FORMAT: ]] && continue
  [[ "$line" =~ ^- ]] && continue
  [[ "$line" =~ ^sprint\|NAME\| ]] && continue

  if [[ "$line" =~ ^sprint\|([^|]+)\|([^|]+)\|(.+)$ ]]; then
    SPRINT="${BASH_REMATCH[1]}"
    MODEL="${BASH_REMATCH[2]}"
    PROMPT="${BASH_REMATCH[3]}"
  else
    continue
  fi

  if [[ -z "$SPRINT" || -z "$MODEL" || -z "$PROMPT" ]]; then
    log "WARN: malformed line: $line"
    continue
  fi

  # skip already-completed sprints if we detected current sprint from branch
  if [[ "$SKIP_UNTIL_CURRENT" == true ]]; then
    if [[ "$SPRINT" == "$CURRENT_SPRINT" ]]; then
      SKIP_UNTIL_CURRENT=false
      log "=== resuming at sprint: $SPRINT (detected from branch) ==="
    else
      log "SKIP: $SPRINT (already done, branch is at $CURRENT_SPRINT)"
      continue
    fi
  fi

  log "=== starting sprint: $SPRINT (model=$MODEL, prompt=$PROMPT) ==="
  log "git status before:"
  git status --short | head -20 | while read -r sl; do log "  $sl"; done

  # run the dispatch, capture output
  DISPATCH_OUT=$(bash "$DISPATCH" -f "$PROMPT" -m "$MODEL" -r "$(pwd)" -b "$BASE_SHA" -M 360 -i 300 2>&1) || true
  RC=${PIPESTATUS[0]:-$?}

  # log dispatch output (last 30 lines)
  echo "$DISPATCH_OUT" | tail -30 | while read -r line; do log "  [kilo] $line"; done

  # check progress
  NEW_SHA="$(git rev-parse HEAD 2>/dev/null || echo "$BASE_SHA")"
  if [[ "$NEW_SHA" != "$BASE_SHA" ]]; then
    COMMITS="$(git rev-list --count "$BASE_SHA"..HEAD 2>/dev/null || echo 0)"
    log "✓ $SPRINT finished: $COMMITS new commits (base=$NEW_SHA)"
    BASE_SHA="$NEW_SHA"
  else
    log "⚠ $SPRINT finished: no new commits (RC=$RC), continuing chain"
  fi

  # show last 5 commits after each sprint
  log "current HEAD: $(git rev-parse --short HEAD)"
  log "last commits:"
  last_commits | while read -r line; do log "  $line"; done

  # clean up any duplicates kilo left behind
  kill_duplicates

done < "$CHAIN_FILE"

# ── final push ───────────────────────────────────────────────────────
if command -v gh >/dev/null 2>&1; then
  log "=== final push + PR update ==="
  git -C "$REPO" push origin dev 2>&1 | tee -a "$LOG_FILE" || true
  PR_BODY="$(gh pr view 2 --json body -q .body 2>/dev/null || echo '')"
  if [[ -n "$PR_BODY" ]]; then
    APPEND="### Sprint chain finished ($(date '+%d.%m.%Y %H:%M'))\n- All sprints in queue completed\n- HEAD: $(git -C "$REPO" rev-parse --short HEAD)\n- Branch: $(git -C "$REPO" branch --show-current)\n"
    printf "%s\n%s" "$PR_BODY" "$APPEND" | gh pr edit 2 --body-file - 2>&1 | tee -a "$LOG_FILE" || true
  fi
  log "=== all sprints done ==="
fi
