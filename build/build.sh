#!/usr/bin/env bash
# build.sh — NUKE bootstrap (Linux / macOS).
#
# Bootstraps the .NET SDK if missing, restores the build/_build.csproj project,
# then invokes the NUKE Build class with whatever targets / args were passed
# through. Standard NUKE-generated bootstrap script (lightly customized for
# the Harbor solution layout: .NET 10 SDK at $HOME/.dotnet, build project at
# ./build/_build.csproj, solution file at ./Harbor.slnx).
#
# Examples:
#   ./build.sh                       # default target (Compile)
#   ./build.sh Compile               # build the solution
#   ./build.sh Test                  # run all tests
#   ./build.sh PublishCliMinimal     # publish minimal CLI variant
#   ./build.sh PublishAll            # publish every artifact
#   ./build.sh Clean Compile Test    # chain targets
#   ./build.sh Compile --configuration Debug

set -euo pipefail

[ "$TRACE" = "true" ] && set -x

# ── Solution-relative paths ──────────────────────────────────────────────────
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
ROOT_DIR="$(cd -- "$SCRIPT_DIR/.." &> /dev/null && pwd)"
BUILD_PROJECT_FILE="$ROOT_DIR/build/_build.csproj"
SOLUTION_FILE="$ROOT_DIR/Harbor.slnx"

# ── Artifacts / temp ─────────────────────────────────────────────────────────
NUKE_TEMP_DIR="$ROOT_DIR/.nuke/temp"
NUKE_BIN_DIR="$ROOT_DIR/.nuke/bin"
mkdir -p "$NUKE_TEMP_DIR"

# ── .NET SDK bootstrap ───────────────────────────────────────────────────────
# Honor the user's DOTNET_INSTALL_DIR; default to $HOME/.dotnet (matches the
# sandbox setup documented in worklog.md Task A4 / R6).
DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"
DOTNET_EXE="$DOTNET_INSTALL_DIR/dotnet"

if [ ! -x "$DOTNET_EXE" ]; then
    # Fall back to PATH-installed dotnet if the install-dir binary is missing.
    if command -v dotnet > /dev/null 2>&1; then
        DOTNET_EXE="$(command -v dotnet)"
    else
        echo "ERROR: dotnet not found at $DOTNET_EXE and not on PATH." >&2
        echo "       Install the .NET 10 SDK:  https://dot.net" >&2
        exit 1
    fi
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUKE_TELEMETRY_OPTOUT=1

# ── Build the _build project (or use cached output) ─────────────────────────
BUILD_PROJECT_FRAMEWORK="net10.0"
BUILD_PROJECT_OUTPUT="$NUKE_BIN_DIR/$BUILD_PROJECT_FRAMEWORK"

"$DOTNET_EXE" build "$BUILD_PROJECT_FILE" \
    --framework "$BUILD_PROJECT_FRAMEWORK" \
    --configuration Release \
    --output "$BUILD_PROJECT_OUTPUT" \
    -nologo -clp:NoSummary \
    "${DOTNET_BUILD_EXTRA_ARGS[@]:-}"

# ── Invoke NUKE ──────────────────────────────────────────────────────────────
"$DOTNET_EXE" exec "$BUILD_PROJECT_OUTPUT/_build.dll" "$@"
