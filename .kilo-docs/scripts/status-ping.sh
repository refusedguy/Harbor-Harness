#!/usr/bin/env bash
set -uo pipefail
set +m

REPO="${1:-.}"
cd "$REPO"

SPRINTS_DIR=".kilo-docs/sprints"
STATUS_LINE=""

for status_file in "$SPRINTS_DIR"/*/status.json; do
  [[ -f "$status_file" ]] || continue
  name=$(jq -r '.name' "$status_file")
  state=$(jq -r '.state' "$status_file")
  commits=$(jq -r '.commits' "$status_file")
  
  if [[ -n "$STATUS_LINE" ]]; then
    STATUS_LINE="$STATUS_LINE "
  fi
  STATUS_LINE="${STATUS_LINE}${name}=${state}|${commits}c"
done

# output single line for cron job
echo "sprint=$STATUS_LINE head=$(git rev-parse --short HEAD) dirty=$(git status --porcelain | grep -c -v -E "^\.nuke/" || true )"
