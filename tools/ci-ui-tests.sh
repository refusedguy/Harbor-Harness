#!/usr/bin/env bash
# tools/ci-ui-tests.sh — Scoped CI runs for Harbor.App.Avalonia UI tests.
#
# Runs only the four Avalonia test classes that exercise component behaviour,
# view inflation, killer features, and theme parity.  Uses TUnit's
# --treenode-filter (NOT --filter) per project memory.
#
# Usage:
#   ./tools/ci-ui-tests.sh
#   ./tools/ci-ui-tests.sh --no-build
#
# Exit codes:
#   0  all test runs passed
#   1  one or more test runs failed
set -euo pipefail

RED='\033[0;31m'
GRN='\033[0;32m'
YEL='\033[1;33m'
NC='\033[0m'

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." &> /dev/null && pwd)"

DOTNET="${DOTNET:-dotnet}"
PROJECT="tests/Harbor.App.Avalonia.Tests"
SOLUTION="$REPO_ROOT/Harbor.slnx"
NO_BUILD=""
BUILD_ARGS=()

if [ "${1:-}" = "--no-build" ]; then
    NO_BUILD="--no-build"
    BUILD_ARGS=(--no-build)
fi

fail() { printf "${RED}[FAIL]${NC} %s\n" "$*"; }
pass() { printf "${GRN}[PASS]${NC} %s\n" "$*"; }
warn() { printf "${YEL}[warn]${NC} %s\n" "$*"; }

# ── 1. Build (unless --no-build) ────────────────────────────────────────────
if [ -z "$NO_BUILD" ]; then
    printf "\n== Build ==\n"
    if "$DOTNET" build "$SOLUTION" -nologo -clp:NoSummary; then
        pass "Build succeeded."
    else
        fail "Build failed."
        exit 1
    fi
fi

# ── 2. Scoped UI test runs ──────────────────────────────────────────────────
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
        --nologo "${BUILD_ARGS[@]}" \
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
    printf "${RED}One or more scoped UI test runs failed.${NC}\n"
    exit 1
else
    printf "${GRN}All scoped UI test runs passed.${NC}\n"
    exit 0
fi
