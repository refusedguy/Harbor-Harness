#!/usr/bin/env bash
set -euo pipefail

REPO="${1:-.}"
cd "$REPO"

CHAIN_FILE=".kilo-docs/sprint-chain.md"
DISPATCH="$HOME/.hermes/skills/autonomous-ai-agents/kilo-dispatch/scripts/kilo-dispatch.sh"
BASE_SHA="$(git rev-parse HEAD)"

if [[ ! -f "$CHAIN_FILE" ]]; then
  echo "[chain] $CHAIN_FILE not found"
  exit 1
fi

while IFS= read -r line; do
  # пропуск пустых строк и комментариев
  [[ -z "$line" || "$line" =~ ^# ]] && continue

  if [[ "$line" =~ ^sprint\|([^|]+)\|([^|]+)\|(.+)$ ]]; then
    SPRINT="${BASH_REMATCH[1]}"
    MODEL="${BASH_REMATCH[2]}"
    PROMPT="${BASH_REMATCH[3]}"
  else
    continue
  fi

  if [[ -z "$SPRINT" || -z "$MODEL" || -z "$PROMPT" ]]; then
    echo "[chain] malformed line: $line"
    continue
  fi

  echo "[chain] === starting sprint: $SPRINT (model=$MODEL, prompt=$PROMPT) ==="
  bash "$DISPATCH" -f "$PROMPT" -m "$MODEL" -r "$(pwd)" -b "$BASE_SHA" -M 360 -i 300
  RC=$?

  # проверка прогресса
  NEW_SHA="$(git rev-parse HEAD)"
  if [[ "$NEW_SHA" != "$BASE_SHA" ]]; then
    COMMITS="$(git rev-list --count "$BASE_SHA"..HEAD)"
    echo "[chain] $SPRINT finished, progress: $COMMITS commits (new base=$NEW_SHA)"
    BASE_SHA="$NEW_SHA"
  else
    echo "[chain] $SPRINT finished, NO progress, chain stopped"
    exit $RC
  fi
done < "$CHAIN_FILE"

echo "[chain] all sprints done"
