#!/usr/bin/env bash
# One-command install for the GitTools showcase plugin (slice-2, #1434).
# Copies the single-file CS-source plugin into the Harbor plugins directory.
# No new axes: tools + ITuiPanelPlugin only.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/GitToolsPlugin.cs"
DEST_DIR="${HARBOR_PLUGINS_DIR:-$HOME/.harbor/plugins}"
DEST="$DEST_DIR/gittools.cs"

mkdir -p "$DEST_DIR"
cp "$SRC" "$DEST"
echo "Installed gittools.cs -> $DEST"
echo "Restart Harbor to load it (plugins compile at startup via Roslyn)."
