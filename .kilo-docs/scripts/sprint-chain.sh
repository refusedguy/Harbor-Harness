#!/usr/bin/env bash
set -uo pipefail

LOCK_FILE="/tmp/harbor-sprint-chain.lock"

REPO="${1:-.}"
cd "$REPO"

CHAIN_FILE=".kilo-docs/sprint-chain.md"
DISPATCH="$HOME/.hermes/skills/autonomous-ai-agents/kilo-dispatch/scripts/kilo-dispatch.sh"
BASE_SHA="$(git rev-parse HEAD)"
LOG_FILE=".kilo-docs/scripts/sprint-chain.log"

log() { echo "[chain] $(date '+%d.%m %H:%M:%S') $*" | tee -a "$LOG_FILE"; }

if [[ ! -f "$CHAIN_FILE" ]]; then
  log "FATAL: $CHAIN_FILE not found"
  exit 1
fi

kill_duplicates() {
  local pids
  pids=$(ps aux | grep -E "kilo-dispatch|kilo run" | grep -v grep | awk '{print $2}' | sort -u)
  for pid in $pids; do
    local cmdline
    cmdline=$(ps -p "$pid" -o args= 2>/dev/null || true)
    if [[ -z "$cmdline" ]]; then continue; fi
    local count
    count=$(ps aux | grep -F "$cmdline" | grep -v grep | wc -l)
    if [[ $count -gt 1 ]]; then
      log "WARN: duplicate kilo process detected ($count instances): $cmdline"
      ps aux | grep -F "$cmdline" | grep -v grep | awk '{print $2}' | sort -n | tail -n +2 | xargs -r kill 2>/dev/null || true
      log "killed duplicates, kept oldest"
    fi
  done
}

dirty_count() { git status --porcelain | grep -vc '^\(M \.\kilo-docs\/sprints\/.*\/status\.json\|\.nuke\/\)' ; }

update_status() {
  local sprint="$1" state="$2"
  local status_file=".kilo-docs/sprints/$sprint/status.json"
  if [[ -f "$status_file" ]]; then
    local tmp
    tmp=$(mktemp)
    jq --arg state "$state" --arg head "$(git rev-parse --short HEAD)" \
       --arg last "$(date '+%Y-%m-%dT%H:%M:%S%z')" \
       '.state = $state | .head = $head | .last_activity = $last' \
       "$status_file" > "$tmp" && mv "$tmp" "$status_file"
  fi
}

# ── preflight ────────────────────────────────────────────────────────
kill_duplicates
log "=== sprint-chain starting ==="
log "repo: $REPO"
log "branch: $(git branch --show-current)"
log "HEAD: $(git rev-parse --short HEAD)"
log "last commits:"
git log --oneline -5 --no-decorate 2>/dev/null | while read -r line; do log "  $line"; done

# ── parse chain and run sprints ──────────────────────────────────────
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
    continue
  fi

  # skip if prompt file missing
  if [[ ! -f "$PROMPT" ]]; then
    log "WARN: prompt file missing for $SPRINT: $PROMPT"
    continue
  fi

  # check status.json — skip if already done
  STATUS_FILE=".kilo-docs/sprints/$SPRINT/status.json"
  if [[ -f "$STATUS_FILE" ]]; then
    STATE=$(jq -r '.state' "$STATUS_FILE" 2>/dev/null || echo "")
    RETRIES=$(jq -r '.retries // 0' "$STATUS_FILE" 2>/dev/null || echo 0)
    if [[ "$STATE" == "done" ]]; then
      log "SKIP: $SPRINT (state=$STATE)"
      continue
    fi
    if [[ "$STATE" == "failed" && "$RETRIES" -ge 2 ]]; then
      log "SKIP: $SPRINT (state=$STATE, retries=$RETRIES)"
      continue
    fi
  fi

  log "=== starting sprint: $SPRINT (model=$MODEL, prompt=$PROMPT) ==="
  update_status "$SPRINT" "running"

  # use sprint-local base from status.json, not stale global
  SPRINT_BASE="$(jq -r '.base // empty' ".kilo-docs/sprints/$SPRINT/status.json" 2>/dev/null || true)"
  if [[ -z "$SPRINT_BASE" ]]; then
    SPRINT_BASE="$(git rev-parse --short HEAD)"
    tmp=$(mktemp)
    jq --arg base "$SPRINT_BASE" '.base = $base' ".kilo-docs/sprints/$SPRINT/status.json" > "$tmp" && mv "$tmp" ".kilo-docs/sprints/$SPRINT/status.json"
  fi

  # run dispatch, capture output
  DISPATCH_OUT=$(bash "$DISPATCH" -f "$PROMPT" -m "$MODEL" -r "$(pwd)" -b "$SPRINT_BASE" -M 360 -i 300 2>&1) || true
  RC=${PIPESTATUS[0]:-$?}

  # log dispatch output
  echo "$DISPATCH_OUT" | tail -30 | while read -r line; do log "  [kilo] $line"; done

  # verify finish
  NEW_SHA="$(git rev-parse HEAD 2>/dev/null || echo "$SPRINT_BASE")"
  DIRTY=$(dirty_count)
  LAST_MSG=$(git log -1 --format="%s" 2>/dev/null || echo "")
  KILO_ALIVE=$(pgrep -af '\.kilo run' 2>/dev/null | wc -l || echo 0)

  if [[ "$NEW_SHA" != "$SPRINT_BASE" ]] && [[ "$DIRTY" -eq 0 ]] && [[ "$KILO_ALIVE" -eq 0 ]]; then
    log "✓ $SPRINT finished: $(git rev-list --count "$SPRINT_BASE"..HEAD) new commits"
    update_status "$SPRINT" "done"
    BASE_SHA="$NEW_SHA"
    
    # move prompt to done/
    if [[ -d ".kilo-docs/sprints/$SPRINT/done" ]]; then
      log "→ $SPRINT marked done"
    fi
  else
    log "⚠ $SPRINT did not finish cleanly (RC=$RC, head_changed=$([ "$NEW_SHA" != "$BASE_SHA" ] && echo yes || echo no), dirty=$DIRTY, kilo_alive=$KILO_ALIVE)"
    update_status "$SPRINT" "failed"
  fi

  log "current HEAD: $(git rev-parse --short HEAD)"
  log "last commits:"
  git log --oneline -3 --no-decorate 2>/dev/null | while read -r l; do log "  $l"; done

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
