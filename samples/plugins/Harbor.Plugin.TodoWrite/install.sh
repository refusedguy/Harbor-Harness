#!/usr/bin/env bash
# One-command install for the TodoWrite showcase plugin (slice-1, #1434).
# Copies the single-file CS-source plugin into the Harbor plugins directory.
# No new axes: tools + ITuiPanelPlugin only.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/TodoWritePlugin.cs"
DEST_DIR="${HARBOR_PLUGINS_DIR:-$HOME/.harbor/plugins}"
DEST="$DEST_DIR/todowrite.cs"

mkdir -p "$DEST_DIR"
cp "$SRC" "$DEST"
echo "Installed todowrite.cs -> $DEST"
echo "Restart Harbor to load it (plugins compile at startup via Roslyn)."
