#!/usr/bin/env bash
# harbor-check.sh — fast pre-commit verification gate (git alias `harbor-check`).
#
# Blocks the commit when:
#   1. Harbor architecture tests regress (always run — the layering law, ~3 s);
#   2. any project touched by the staged diff fails `dotnet build -c Release`
#      (compile + banned-API + analyzer gates);
#   3. a changed tests/<Project> suite fails.
#
# SPEED (< 30 s budget):
#   - only Architecture.Tests always run (direct MTP host, no MSBuild in loop);
#   - all builds are incremental (MSBuild no-ops warm in ~1 s);
#   - a green result is memoized per (HEAD + staged tree) — re-attempts of the
#     same change are instant. HARBOR_CHECK_NO_CACHE=1 to force re-verification.
#   - docs/sprint-metadata-only commits exit instantly.
#
# Usage:
#   harbor-check.sh [--staged|--all]    # default --staged (pre-commit mode)
#   git harbor-check                    # via the alias set by setup-git-hooks.sh
#
# Exit codes: 0 = green; 1 = blocked (commit refused).

set -uo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || {
  echo "[harbor-check] FAIL: not a git repo"; exit 1; }
cd "$REPO_ROOT"

MODE="${1:---staged}"
STAMP_DIR="${HARBOR_CHECK_CACHE_DIR:-/tmp/harbor-check-stamps}"
LOG_DIR="${TMPDIR:-/tmp}/kilo"
mkdir -p "$STAMP_DIR" "$LOG_DIR"

say()  { echo "[harbor-check] $*"; }
fail() { echo "[harbor-check] BLOCKED: $*"; exit 1; }

# ── changed files ────────────────────────────────────────────────────
if [[ "$MODE" == "--staged" ]]; then
  FILES="$(git diff --cached --name-only --diff-filter=d 2>/dev/null || true)"
else
  FILES="$({ git diff --name-only --diff-filter=d HEAD; git ls-files --others --exclude-standard; } 2>/dev/null)"
fi

# fast path: no build-relevant files → nothing to verify
if [[ -z "$FILES" ]] || ! grep -qE '\.(cs|csproj|props|targets|slnx|props\.json)$' <<<"$FILES"; then
  echo "[harbor-check] no build files in change — instant pass"
  exit 0
fi
FILES="$(sort -u <<<"$FILES")"

# ── green-result memo: same HEAD + same staged tree ⇒ skip re-run ────
cache_key() {
  printf '%s|%s' "$(git rev-parse HEAD 2>/dev/null || echo no-head)" "$FILES" | sha1sum | cut -d' ' -f1
}
STAMP="$STAMP_DIR/$(cache_key)"
if [[ -f "$STAMP" && "${HARBOR_CHECK_NO_CACHE:-0}" != "1" ]]; then
  echo "[harbor-check] cached green for this exact change — pass"
  exit 0
fi

# ── stage 1: architecture tests (always) ─────────────────────────────
ARCH_PROJ="tests/Harbor.Architecture.Tests"
ARCH_DLL="$ARCH_PROJ/bin/Release/net10.0/Harbor.Architecture.Tests.dll"
say "arch gate: build + run $ARCH_PROJ"
# HarborArchGate=false: the hook runs tests itself; the build-time gate would
# duplicate the run and blur compile vs test failure diagnostics.
dotnet build "$ARCH_PROJ" -c Release --no-restore -v q -p:HarborArchGate=false > /dev/null 2>&1 \
  || fail "arch tests project does not build — fix compilation first"
[[ -f "$ARCH_DLL" ]] || fail "arch test host missing: $ARCH_DLL"
if ! timeout 25 dotnet exec "$ARCH_DLL" > "${TMPDIR:-/tmp}/kilo/harbor-check-arch.log" 2>&1; then
  tail -25 "${TMPDIR:-/tmp}/kilo/harbor-check-arch.log"
  fail "architecture tests regressed (docs/ARCHITECTURE_LAYERS.md §2)"
fi
say "arch tests green (47 rules)"

# ── stage 2: touched projects — build; test projects also run their suite ──
changed_projects() {
  local f dir
  while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    if [[ "$f" == *.csproj ]]; then
      echo "$f"
      continue
    fi
    dir="$(dirname "$f")"
    while [[ "$dir" != "." ]]; do
      if compgen -G "$dir/*.csproj" > /dev/null; then
        printf '%s\n' "$dir"/*.csproj | head -1
        break
      fi
      dir="$(dirname "$dir")"
    done
  done <<< "$FILES" | sort -u
}

FAIL=0
declare -A SEEN=()
while IFS= read -r proj; do
  [[ -z "$proj" ]] && continue
  [[ -n "${SEEN[$proj]:-}" ]] && continue
  SEEN["$proj"]=1
  pdir="$(dirname "$proj")"
  if [[ "$proj" == tests/* ]]; then
    name="$(basename "$pdir")"
    say "$name: build + test suite"
    dotnet build "$proj" -c Release --no-restore -v q -p:HarborArchGate=false > /dev/null 2>&1 \
      || { say "build FAILED: $proj"; FAIL=1; continue; }
    # Run via `dotnet test` — the SAME invocation CI uses — not `dotnet exec`.
    # Rationale: raw exec skips the MTP host's dependency graph; suites that
    # lazy-load transitively-required assemblies by bare name at runtime
    # (Avalonia XAML resolver → Avalonia.Controls.ColorPicker) then fail with
    # FileNotFoundException under exec while staying green under dotnet test,
    # poisoning the rest of the run (AppBuilder double-setup cascade).
    if ! timeout 300 dotnet test "$proj" -c Release --no-build -p:HarborArchGate=false \
         > "${TMPDIR:-/tmp}/kilo/harbor-check-${name}.log" 2>&1; then
      tail -25 "${TMPDIR:-/tmp}/kilo/harbor-check-${name}.log"
      say "tests FAILED in $name"
      FAIL=1
    else
      say "tests green: $name"
    fi
  else
    say "build $(basename "$proj") (incremental)"
    dotnet build "$proj" -c Release --no-restore -v q -p:HarborArchGate=false > /dev/null 2>&1 \
      || { say "build FAILED: $proj (compile/analyzer)"; FAIL=1; }
  fi
done < <(changed_projects)

if [[ "$FAIL" -ne 0 ]]; then
  fail "changed-project verification failed"
fi

# ── memoize green ────────────────────────────────────────────────────
mkdir -p "$STAMP_DIR"
date -u '+%Y-%m-%dT%H:%M:%SZ' > "$STAMP_DIR/$(cache_key)"
say "all green (${#SEEN[@]} project(s) verified)"
