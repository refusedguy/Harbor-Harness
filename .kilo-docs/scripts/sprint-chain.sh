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
  else
    echo "[chain] $(date '+%d.%m %H:%M') $SPRINT finished, no new commits (may already be done), continuing chain"
  fi

  # не останавливаем цепочку на отсутствии прогресса — идём к следующему спринту
done < "$CHAIN_FILE"

# финальный пуш всего прогресса + обновление PR #2
if command -v gh >/dev/null 2>&1; then
  echo "[chain] $(date '+%d.%m %H:%M') final push + PR update"
  git -C "$REPO" push origin dev 2>&1 | tee -a "$CHAIN_FILE.log" || true
  PR_BODY="$(gh pr view 2 --json body -q .body 2>/dev/null || echo '')"
  if [[ -n "$PR_BODY" ]]; then
    APPEND="### Спринт chain finished ($(date '+%d.%m %Y'))\n- Все спринты очереди завершены\n- HEAD: $(git -C "$REPO" rev-parse --short HEAD)\n"
    printf "%s\n%s" "$PR_BODY" "$APPEND" | gh pr edit 2 --body-file - 2>&1 | tee -a "$CHAIN_FILE.log" || true
  fi
  echo "[chain] $(date '+%d.%m %H:%M') all sprints done"
fi
