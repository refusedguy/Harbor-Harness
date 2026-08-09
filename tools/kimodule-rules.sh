#!/usr/bin/env bash
# tools/kimodule-rules.sh — Kimodule Style Selector Rules for Harbor.
#
# Checks XAML files for bare class selectors without element type prefix.
# Avalonia style selectors MUST be scoped to a control type:
#   Good: Selector="Border.Card"
#   Bad:  Selector=".Card"
#
# Usage:
#   ./tools/kimodule-rules.sh
#   ./tools/kimodule-rules.sh --axaml-dir apps/Harbor.App.Avalonia
#
# Exit codes:
#   0  no bare selectors found
#   1  bare selectors detected
set -euo pipefail

RED='\033[0;31m'
GRN='\033[0;32m'
YEL='\033[1;33m'
NC='\033[0m'

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." &> /dev/null && pwd)"

AXAML_DIR="${1:-$REPO_ROOT/apps/Harbor.App.Avalonia}"

info()  { printf "${GRN}[pass]${NC} %s\n" "$*"; }
warn()  { printf "${YEL}[warn]${NC} %s\n" "$*"; }
fail()  { printf "${RED}[fail]${NC} %s\n" "$*"; }

printf "== Kimodule Rules: bare class selector check ==\n"

bare_hits=$(grep -rn 'Selector="\.[A-Za-z]' "$AXAML_DIR" --include="*.axaml" 2>/dev/null || true)
if [ -z "$bare_hits" ]; then
    info "No bare class selectors found."
    exit 0
else
    fail "Bare class selectors detected (must be ElementType.ClassName):"
    echo "$bare_hits" | while IFS= read -r line; do
        printf "       %s\n" "$line"
    done
    exit 1
fi
