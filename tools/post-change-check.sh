#!/usr/bin/env bash
# tools/post-change-check.sh — Post-change inspection gate for Harbor UI work.
#
# Runs the minimal set of commands that must stay green after any
# UI / Avalonia / XAML / style change:
#   1. dotnet build (with timeout)
#   2. Scoped UI tests (Component / ViewInflation / KillerFeature / ThemeParity)
#
# Usage:
#   ./tools/post-change-check.sh
#   ./tools/post-change-check.sh --timeout 300
#
# Exit codes:
#   0  all checks passed
#   1  build or test failure
set -euo pipefail

RED='\033[0;31m'
GRN='\033[0;32m'
NC='\033[0m'

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." &> /dev/null && pwd)"

DOTNET="${DOTNET:-dotnet}"
SOLUTION="$REPO_ROOT/Harbor.slnx"
PROJECT="tests/Harbor.App.Avalonia.Tests"
TIMEOUT="300"
while [ $# -gt 0 ]; do
    case "$1" in
        --timeout)
            TIMEOUT="$2"
            shift 2
            ;;
        *)
            shift
            ;;
    esac
done

fail() { printf "${RED}[FAIL]${NC} %s\n" "$*"; }
pass() { printf "${GRN}[PASS]${NC} %s\n" "$*"; }

# ── 1. dotnet build with timeout ────────────────────────────────────────────
printf "\n== Build (timeout %ss) ==\n" "$TIMEOUT"
if timeout "${TIMEOUT}s" "$DOTNET" build "$SOLUTION" -nologo -clp:NoSummary; then
    pass "Build succeeded."
else
    fail "Build failed or timed out after ${TIMEOUT}s."
    exit 1
fi

# ── 2. Scoped UI tests ──────────────────────────────────────────────────────
FILTERS=(
    "/*/*/ComponentTests/*"
    "/*/*/ViewInflationTests/*"
    "/*/*/KillerFeatureTests/*"
    "/*/*/ThemeParityTests/*"
)

overall=0

for f in "${FILTERS[@]}"; do
    printf "\n== UI Tests: %s ==\n" "$f"
    if "$DOTNET" test "$PROJECT" \
        --nologo --no-build \
        -c Release \
        --logger "console;verbosity=detailed" \
        --treenode-filter "$f"; then
        pass "Test run passed: $f"
    else
        fail "Test run FAILED: $f"
        overall=1
    fi
done

# ── Summary ─────────────────────────────────────────────────────────────────
printf "\n== Summary ==\n"
if [ "$overall" -ne 0 ]; then
    printf "${RED}Post-change check failed.${NC}\n"
    exit 1
else
    printf "${GRN}Post-change check passed.${NC}\n"
    exit 0
fi
