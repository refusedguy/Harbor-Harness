#!/usr/bin/env bash
# sprint-chain.sh — fully automated sprint dispatcher (Release Engineering).
#
# Reads .kilo-docs/sprint-chain.md, picks the next queued sprint and runs the
# complete cycle with zero manual steps:
#
#   preflight → clean-tree check → feature branch sprint/<name> →
#   prompt.md copy into sprint dir → kilo dispatch (DISPATCH) → wait →
#   status.json (pass/fail counts, head SHA, model, duration, log excerpt) →
#   release notes (CHANGELOG.md + sprint/<name> tag) → push sprint branch.
#
# SAFETY / FAIL-SAFE RULES (enforced by this script):
#   - NEVER `rm -rf`, NEVER `git reset --hard` / `git clean`, NEVER force-push.
#   - Writes only: .kilo-docs/sprints/<slug>/*, .kilo-docs/scripts/*.log,
#     CHANGELOG.md (via release-notes.sh), git branch/tag refs, /tmp files.
#   - Mainline branches (dev/master/main) are never COMMITTED to: starting the
#     chain on a clean mainline auto-creates the sprint feature branch. Every
#     sprint lands on its own `sprint/<name>` branch (stacked: the next sprint
#     branches from the previous sprint's tip). Mainline moves only when a
#     human merges/approves.
#   - A dirty worktree (beyond tolerated sprint metadata) aborts the chain —
#     nothing is cleaned or overwritten.
#   - Liveness/progress files go to /tmp, never into the worktree (a dirty
#     tree inside the repo would stall the dispatcher's clean-tree check).
#
# Usage:
#   sprint-chain.sh [REPO]              # default REPO = current directory
#   DRY_RUN=1 sprint-chain.sh           # plan only: no dispatch, no writes
#   HARBOR_CHAIN_PUSH=0 sprint-chain.sh # skip pushing the sprint branch
#   RELEASE_NOTES=0 sprint-chain.sh     # skip CHANGELOG/tag generation
#
# Exit codes: 0 = queue empty or all eligible sprints finished; 1 = fatal;
#             2 = sprint failed (chain stopped for inspection).

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
}
trap cleanup EXIT

REPO="${1:-.}"
DRY_RUN="${DRY_RUN:-0}"
PUSH_BRANCH="${HARBOR_CHAIN_PUSH:-1}"
RUN_RELEASE_NOTES="${RELEASE_NOTES:-1}"
MAX_DISPATCH_MINUTES="${HARBOR_CHAIN_MAX_MINUTES:-360}"
DISPATCH_INTERVAL="${HARBOR_CHAIN_INTERVAL:-300}"
# A crashed dispatcher leaves state=running with no live heartbeat; the chain
# must self-heal (requeue) instead of skipping the sprint forever.
STALE_RUNNING_MINUTES="${HARBOR_CHAIN_STALE_MINUTES:-15}"

cd "$REPO" 2>/dev/null || { echo "[chain] FATAL: repo not found: $REPO"; exit 1; }

CHAIN_FILE=".kilo-docs/sprint-chain.md"
DISPATCH="${HARBOR_CHAIN_DISPATCH:-$HOME/.hermes/skills/autonomous-ai-agents/kilo-dispatch/scripts/kilo-dispatch.sh}"
RELEASE_NOTES_SH=".kilo-docs/scripts/release-notes.sh"
LOG_FILE=".kilo-docs/scripts/sprint-chain.log"
MAINLINE_RE='^(dev|master|main)$'

log() { echo "[chain] $(date '+%d.%m %H:%M:%S') $*" | tee -a "$LOG_FILE"; }
die() { log "FATAL: $*"; exit 1; }

[[ -f "$CHAIN_FILE" ]] || die "$CHAIN_FILE not found"
[[ -f "$DISPATCH" ]] || die "dispatcher not found: $DISPATCH"
command -v jq >/dev/null 2>&1 || die "jq is required"
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || die "not a git repo"

# ── single-instance lock (flock, no file deletion needed) ────────────
exec 9>/tmp/harbor-sprint-chain.lock
if ! flock -n 9; then
  die "another sprint-chain instance is already running (lock: /tmp/harbor-sprint-chain.lock)"
fi

# ── preflight ────────────────────────────────────────────────────────
# Tolerated dirt: sprint status.json updates, untracked .kilo-docs reports,
# .nuke/ artifacts. Everything else (tracked modifications!) aborts the chain.
dirty_count() {
  git status --porcelain 2>/dev/null | grep -vE \
    '^.. \.kilo-docs/sprints/[^/]+/status\.json$|^\?\? \.kilo-docs/|^.. \.nuke/' | wc -l
}

BRANCH="$(git branch --show-current)"
log "=== sprint-chain starting ==="
log "repo: $(pwd)"
log "branch: $BRANCH  HEAD: $(git rev-parse --short HEAD)"

# Mainline is never committed to; but a CLEAN mainline may seed the first
# sprint branch (the branch step below switches away before any write).
if [[ "$BRANCH" =~ $MAINLINE_RE && "$DRY_RUN" != "1" && "$(dirty_count)" -gt 0 ]]; then
  die "refusing to run on mainline branch '$BRANCH' with a dirty worktree — commit/stash manually (fail-safe)"
fi

DIRTY=$(dirty_count)
if [[ "$DIRTY" -gt 0 && "$DRY_RUN" != "1" ]]; then
  log "dirty worktree ($DIRTY entries):"
  git status --porcelain | grep -vE \
    '^.. \.kilo-docs/sprints/[^/]+/status\.json$|^\?\? \.kilo-docs/|^.. \.nuke/' | head -10 | while read -r l; do log "  $l"; done
  die "worktree is dirty — commit or stash manually; this script never resets/cleans"
fi

kilo_alive() { pgrep -fc '\bkilo run\b' 2>/dev/null || true; }
if [[ "$(kilo_alive)" -gt 0 && "$DRY_RUN" != "1" ]]; then
  die "$(kilo_alive) kilo process(es) already running — refusing to interfere (no auto-kill)"
fi

running_stale() {
  # running_stale <slug> <status_file> → rc 0 when the "running" dispatch has
  # no live heartbeat: either the heartbeat file (written every 30 s while a
  # dispatch runs) or the .progress.last_heartbeat status-ping must be fresher
  # than STALE_RUNNING_MINUTES, otherwise the dispatcher is considered dead.
  local slug="$1" status_file="$2" ts=0 le
  local hb="/tmp/harbor-sprint-progress-${slug}.json"
  [[ -f "$hb" ]] && ts=$(stat -c %Y "$hb" 2>/dev/null || echo 0)
  le=$(jq -r '.progress.last_heartbeat // ""' "$status_file" 2>/dev/null || true)
  if [[ -n "$le" ]]; then
    le=$(date -u -d "$le" +%s 2>/dev/null) || le=0
  else
    le=0
  fi
  (( le > ts )) && ts=$le
  (( ts == 0 )) && return 0
  (( $(date +%s) - ts > STALE_RUNNING_MINUTES * 60 ))
}

start_status_server() {
  [[ "${HARBOR_CHAIN_STATUS_SERVER:-1}" == "1" ]] || return 0
  (
    cd .kilo-docs 2>/dev/null || exit 0
    python3 -m http.server 4321 >/dev/null 2>&1
  ) &
  STATUS_SERVER_PID=$!
  log "status server: http://localhost:4321/ (pid=$STATUS_SERVER_PID)"
}

start_heartbeat() {
  # Liveness probe OUTSIDE the git tree (/tmp) — writing status.json here
  # would keep the worktree dirty and stall kilo-dispatch's clean-tree check.
  # start_heartbeat <slug>
  local slug="$1" hb="/tmp/harbor-sprint-progress-${slug}.json"
  (
    while true; do
      sleep 30
      jq -n --arg slug "$slug" --arg last "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
        '{sprint: $slug, last_heartbeat: $last}' > "$hb" 2>/dev/null || true
    done
  ) &
  HEARTBEAT_PID=$!
}

slugify() {
  # lowercase, spaces/underscores → hyphens, strip anything but [a-z0-9-]
  printf '%s' "$1" | tr '[:upper:] _' '[:lower:] --' | sed 's/[^a-z0-9-]//g; s/--*/-/g; s/^-//; s/-$//'
}

parse_test_counts() {
  # Best-effort pass/fail counts from a dispatch log (TUnit / dotnet test /
  # generic `passed: N` / `failed: N` summary lines, localized variants incl.).
  # parse_test_counts <log_file> → sets TESTS_PASSED / TESTS_FAILED
  local logf="$1" p f
  p=$(grep -oiE '(passed|успешно выполнено|пройдено)[^0-9]*([0-9]+)|([0-9]+)[[:space:]]*(passed|passed!)' "$logf" 2>/dev/null \
      | grep -oE '[0-9]+' | tail -1 || true)
  f=$(grep -oiE '(failed|сбой|провалено)[^0-9]*([0-9]+)|([0-9]+)[[:space:]]*(failed|failed!)' "$logf" 2>/dev/null \
      | grep -oE '[0-9]+' | tail -1 || true)
  TESTS_PASSED="${p:-0}"
  TESTS_FAILED="${f:-0}"
}

PROGRESS_BLOCK='{"current_task":"","tasks_done":0,"tasks_total":0,"eta_minutes":0,"last_heartbeat":""}'

init_status_file() {
  # init_status_file <status_file> <slug> <model> <prompt> <base> <branch> <state>
  local status_file="$1" slug="$2" model="$3" prompt="$4" base="$5" branch="$6" state="$7"
  local tmp
  if [[ ! -f "$status_file" ]]; then
    jq -n --arg slug "$slug" --arg model "$model" --arg prompt "$prompt" \
          --arg base "$base" --arg branch "$branch" --arg state "$state" \
          --arg now "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
          '{state: $state, sprint: $slug, name: $slug, model: $model, prompt: $prompt,
            head: "", commits: 0, created_at: $now, base: $base, branch: $branch}' \
      > "$status_file"
    return 0
  fi
  # existing file: flip state, keep legacy fields intact
  tmp=$(mktemp)
  jq --arg state "$state" --arg base "$base" \
     '.state = $state | .base = $base | .name //= .sprint // $slug' \
     --arg slug "$slug" \
     "$status_file" > "$tmp" 2>/dev/null && mv -f "$tmp" "$status_file" \
    || rm -f "$tmp"
}

write_status() {
  # write_status <status_file> <state> <head> <commits> <duration> <dispatch_rc> <dirty> <log_src>
  local status_file="$1" state="$2" head_sha="$3" commits="$4" duration="$5" rc="$6" dirty="$7"
  local tmp excerpt logsrc="$8"
  if [[ -f "$logsrc" ]]; then
    excerpt=$(tail -20 "$logsrc" 2>/dev/null || true)
  else
    excerpt="${DISPATCH_EXCERPT:-}"
  fi
  tmp=$(mktemp)
  jq --arg state "$state" --arg head "$head_sha" --argjson commits "$commits" \
     --argjson duration "$duration" --argjson rc "$rc" --argjson dirty "$dirty" \
     --argjson tests_passed "${TESTS_PASSED:-0}" --argjson tests_failed "${TESTS_FAILED:-0}" \
     --arg branch "$(git branch --show-current)" \
     --arg now "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" --arg excerpt "$excerpt" \
     '(.state = $state
      | .head = $head
      | .commits = $commits
      | .branch = $branch
      | .name //= .sprint
      | .completed_at = $now
      | .last_activity = $now
      | .duration_seconds = $duration
      | .dispatch_exit_code = $rc
      | .log_excerpt = $excerpt
      | .tests = {
          passed: $tests_passed,
          failed: $tests_failed
        }
      | .checks = {
          dispatch_ok: ($rc == 0),
          head_advanced: ($head != .base),
          worktree_clean: ($dirty == 0),
          passed: ([$rc == 0, ($head != .base), ($dirty == 0)] | map(select(.)) | length),
          failed: ([$rc == 0, ($head != .base), ($dirty == 0)] | map(select(. == false)) | length)
        }))' \
     "$status_file" > "$tmp" 2>/dev/null && mv -f "$tmp" "$status_file" \
    || { rm -f "$tmp"; log "WARN: could not update $status_file"; }
}

start_status_server

# ── parse chain, run sprints ─────────────────────────────────────────
ANY_STARTED=0
while IFS= read -r line; do
  [[ -z "$line" ]] && continue
  [[ "$line" =~ ^# ]] && continue
  [[ "$line" =~ ^FORMAT: ]] && continue
  [[ "$line" =~ ^- ]] && continue
  [[ "$line" =~ ^sprint\|NAME\| ]] && continue
  [[ "$line" =~ ^sprint\|([^|]+)\|([^|]+)\|(.+)$ ]] || continue

  SPRINT="${BASH_REMATCH[1]}"
  MODEL="${BASH_REMATCH[2]}"
  PROMPT="${BASH_REMATCH[3]}"
  [[ -z "$SPRINT" || -z "$MODEL" || -z "$PROMPT" ]] && continue

  SLUG="$(slugify "$SPRINT")"
  [[ -z "$SLUG" ]] && { log "WARN: unusable sprint name '$SPRINT', skipped"; continue; }

  SPRINT_DIR=".kilo-docs/sprints/${SLUG}"
  STATUS_FILE="${SPRINT_DIR}/status.json"
  BRANCH_NAME="sprint/${SLUG}"

  if [[ ! -f "$PROMPT" ]]; then
    log "WARN: prompt file missing for $SLUG: $PROMPT"
    continue
  fi

  if [[ -f "$STATUS_FILE" ]]; then
    STATE=$(jq -r '.state // ""' "$STATUS_FILE" 2>/dev/null || echo "")
    RETRIES=$(jq -r '.retries // 0' "$STATUS_FILE" 2>/dev/null || echo 0)
    case "$STATE" in
      done)    log "SKIP: $SLUG (state=done)"; continue ;;
      running)
        if [[ "$DRY_RUN" == "1" ]]; then
          if running_stale "$SLUG" "$STATUS_FILE"; then
            log "NOTE: $SLUG state=running but heartbeat is stale — would requeue and re-dispatch"
          else
            log "SKIP: $SLUG (live dispatch running)"
          fi
          continue
        fi
        if running_stale "$SLUG" "$STATUS_FILE"; then
          RETRIES=$((RETRIES+1))
          tmp=$(mktemp)
          jq --argjson r "$RETRIES" --arg now "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
             '.state = "failed" | .retries = $r | .stale_requeued_at = $now' \
             "$STATUS_FILE" > "$tmp" 2>/dev/null && mv -f "$tmp" "$STATUS_FILE" || rm -f "$tmp"
          log "WARN: $SLUG state=running but no live heartbeat (${STALE_RUNNING_MINUTES}m) — requeued (retries=$RETRIES)"
        else
          log "SKIP: $SLUG (live dispatch running)"; continue
        fi ;;
      failed)
        if [[ "$RETRIES" -ge 2 ]]; then log "SKIP: $SLUG (state=failed, retries=$RETRIES)"; continue; fi ;;
    esac
  fi

  # ── plan ──
  BASE="$(git rev-parse --short HEAD)"
  log "=== next sprint: $SLUG (model=$MODEL) ==="
  log "  base: $BASE → branch: $BRANCH_NAME → prompt: $PROMPT"

  if [[ "$DRY_RUN" == "1" ]]; then
    log "DRY_RUN: skipping dispatch, status writes and release notes for $SLUG"
    continue
  fi

  ANY_STARTED=1

  # ── prompt into sprint dir (the only out-of-chain copy allowed) ──
  mkdir -p "$SPRINT_DIR"
  if [[ "$PROMPT" != "${SPRINT_DIR}/prompt.md" ]] && ! cmp -s "$PROMPT" "${SPRINT_DIR}/prompt.md" 2>/dev/null; then
    cp "$PROMPT" "${SPRINT_DIR}/prompt.md" || die "cannot copy prompt into $SPRINT_DIR"
    log "prompt copied → ${SPRINT_DIR}/prompt.md"
  fi

  # ── feature branch (fail-safe: mainline stays clean) ──
  if git show-ref --verify --quiet "refs/heads/${BRANCH_NAME}"; then
    git checkout "$BRANCH_NAME" >/dev/null 2>&1 || die "cannot checkout existing branch $BRANCH_NAME"
    log "resumed existing branch: $BRANCH_NAME"
  else
    git checkout -b "$BRANCH_NAME" >/dev/null 2>&1 || die "cannot create branch $BRANCH_NAME from $BASE"
    log "created branch: $BRANCH_NAME (from $BASE)"
  fi

  init_status_file "$STATUS_FILE" "$SLUG" "$MODEL" "${SPRINT_DIR}/prompt.md" "$BASE" "$BRANCH_NAME" "running"
  start_heartbeat "$SLUG"

  # ── dispatch (synchronous; the dispatcher waits for kilo) ──
  START_TS=$(date +%s)
  DISPATCH_LOG="${SPRINT_DIR}/dispatch.log"
  RESTARTS=0; MAX_RESTARTS=3; RC=1
  while [[ $RESTARTS -lt $MAX_RESTARTS ]]; do
    log "dispatch attempt $((RESTARTS+1))/$MAX_RESTARTS for $SLUG"
    DISPATCH_EXCERPT=$(bash "$DISPATCH" -f "${SPRINT_DIR}/prompt.md" -m "$MODEL" -r "$(pwd)" \
      -b "$BASE" -M "$MAX_DISPATCH_MINUTES" -i "$DISPATCH_INTERVAL" -l "$DISPATCH_LOG" 2>&1)
    RC=$?
    if [[ -n "$DISPATCH_EXCERPT" ]]; then
      printf '%s\n' "$DISPATCH_EXCERPT" | tail -10 | while read -r l; do log "  [kilo] $l"; done
    fi
    if [[ "$RC" -eq 143 ]]; then
      RESTARTS=$((RESTARTS+1))
      log "WARN: dispatch rc=143, restarting (attempt $RESTARTS/$MAX_RESTARTS)"
      continue
    fi
    break
  done
  # stop the liveness loop before any verification — it must not dirty the tree
  if [[ -n "$HEARTBEAT_PID" ]] && kill -0 "$HEARTBEAT_PID" 2>/dev/null; then
    kill "$HEARTBEAT_PID" 2>/dev/null || true
    wait "$HEARTBEAT_PID" 2>/dev/null || true
    HEARTBEAT_PID=""
  fi
  DURATION=$(( $(date +%s) - START_TS ))

  # ── verify finish ──
  NEW_SHA="$(git rev-parse --short HEAD 2>/dev/null || echo "$BASE")"
  DIRTY=$(dirty_count)
  COMMITS=$(git rev-list --count "${BASE}..HEAD" 2>/dev/null || echo 0)
  TESTS_PASSED=0; TESTS_FAILED=0
  [[ -f "$DISPATCH_LOG" ]] && parse_test_counts "$DISPATCH_LOG"

  if [[ "$RC" -eq 0 && "$NEW_SHA" != "$BASE" && "$DIRTY" -eq 0 ]]; then
    write_status "$STATUS_FILE" "done" "$NEW_SHA" "$COMMITS" "$DURATION" "$RC" "$DIRTY" "$DISPATCH_LOG"
    log "✓ $SLUG finished: $COMMITS commits in ${DURATION}s (head=$NEW_SHA, tests ${TESTS_PASSED}p/${TESTS_FAILED}f)"

    if [[ "$RUN_RELEASE_NOTES" == "1" && -f "$RELEASE_NOTES_SH" ]]; then
      if bash "$RELEASE_NOTES_SH" "$SLUG" >> "$LOG_FILE" 2>&1; then
        log "release notes written (CHANGELOG.md + tag sprint/$SLUG)"
      else
        log "WARN: release-notes.sh failed for $SLUG (status.json still written)"
      fi
    fi
    git add "${STATUS_FILE}" 2>/dev/null
    git commit -m "chore(sprint): $SLUG status → done ($COMMITS commits, ${DURATION}s)" \
      -- "${STATUS_FILE}" >/dev/null 2>&1 || log "WARN: status commit skipped for $SLUG"
  else
    RETRIES=$((RETRIES+1))
    write_status "$STATUS_FILE" "failed" "$NEW_SHA" "$COMMITS" "$DURATION" "$RC" "$DIRTY" "$DISPATCH_LOG"
    tmp=$(mktemp)
    jq --argjson r "$RETRIES" '.retries = $r' "$STATUS_FILE" > "$tmp" 2>/dev/null \
      && mv -f "$tmp" "$STATUS_FILE" || rm -f "$tmp"
    log "⚠ $SLUG did not finish (RC=$RC, head $BASE→$NEW_SHA, dirty=$DIRTY, ${DURATION}s) — chain stopped"
    log "  inspect branch: $BRANCH_NAME, log: $DISPATCH_LOG"
    exit 2
  fi

  log "HEAD now: $(git rev-parse --short HEAD)"
done < "$CHAIN_FILE"

# ── push sprint branches (never mainline) ────────────────────────────
if [[ "$PUSH_BRANCH" == "1" && "$ANY_STARTED" == "1" ]]; then
  CURRENT="$(git branch --show-current)"
  if [[ "$CURRENT" =~ ^sprint/ ]]; then
    log "pushing sprint branch: $CURRENT (mainline untouched — merge manually after review)"
    git push -u origin "$CURRENT" 2>&1 | tail -3 | tee -a "$LOG_FILE" || log "WARN: push failed (offline? no remote?)"
  fi
elif [[ "$ANY_STARTED" == "1" ]]; then
  log "push disabled (HARBOR_CHAIN_PUSH=0)"
fi

log "=== sprint-chain finished ==="
log "waiting for human approval: merge sprint/* branches into mainline when CI is green"
exit 0
