#!/usr/bin/env bash
# setup-git-hooks.sh — one-time activation of the Harbor verification gate.
#
#   1. core.hooksPath → .githooks/ (the committed pre-commit hook becomes active)
#   2. git alias `harbor-check` → manual full check (same gate, --all mode)
#
# Idempotent: safe to re-run (e.g. after a fresh clone).
# Usage: bash .kilo-docs/scripts/setup-git-hooks.sh
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

chmod +x .githooks/pre-commit .kilo-docs/scripts/harbor-check.sh

git config core.hooksPath .githooks
git config alias.harbor-check '!bash .kilo-docs/scripts/harbor-check.sh --all'

echo "installed: core.hooksPath=$(git config core.hooksPath)"
echo "alias harbor-check -> $(git config alias.harbor-check)"
echo "pre-commit now runs arch tests + changed-project verification (skip once: git commit --no-verify)"
