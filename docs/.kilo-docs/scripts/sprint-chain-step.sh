#!/usr/bin/env bash
set -uo pipefail
set -m

REPO="${1:-.}"
cd "$REPO"

CHAIN_FILE=".kilo-docs/sprint-chain.md"
DISPATCH="$HOME/.hermes/skills/autonomous-ai-agents/kilo-dispatch/scripts/kilo-dispatch.sh"
BASE_SHA="$(git rev-parse HEAD)"
LOG_FILE=".kilo-docs/scripts/sprint-chain.log"

log() { echo "[chain-step] $(date '+%d.%m %H:%M:%S') $*" | tee -a "$LOG_FILE"; }

if [[ ! -f "$CHAIN_FILE" ]]; then
  log "FATAL: $CHAIN_FILE not found"
  exit 1
fi

# find first queued/running sprint
TARGET_SPRINT=""
TARGET_MODEL=""
TARGET_PROMPT=""

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
    log "WARN: prompt missing for $SPRINT: $PROMPT"
    continue
  fi

  STATUS_FILE=".kilo-docs/sprints/$SPRINT/status.json"
  if [[ -f "$STATUS_FILE" ]]; then
    STATE=$(jq -r '.state' "$STATUS_FILE" 2>/dev/null || echo "queued")
    if [[ "$STATE" == "done" || "$STATE" == "failed" ]]; then
      continue
    fi
  fi

  TARGET_SPRINT="$SPRINT"
  TARGET_MODEL="$MODEL"
  TARGET_PROMPT="$PROMPT"
  break
done < "$CHAIN_FILE"

if [[ -z "$TARGET_SPRINT" ]]; then
  log "No queued sprints found — chain complete"
  exit 0
fi

log "=== step: $TARGET_SPRINT (model=$TARGET_MODEL) ==="

# update status to running
STATUS_FILE=".kilo-docs/sprints/$TARGET_SPRINT/status.json"
if [[ -f "$STATUS_FILE" ]]; then
  tmp=$(mktemp)
  jq --arg state "running" --arg head "$(git rev-parse --short HEAD)" \
     --arg last "$(date '+%Y-%m-%dT%H:%M:%S%z')" \
     '.state = $state | .head = $head | .last_activity = $last | .started_at = (.started_at // $last)' \
     "$STATUS_FILE" > "$tmp" && mv "$tmp" "$STATUS_FILE"
fi

# run dispatch
bash "$DISPATCH" -f "$TARGET_PROMPT" -m "$TARGET_MODEL" -r "$(pwd)" -b "$BASE_SHA" -M 55 -i 300 > /tmp/kilo-dispatch-step.log 2>&1
RC=$?

# log last 20 lines of dispatch
log "dispatch RC=$RC"
tail -20 /tmp/kilo-dispatch-step.log 2>/dev/null | while read -r l; do log "  [kilo] $l"; done

# verify finish
NEW_SHA="$(git rev-parse HEAD 2>/dev/null || echo "$BASE_SHA")"
DIRTY=$(git status --porcelain | grep -vc '.nuke/' 2>/dev/null || echo 0)
KILO_ALIVE=$(pgrep -fc '[k]ilo run' 2>/dev/null || echo 0)

if [[ "$NEW_SHA" != "$BASE_SHA" ]] && [[ "$DIRTY" -eq 0 ]] && [[ "$KILO_ALIVE" -eq 0 ]]; then
  COMMITS=$(git rev-list --count "$BASE_SHA"..HEAD 2>/dev/null || echo 0)
  log "✓ $TARGET_SPRINT finished: $COMMITS commits"
  tmp=$(mktemp)
  jq --arg state "done" --arg head "$NEW_SHA" \
     --arg last "$(date '+%Y-%m-%dT%H:%M:%S%z')" \
     --arg commits "$COMMITS" \
     '.state = $state | .head = $head | .last_activity = $last | .commits = ($commits | tonumber) | .finished_at = $last' \
     "$STATUS_FILE" > "$tmp" && mv "$tmp" "$STATUS_FILE"
else
  log "⚠ $TARGET_SPRINT incomplete: head_changed=$([ "$NEW_SHA" != "$BASE_SHA" ] && echo yes || echo no) dirty=$DIRTY kilo=$KILO_ALIVE"
  tmp=$(mktemp)
  jq --arg state "failed" --arg head "$NEW_SHA" \
     --arg last "$(date '+%Y-%m-%dT%H:%M:%S%z')" \
     '.state = $state | .head = $head | .last_activity = $last | .failures = (.failures + 1)' \
     "$STATUS_FILE" > "$tmp" && mv "$tmp" "$STATUS_FILE"
fi

# generate report
bash .kilo-docs/scripts/md-to-report.sh "$REPO" > /dev/null 2>&1 || true

log "current HEAD: $(git rev-parse --short HEAD)"
git log --oneline -3 --no-decorate 2>/dev/null | while read -r l; do log "  $l"; done

exit 0
