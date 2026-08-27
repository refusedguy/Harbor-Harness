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
  # пропуск пустых строк и строк-примеров/комментариев
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

  # финальная проверка что это не мусор
  if [[ -z "$SPRINT" || -z "$MODEL" || -z "$PROMPT" ]]; then
    echo "[chain] malformed line: $line"
    continue
  fi

  echo "[chain] $(date '+%d.%m %H:%M') === starting sprint: $SPRINT (model=$MODEL, prompt=$PROMPT) ==="
  bash "$DISPATCH" -f "$PROMPT" -m "$MODEL" -r "$(pwd)" -b "$BASE_SHA" -M 360 -i 300
  RC=$?

  # проверка прогресса
  NEW_SHA="$(git rev-parse HEAD)"
  if [[ "$NEW_SHA" != "$BASE_SHA" ]]; then
    COMMITS="$(git rev-list --count "$BASE_SHA"..HEAD)"
    echo "[chain] $(date '+%d.%m %H:%M') $SPRINT finished, progress: $COMMITS commits (new base=$NEW_SHA)"
    BASE_SHA="$NEW_SHA"
    # авто-финализация спринта: пуш + обновление PR #2
    if command -v gh >/dev/null 2>&1; then
      echo "[chain] $(date '+%d.%m %H:%M') finalizing sprint: push + PR update"
      git -C "$REPO" push origin dev 2>&1 | tee -a "$CHAIN_FILE.log" || true
      # дописать секцию спринта в описание PR #2
      PR_BODY="$(gh pr view 2 --json body -q .body 2>/dev/null || echo '')"
      if [[ -n "$PR_BODY" ]]; then
        APPEND="### Спринт $SPRINT ($(date '+%d.%m %Y'))\n- Прогресс: $COMMITS коммитов с последнего спринта\n- HEAD: $NEW_SHA\n"
        printf "%s\n%s" "$PR_BODY" "$APPEND" | gh pr edit 2 --body-file - 2>&1 | tee -a "$CHAIN_FILE.log" || true
      fi
      echo "[chain] $(date '+%d.%m %H:%M') finalize done"
    fi
  else
    echo "[chain] $(date '+%d.%m %H:%M') $SPRINT finished, NO progress, chain stopped"
    exit $RC
  fi
done < "$CHAIN_FILE"

echo "[chain] $(date '+%d.%m %H:%M') all sprints done"
