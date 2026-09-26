#!/usr/bin/env bash
# release-notes.sh — automatic release notes for a finished sprint.
#
#   release-notes.sh <sprint-slug>
#
# Pipeline (after a sprint reaches state=done):
#   1. read sprint title + tasks from .kilo-docs/sprints/<slug>/prompt.md
#   2. collect git log since the previous sprint/* tag (fallback: base SHA)
#   3. merge tasks[] / results / metrics into the sprint's status.json
#   4. insert a `### Sprint <name>` section under ## [Unreleased] in CHANGELOG.md
#   5. create the annotated tag `sprint/<slug>` when missing
#
# Writes ONLY: .kilo-docs/sprints/<slug>/status.json, CHANGELOG.md, one tag.
# Idempotent: re-running does not duplicate CHANGELOG entries or retag.
# Called by sprint-chain.sh after a sprint finishes; also runnable by hand.

set -euo pipefail
set +m

cd "$(git rev-parse --show-toplevel)"

SLUG="${1:?usage: release-notes.sh <sprint-slug>}"
SPRINT_DIR=".kilo-docs/sprints/${SLUG}"
STATUS_FILE="${SPRINT_DIR}/status.json"
PROMPT_FILE="${SPRINT_DIR}/prompt.md"
CHANGELOG_FILE="CHANGELOG.md"
TAG_NAME="sprint/${SLUG}"

command -v jq >/dev/null 2>&1 || { echo "release-notes: jq required"; exit 1; }
[[ -f "$STATUS_FILE" ]] || { echo "release-notes: no $STATUS_FILE"; exit 1; }

# 1) title from prompt.md line 1 ("Ты работаешь в репозитории … Спринт X.")
TITLE="$SLUG"
if [[ -f "$PROMPT_FILE" ]]; then
  T="$(sed -nE 's/.*Спринт ([^.]+)\.?$/\1/p' "$PROMPT_FILE" | head -1)"
  [[ -n "$T" ]] && TITLE="$T"
fi

# tasks: numbered `1. **Title.**` bullets under the "## Tasks" heading
TASKS_JSON="$(awk '/^## Tasks/{f=1;next} /^## /{f=0} f' "$PROMPT_FILE" 2>/dev/null \
  | sed -nE 's/^[[:space:]]*[0-9]+\.[[:space:]]+\*\*([^*]+)\*\*.*/\1/p' \
  | jq -Rsc 'split("\n") | map(select(length > 0))' 2>/dev/null || echo "[]")"

# 2) commit range: previous sprint tag → HEAD, else the sprint's base,
#    else the last commit before the sprint was queued (status.json created_at)
PREV_TAG="$(git tag -l 'sprint/*' --sort=-creatordate | grep -Fxv "$TAG_NAME" | head -1 || true)"
BASE_SHA="$(jq -r '.base // ""' "$STATUS_FILE" 2>/dev/null || echo "")"
RANGE=""
if [[ -n "$BASE_SHA" ]] && git rev-parse --verify -q "${BASE_SHA}^{commit}" >/dev/null; then
  RANGE="${BASE_SHA}..HEAD"
elif [[ -n "$PREV_TAG" ]]; then
  RANGE="${PREV_TAG}..HEAD"
else
  CREATED_AT="$(jq -r '.created_at // ""' "$STATUS_FILE" 2>/dev/null || echo "")"
  BASE_AT=""
  if [[ -n "$CREATED_AT" ]]; then
    BASE_AT="$(git rev-list -1 --before="$CREATED_AT" HEAD 2>/dev/null || true)"
  fi
  if [[ -n "$BASE_AT" ]]; then
    RANGE="${BASE_AT}..HEAD"
  else
    # --max-count=1 BEFORE the range: truncating in git avoids a
    # `| head -1` early-close SIGPIPE (rc 141) that would kill the
    # script under `set -euo pipefail` once the range is long.
    RANGE="$(git rev-list --max-count=1 --max-parents=0 HEAD)..HEAD"
  fi
fi
COMMIT_COUNT="$(git rev-list --count "$RANGE" 2>/dev/null || echo 0)"

# 3) merge tasks + results + metrics into status.json
#    (.head / .commits are refreshed too: re-verification commits after the
#    dispatch-completed write_status would otherwise leave a stale tip SHA)
TMP_JSON="$(mktemp)"
# --max-count=40 instead of `| head -40`: an early-closing reader turns
# git's next flush into EPIPE/SIGPIPE (rc 141) — under `set -euo pipefail`
# that aborts the script mid-run whenever the commit range exceeds 40.
COMMITS_JSON="$(git log --max-count=40 --format='- %h %s' "$RANGE" 2>/dev/null \
  | jq -Rsc 'split("\n") | map(select(length > 0))')"
HEAD_SHA="$(git rev-parse --short HEAD 2>/dev/null || echo "")"
jq --arg title "$TITLE" \
   --argjson tasks "$TASKS_JSON" \
   --argjson commits "$COMMITS_JSON" \
   --argjson commit_count "$COMMIT_COUNT" \
   --arg range "$RANGE" \
   --arg head "$HEAD_SHA" \
   '.name //= (.sprint // "")
    | .title = $title
    | .tasks = $tasks
    | .head = $head
    | .commits = $commit_count
    | .last_activity = (now | todateiso8601)
    | .results = {commit_count: $commit_count, range: $range, commits: $commits}' \
  "$STATUS_FILE" > "$TMP_JSON" 2>/dev/null \
  && mv -f "$TMP_JSON" "$STATUS_FILE" \
  || { echo "release-notes: WARN could not update $STATUS_FILE"; rm -f "$TMP_JSON"; }

# 4) CHANGELOG.md — idempotent insert under '## [Unreleased]'
# (section id = slug; display title goes on the summary line)
if [[ -f "$CHANGELOG_FILE" ]] && ! grep -q "^### Sprint $SLUG — " "$CHANGELOG_FILE"; then
  TMP_CHLOG="$(mktemp)"
  BLOCK_FILE="$(mktemp)"
  {
    printf '### Sprint %s — %s\n\n' "$SLUG" "$(date -u '+%d.%m.%Y')"
    [[ "$TITLE" != "$SLUG" ]] && printf '%s\n\n' "**$TITLE.**"
    awk '/^## Tasks/{f=1;next} /^## /{f=0} f' "$PROMPT_FILE" \
      | sed -nE 's/^[[:space:]]*[0-9]+\.[[:space:]]+\*\*([^*]+)\*\*.*/- \1/p'
    printf '\n**Commits since previous sprint tag** (%s):\n' "$COMMIT_COUNT"
    git log --max-count=40 --format='- %h %s' "$RANGE" 2>/dev/null
    printf '\n'
  } > "$BLOCK_FILE"
  awk -v bf="$BLOCK_FILE" '
    { print }
    !done && /^## \[Unreleased\][[:space:]]*$/ {
      while ((getline line < bf) > 0) print line
      close(bf)
      done = 1
    }' "$CHANGELOG_FILE" > "$TMP_CHLOG" && mv -f "$TMP_CHLOG" "$CHANGELOG_FILE" \
    || echo "release-notes: WARN CHANGELOG update failed for $SLUG"
  rm -f "$TMP_CHLOG" "$BLOCK_FILE"
fi

# 5) tag
if ! git rev-parse --verify -q "refs/tags/$TAG_NAME" >/dev/null; then
  git tag -a "$TAG_NAME" -m "sprint $SLUG — $COMMIT_COUNT commits" 2>/dev/null \
    || echo "release-notes: WARN tag $TAG_NAME not created"
fi

echo "release-notes: $SLUG → status.json + CHANGELOG.md + tag $TAG_NAME (${COMMIT_COUNT} commits)"
