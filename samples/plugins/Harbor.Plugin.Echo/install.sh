#!/usr/bin/env bash
# One-command install for the Echo showcase plugin (slice of #1434).
# Copies the single-file CS-source plugin into the Harbor plugins directory.
# No new axes: the existing tool axis only (#555 freeze).
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/EchoPlugin.cs"
DEST_DIR="${HARBOR_PLUGINS_DIR:-$HOME/.harbor/plugins}"
DEST="$DEST_DIR/echo.cs"

mkdir -p "$DEST_DIR"
cp "$SRC" "$DEST"
echo "Installed echo.cs -> $DEST"
echo "Restart Harbor to load it (plugins compile at startup via Roslyn)."
