#!/usr/bin/env bash
# tools/regen-demo.sh — local equivalent of .github/workflows/demo.yml.
#
#   tools/regen-demo.sh              # record all four tapes + normalise
#   tools/regen-demo.sh hero         # one tape
#   tools/regen-demo.sh --twice      # record twice and diff (the reproducibility check)
#   tools/regen-demo.sh --check      # only run the envelope report on existing GIFs
#
# The workflow does the same three things in the same order: install the pinned
# toolchain, run the tapes, re-pace the captures. Running this locally before
# opening a PR is how you find out that a change reflowed the demo, without
# waiting for a 20-minute CI run.
#
# Requirements: vhs 0.11.0 + ttyd 1.7.7 + JetBrains Mono 2.304 (installed and
# verified by demo/install-toolchain.sh), ffmpeg, python3, and a Release build of
# the CLI:
#
#   dotnet build apps/Harbor.App.Cli -c Release
set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

TAPES=(hero markdown approval plain)
TWICE=false
CHECK_ONLY=false
SELECTED=()

die() { echo "regen-demo: $*" >&2; exit 1; }

for arg in "$@"; do
  case "$arg" in
    --twice) TWICE=true ;;
    --check) CHECK_ONLY=true ;;
    -h|--help) sed -n '2,18p' "${BASH_SOURCE[0]}"; exit 0 ;;
    -*) die "unknown option '$arg'" ;;
    *) SELECTED+=("$arg") ;;
  esac
done

if [[ ${#SELECTED[@]} -gt 0 ]]; then
  for name in "${SELECTED[@]}"; do
    found=false
    for tape in "${TAPES[@]}"; do [[ "$name" == "$tape" ]] && found=true; done
    [[ "$found" == true ]] || die "unknown tape '$name' (expected: ${TAPES[*]})"
  done
  TAPES=("${SELECTED[@]}")
fi

report() { python3 tools/demo_repro.py report --dir assets/demo; }

if [[ "$CHECK_ONLY" == true ]]; then
  report
  exit $?
fi

command -v vhs >/dev/null || die "vhs is not on PATH — run: eval \"\$(demo/install-toolchain.sh --print-path)\" && demo/install-toolchain.sh"
command -v ffmpeg >/dev/null || die "ffmpeg is required to re-pace the captures"
[[ -x apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli ]] \
  || die "the Release CLI is missing — run: dotnet build apps/Harbor.App.Cli -c Release"

record() { # record <tape> <outdir>
  local tape="$1" outdir="$2"
  mkdir -p "$outdir"
  local staged="$outdir/$tape.tape"
  sed "s#^Output \"assets/demo/$tape-raw.gif\"\$#Output \"$outdir/$tape-raw.gif\"#" \
    "demo/$tape.tape" > "$staged"
  echo "== recording demo/$tape.tape"
  vhs "$staged"
  python3 tools/demo_repro.py normalize \
    --input "$outdir/$tape-raw.gif" --output "$outdir/$tape.gif"
}

if [[ "$TWICE" == true ]]; then
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' EXIT
  failed=0
  for tape in "${TAPES[@]}"; do
    record "$tape" "$work/pass-A"
    record "$tape" "$work/pass-B"
    if ! python3 tools/demo_repro.py compare \
      --a "$work/pass-A/$tape.gif" --b "$work/pass-B/$tape.gif" --name "$tape"; then
      failed=1
    fi
  done
  [[ $failed -eq 0 ]] || die "demo playback is NOT reproducible"
  echo "Every tape produced identical output in both passes."
  exit 0
fi

for tape in "${TAPES[@]}"; do
  record "$tape" assets/demo
  cp "assets/demo/$tape.gif" "assets/demo/$tape-compressed.gif"
  rm -f "assets/demo/$tape.gif" "assets/demo/$tape-raw.gif"
done

report
echo
echo "Review with:  git diff --stat assets/demo"
echo "The GIFs are committed by CI; commit baseline.json only if the drift is intended:"
echo "  gh workflow run demo.yml -f record_baseline=true"
