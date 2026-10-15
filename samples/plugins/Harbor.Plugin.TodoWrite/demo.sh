#!/usr/bin/env bash
# 60-second demo for the TodoWrite showcase plugin (slice-1, #1434).
# Hermetic: no dotnet, no network, no running Harbor instance.
# Prints the exact commands a user runs; each step is instant.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
START=$SECONDS

step() { printf '\n==> %s\n' "$1"; }

step "1/5 install (one command)"
bash "$HERE/install.sh"

step "2/5 add two todos (harbor ask)"
echo 'harbor> ask "Add todo: write showcase docs, then add todo: record demo"'
echo '[tool_execution_end] tool=todo action=add ok=true (x2)'

step "3/5 list this session"
echo 'harbor> ask "List my todos"'
echo '[tool_execution_end] tool=todo action=list ok=true'
echo 'Todos (2): [ ] ... write showcase docs / record demo'

step "4/5 cross-session search"
echo 'harbor> ask "Search todos for demo across sessions"'
echo '[tool_execution_end] tool=todo action=search ok=true'
echo "Matches for 'demo' (session <id>): [ ] ... record demo"

step "5/5 todo-list panel (CellForge, Right dock)"
grep -q 'class TodoListPanel' "$HERE/TodoWritePlugin.cs" \
  && echo "panel registered: todo-list (Right, 30) via ITuiPanelPlugin"
grep -q 'todos.json' "$HERE/TodoWritePlugin.cs" \
  && echo "persistence: <DataDirectory>/todos.json (survives restarts)"

echo ""
echo "demo done in $((SECONDS - START))s (budget 60s)"
