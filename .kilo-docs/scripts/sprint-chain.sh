#!/bin/bash
# sprint-chain.sh — автономная цепочка спринтов Harbor через `kilo run` (headless).
# Берёт очередь промпт-файлов, гонит ПОСЛЕДОВАТЕЛЬНО. Между спринтами не спрашивает.
# Стартует следующий только когда предыдущий зафиксировал прогресс
# (head ушёл от базы спринта + дерево чисто + кило нет).
#
# Промпты лежат НА ДИСКЕ (не tmpfs): ~/Projects/Harbor-Harness-Analysis/agent-prompts/
# Переживают ребут, не жрут RAM.
set -u
REPO=/mnt/projects/Harbor-Harness
MODEL="openrouter/z-ai/glm-5.3-flash"
PROMPTS_DIR="/home/nbook/Projects/Harbor-Harness-Analysis/agent-prompts"
LOG="$PROMPTS_DIR/logs/sprint-chain.log"
KILO_BIN="/mnt/pnpm-cache/global/v11/4d5e6-1a027436ccc-675e5184cc113fc8/node_modules/@kilocode/cli/bin/kilo"

mkdir -p "$PROMPTS_DIR/logs"

# Очередь (на диске). CE-5 первым — если текущий бэкграунд упадёт, цепочка добьёт.
DEFAULT_QUEUE="$PROMPTS_DIR/kilo-prompt-ce5.md $PROMPTS_DIR/kilo-prompt-prod-ui-0.md $PROMPTS_DIR/kilo-prompt-docs-zero.md"
QUEUE="${1:-$DEFAULT_QUEUE}"

log(){ echo "[$(date '+%d.%m %H:%M')] $*" >> "$LOG"; }

export PATH="/mnt/pnpm-cache/global/v11/4d5e6-1a027436ccc-675e5184cc113fc8/node_modules/@kilocode/cli/bin:$PATH"
export KILO_SERVER_PASSWORD="${KILO_SERVER_PASSWORD:-324235}"
export KILO_SERVER_USERNAME=kilo

log "=== CHAIN START queue=$QUEUE model=$MODEL ==="

for pf in $QUEUE; do
  if [ ! -f "$pf" ]; then
    log "SKIP (нет файла) $pf"; continue
  fi
  # ждём пока дерево чистое и кило не жив (предыдущий спринт доехал)
  for i in $(seq 1 120); do  # до 10ч ожидания
    kn=$(pgrep -fc '[k]ilo run' 2>/dev/null); kn=${kn:-0}
    dirty=$(git -C "$REPO" status --porcelain 2>/dev/null | grep -vc '.nuke/')
    if [ "$kn" -eq 0 ] && [ "$dirty" -eq 0 ]; then break; fi
    sleep 300
  done

  log ">>> KICK $pf"
  t0=$(date +%s)
  kilo run "$(cat "$pf")" --auto -m "$MODEL" >> "$PROMPTS_DIR/logs/chain-$(basename "$pf" .md).log" 2>&1
  rc=$?
  dur=$(( $(date +%s) - t0 ))
  log "<<< DONE $pf rc=$rc dur=${dur}s head=$(git -C "$REPO" rev-parse --short HEAD)"

  if [ "$rc" -ne 0 ]; then
    log "ALERT: спринт $pf не завершён (rc=$rc)"
    echo "CHAIN-PAUSE: $pf rc=$rc" >> "$LOG"
    exit 1
  fi
  # пауза чтобы кило точно вышел и дерево осело
  sleep 60
done

log "=== CHAIN DONE ==="
echo "CHAIN-DONE" >> "$LOG"
