#!/usr/bin/env bash
# One-command install for the Pomodoro showcase plugin (slice of #1434).
# Copies the single-file CS-source plugin into the Harbor plugins directory.
# The separate harbor-plugins-host process compiles it and serves the tool over
# MCP stdio, so the AOT CLI never loads Roslyn (out-of-proc, AOT-compatible).
# No new axes: the existing tool axis only (#555 freeze).
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/PomodoroPlugin.cs"
DEST_DIR="${HARBOR_PLUGINS_DIR:-$HOME/.harbor/plugins}"
DEST="$DEST_DIR/pomodoro.cs"

mkdir -p "$DEST_DIR"
cp "$SRC" "$DEST"
echo "Installed pomodoro.cs -> $DEST"
echo "Restart Harbor to load it (the out-of-proc plugin host compiles it at startup)."
