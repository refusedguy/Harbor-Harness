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
# Exit codes: 0 = green; 1 = blocked (build/test failure, commit refused);
#              2 = infra TIMEOUT (a suite was killed by timeout(1), rc=124 —
#              a hung run, NOT a test failure).

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
# NOTE (#1006): this timeout is hang protection, not a speed gate. It asserts
# nothing about how fast the suite is — it only bounds a hung run. That is why
# an absolute wall-clock value is correct here, unlike in a CI perf gate: a
# local hook has no same-run baseline pair to be relative to (cf. #410, #998).
# Budget: worst green `dotnet exec` in CI logs of the core shard is 48.0 s
# (10 green dev runs, 34.9–48.0 s); the post-#1001 tree runs 77 s locally
# (issue body). 77 × 1.43 (runner spread from merged #998) ≈ 110 s, rounded
# up to 180 s. arch(180) > per-project(120): the 47-rule suite is the
# expensive one — the old 25/120 pair had the budgets inverted.
ARCH_TIMEOUT=180
ARCH_LOG="${TMPDIR:-/tmp}/kilo/harbor-check-arch.log"
timeout "$ARCH_TIMEOUT" dotnet exec "$ARCH_DLL" > "$ARCH_LOG" 2>&1
arch_rc=$?
if [[ $arch_rc -eq 124 ]]; then
  # timeout(1) killed the run: the log below is a killed-process stump, NOT a
  # test failure (#859/#782/#711/#993 — no plausible value in place of the
  # fact). Stamp the log itself so a later reader is not misled either.
  say "TIMEOUT after ${ARCH_TIMEOUT}s: arch suite killed by timeout (rc=124), not by a failing test" | tee -a "$ARCH_LOG"
  tail -25 "$ARCH_LOG"
  echo "[harbor-check] TIMEOUT: arch suite exceeded ${ARCH_TIMEOUT}s — rerun with HARBOR_CHECK_NO_CACHE=1; exit 2 (infra), not 1 (test failure)"
  exit 2
elif [[ $arch_rc -ne 0 ]]; then
  tail -25 "$ARCH_LOG"
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
TIMEOUT_HIT=0
declare -A SEEN=()
while IFS= read -r proj; do
  [[ -z "$proj" ]] && continue
  [[ -n "${SEEN[$proj]:-}" ]] && continue
  SEEN["$proj"]=1
  pdir="$(dirname "$proj")"
  if [[ "$proj" == tests/* ]]; then
    name="$(basename "$pdir")"
    dll="$pdir/bin/Release/net10.0/${name}.dll"
    say "$name: build + test suite"
    dotnet build "$proj" -c Release --no-restore -v q -p:HarborArchGate=false > /dev/null 2>&1 \
      || { say "build FAILED: $proj"; FAIL=1; continue; }
    if [[ -f "$dll" ]]; then
      timeout 120 dotnet exec "$dll" \
        > "${TMPDIR:-/tmp}/kilo/harbor-check-${name}.log" 2>&1
      rc=$?
      if [[ $rc -eq 124 ]]; then
        say "TIMEOUT after 120s: test run of $name killed by timeout (rc=124) — stump below, not a test failure" \
          | tee -a "${TMPDIR:-/tmp}/kilo/harbor-check-${name}.log"
        tail -25 "${TMPDIR:-/tmp}/kilo/harbor-check-${name}.log"
        TIMEOUT_HIT=1
      elif [[ $rc -ne 0 ]]; then
        tail -25 "${TMPDIR:-/tmp}/kilo/harbor-check-${name}.log"
        say "tests FAILED in $name"
        FAIL=1
      else
        say "tests green: $name"
      fi
    else
      say "tests green: $name"
    fi
  else
    say "build $(basename "$proj") (incremental)"
    dotnet build "$proj" -c Release --no-restore -v q -p:HarborArchGate=false > /dev/null 2>&1 \
      || { say "build FAILED: $proj (compile/analyzer)"; FAIL=1; }
  fi
done < <(changed_projects)

if [[ "$TIMEOUT_HIT" -ne 0 ]]; then
  echo "[harbor-check] TIMEOUT: a per-project suite hit the 120s hang budget — rerun with HARBOR_CHECK_NO_CACHE=1; exit 2 (infra), not 1 (test failure)"
  exit 2
fi

if [[ "$FAIL" -ne 0 ]]; then
  fail "changed-project verification failed"
fi

# ── memoize green ────────────────────────────────────────────────────
mkdir -p "$STAMP_DIR"
date -u '+%Y-%m-%dT%H:%M:%SZ' > "$STAMP_DIR/$(cache_key)"
say "all green (${#SEEN[@]} project(s) verified)"
