#!/usr/bin/env bash
set -uo pipefail

HEARTBEAT_PID=""
STATUS_SERVER_PID=""

cleanup() {
  if [[ -n "$HEARTBEAT_PID" ]] && kill -0 "$HEARTBEAT_PID" 2>/dev/null; then
    kill "$HEARTBEAT_PID" 2>/dev/null || true
    wait "$HEARTBEAT_PID" 2>/dev/null || true
  fi
  if [[ -n "$STATUS_SERVER_PID" ]] && kill -0 "$STATUS_SERVER_PID" 2>/dev/null; then
    kill "$STATUS_SERVER_PID" 2>/dev/null || true
    wait "$STATUS_SERVER_PID" 2>/dev/null || true
  fi
  rm -f /tmp/harbor-sprint-chain.lock
}
trap cleanup EXIT

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

dirty_count() { local all; all=$(git status --porcelain | wc -l || true); local excluded; excluded=$(git status --porcelain | grep -E "^M \.kilo-docs/sprints/.*/status.json|\.nuke/" | wc -l || true); echo $((all - excluded)); }

update_status() {
  local sprint="$1" state="$2"
  local status_file=".kilo-docs/sprints/${sprint,,}/status.json"
  if [[ -f "$status_file" ]]; then
    local tmp
    tmp=$(mktemp)
    if jq --arg state "$state" --arg head "$(git rev-parse --short HEAD)" \
       --arg last "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
       '.state = $state | .head = $head | .last_activity = $last' \
       "$status_file" > "$tmp" 2>/dev/null; then
      mv -f "$tmp" "$status_file" 2>/dev/null || true
    else
      rm -f "$tmp" 2>/dev/null || true
    fi
  fi
}

update_progress() {
  local sprint="$1" current_task="$2" tasks_done="$3" tasks_total="$4" eta_min="$5"
  local status_file=".kilo-docs/sprints/${sprint,,}/status.json"
  if [[ -f "$status_file" ]]; then
    local tmp
    tmp=$(mktemp)
    jq --arg task "$current_task" \
       --argjson done "$tasks_done" \
       --argjson total "$tasks_total" \
       --argjson eta "$eta_min" \
       --arg last "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
       '.progress.current_task = $task | .progress.tasks_done = $done | .progress.tasks_total = $total | .progress.eta_minutes = $eta | .progress.last_heartbeat = $last' \
       "$status_file" > "$tmp" && mv "$tmp" "$status_file"
  fi
}

health_check() {
  log "=== health check ==="
  if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    log "FATAL: not a git repo"
    exit 1
  fi
  local dirty
  dirty=$(git status --porcelain | grep -E "^M \.kilo-docs/sprints/.*/status\.json|\.nuke/" | wc -l || true)
  if [[ "$dirty" -gt 0 ]]; then
    log "WARN: dirty tree ($dirty files), cleaning..."
    git reset --hard HEAD
    git clean -fd -e '.kilo-docs/sprints/' -e '.kilo-docs/docs/' || true
  fi
  local kn
  kn=$(pgrep -af '\.kilo run' 2>/dev/null | wc -l || echo 0)
  if [[ "$kn" -gt 0 ]]; then
    log "WARN: $kn kilo processes alive, killing..."
    pkill -9 -f '\.kilo run' 2>/dev/null || true
    sleep 2
  fi
  log "health check passed"
}

start_heartbeat() {
  local sprint="$1"
  (
    while true; do
      sleep 30
      local status_file=".kilo-docs/sprints/${sprint,,}/status.json"
      if [[ -f "$status_file" ]]; then
        local tmp
        tmp=$(mktemp)
        jq --arg last "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
           '.progress.last_heartbeat = $last' \
           "$status_file" > "$tmp" && mv "$tmp" "$status_file"
      fi
    done
  ) &
  HEARTBEAT_PID=$!
}

start_status_server() {
  (
    cd .kilo-docs || exit 1
    python3 -m http.server 4321 >/dev/null 2>&1
  ) &
  STATUS_SERVER_PID=$!
  log "status server started at http://localhost:4321/ (pid=$STATUS_SERVER_PID)"
}

estimate_validation() {
  local sprint="$1"
  local sprint_dir=".kilo-docs/sprints/${sprint,,}"

  if [[ ! -f "$sprint_dir/estimate.json" ]]; then
    log "WARN: missing estimate.json for $sprint"
    return 1
  fi

  if ! jq empty "$sprint_dir/estimate.json" 2>/dev/null; then
    log "WARN: invalid estimate.json for $sprint"
    return 1
  fi

  local confidence
  confidence=$(jq -r '.confidence // 0' "$sprint_dir/estimate.json" 2>/dev/null || echo 0)
  if [[ "$(echo "$confidence < 0.7" | bc -l 2>/dev/null || echo 0)" == "1" ]]; then
    log "ALERT: confidence=$confidence < 0.7, stopping chain for human review"
    return 2
  fi

  return 0
}

# ── preflight ────────────────────────────────────────────────────────
kill_duplicates
health_check
log "=== sprint-chain starting ==="
log "repo: $REPO"
log "branch: $(git branch --show-current)"
log "HEAD: $(git rev-parse --short HEAD)"
log "last commits:"
git log --oneline -5 --no-decorate 2>/dev/null | while read -r line; do log "  $line"; done

start_status_server

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

  if [[ ! -f "$PROMPT" ]]; then
    log "WARN: prompt file missing for $SPRINT: $PROMPT"
    continue
  fi

  STATUS_FILE=".kilo-docs/sprints/${SPRINT,,}/status.json"
  if [[ -f "$STATUS_FILE" ]]; then
    STATE=$(jq -r '.state' "$STATUS_FILE" 2>/dev/null || echo "")
    RETRIES=$(jq -r '.retries // 0' "$STATUS_FILE" 2>/dev/null || echo 0)
    if [[ "$STATE" == "done" ]]; then
      log "SKIP: $SPRINT (state=$STATE)"
      continue
    fi
    if [[ "$STATE" == "running" ]]; then
      log "SKIP: $SPRINT (already running)"
      continue
    fi
    if [[ "$STATE" == "failed" && "$RETRIES" -ge 2 ]]; then
      log "SKIP: $SPRINT (state=$STATE, retries=$RETRIES)"
      continue
    fi
  fi

  log "=== starting sprint: $SPRINT (model=$MODEL, prompt=$PROMPT) ==="
  update_status "$SPRINT" "running"
  start_heartbeat "$SPRINT"

  # ensure progress block exists
  if [[ -f "$STATUS_FILE" ]]; then
    tmp=$(mktemp)
    jq '.progress //= {"current_task":"","tasks_done":0,"tasks_total":0,"eta_minutes":0,"last_heartbeat":""}' "$STATUS_FILE" > "$tmp" && mv "$tmp" "$STATUS_FILE"
  fi

  # use sprint-local base from status.json, not stale global
  SPRINT_BASE="$(jq -r '.base // empty' "$STATUS_FILE" 2>/dev/null || true)"
  if [[ -z "$SPRINT_BASE" ]]; then
    SPRINT_BASE="$(git rev-parse --short HEAD)"
    tmp=$(mktemp)
    jq --arg base "$SPRINT_BASE" '.base = $base' "$STATUS_FILE" > "$tmp" && mv "$tmp" "$STATUS_FILE"
  fi

  # run dispatch with restart policy
  RESTARTS=0
  MAX_RESTARTS=3
  RC=1
  while [[ $RESTARTS -lt $MAX_RESTARTS ]]; do
    log "dispatch attempt $((RESTARTS+1)) for $SPRINT"
    DISPATCH_OUT=$(bash "$DISPATCH" -f "$PROMPT" -m "$MODEL" -r "$(pwd)" -b "$SPRINT_BASE" -M 360 -i 300 2>&1) || true
    RC=${PIPESTATUS[0]:-$?}
    echo "$DISPATCH_OUT" | tail -30 | while read -r line; do log "  [kilo] $line"; done

    if [[ "$RC" -eq 143 ]]; then
      RESTARTS=$((RESTARTS+1))
      BACKOFF=$((60 * RESTARTS))
      log "WARN: dispatch rc=143, restarting in ${BACKOFF}s (attempt $RESTARTS/$MAX_RESTARTS)"
      sleep "$BACKOFF"
      continue
    fi
    break
  done

  # verify finish
  NEW_SHA="$(git rev-parse HEAD 2>/dev/null || echo "$SPRINT_BASE")"
  DIRTY=$(dirty_count)
  LAST_MSG=$(git log -1 --format="%s" 2>/dev/null || echo "")
  KILO_ALIVE=$(pgrep -af '\.kilo run' 2>/dev/null | wc -l || echo 0)

  if [[ "$NEW_SHA" != "$SPRINT_BASE" ]] && [[ "$DIRTY" -eq 0 ]] && [[ "$KILO_ALIVE" -eq 0 ]]; then
    log "✓ $SPRINT finished: $(git rev-list --count "$SPRINT_BASE"..HEAD) new commits"

    EST_RC=0
    estimate_validation "$SPRINT" || EST_RC=$?

    if [[ $EST_RC -eq 2 ]]; then
      log "STOP: confidence too low, human review required"
      update_status "$SPRINT" "failed"
      FAILURES=$((FAILURES+1))
      continue
    fi

    update_status "$SPRINT" "done"
    BASE_SHA="$NEW_SHA"

    if [[ -d ".kilo-docs/sprints/${SPRINT,,}/done" ]]; then
      log "→ $SPRINT marked done"
    fi
  else
    log "⚠ $SPRINT did not finish cleanly (RC=$RC, head_changed=$([ "$NEW_SHA" != "$SPRINT_BASE" ] && echo yes || echo no), dirty=$DIRTY, kilo_alive=$KILO_ALIVE)"
    update_status "$SPRINT" "failed"
    FAILURES=$((FAILURES+1))
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
