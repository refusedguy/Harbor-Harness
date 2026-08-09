#!/usr/bin/env bash
# tools/code-inspect.sh — Post-change inspection gate for Harbor.
#
# Runs the minimal set of build + test commands that must stay green after
# any UI / Avalonia / XAML / style change.
#
# Usage:
#   ./tools/code-inspect.sh
#   ./tools/code-inspect.sh --category E2E
#   ./tools/code-inspect.sh --project tests/Harbor.App.Avalonia.Tests
#
# Exit codes:
#   0  all commands passed
#   1  build or test failure
set -euo pipefail

RED='\033[0;31m'
GRN='\033[0;32m'
NC='\033[0m'

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." &> /dev/null && pwd)"

DOTNET="${DOTNET:-dotnet}"
SOLUTION="$REPO_ROOT/Harbor.slnx"

# TUnit uses --treenode-filter (NOT --filter) per project memory.
UI_PROJECT="tests/Harbor.App.Avalonia.Tests"
ARCH_PROJECT="tests/Harbor.Architecture.Tests"

category="${1:-}"

fail() { printf "${RED}[FAIL]${NC} %s\n" "$*"; }
pass() { printf "${GRN}[PASS]${NC} %s\n" "$*"; }

# ── 1. dotnet build ──────────────────────────────────────────────────────────
printf "\n== Build ==\n"
if "$DOTNET" build "$SOLUTION" -nologo -clp:NoSummary; then
    pass "Build succeeded."
else
    fail "Build failed."
    exit 1
fi

# ── 2. Architecture tests ────────────────────────────────────────────────────
printf "\n== Architecture Tests ==\n"
if "$DOTNET" test "$ARCH_PROJECT" --nologo --no-build -c Release \
    --logger "console;verbosity=detailed"; then
    pass "Architecture tests passed."
else
    fail "Architecture tests failed."
    exit 1
fi

# ── 3. UI tests ──────────────────────────────────────────────────────────────
if [ -n "$category" ]; then
    printf "\n== UI Tests (Category=%s) ==\n" "$category"
    if "$DOTNET" test "$UI_PROJECT" --nologo --no-build -c Release \
        --logger "console;verbosity=detailed" \
        --filter "Category=$category"; then
        pass "UI tests (Category=$category) passed."
    else
        fail "UI tests (Category=$category) failed."
        exit 1
    fi
else
    printf "\n== UI Tests (all) ==\n"
    if "$DOTNET" test "$UI_PROJECT" --nologo --no-build -c Release \
        --logger "console;verbosity=detailed"; then
        pass "UI tests passed."
    else
        fail "UI tests failed."
        exit 1
    fi
fi

printf "\n== All inspections passed ==\n"
exit 0
