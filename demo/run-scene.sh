#!/usr/bin/env bash
# demo/run-scene.sh — the scripted HARBOR_DEMO run every tape records.
#
#   demo/run-scene.sh <hero|markdown|approval|all> [ansi|plain|cellforge] [-- extra CLI args]
#
# Why a wrapper instead of `Type "./Harbor.App.Cli --demo --scene hero --tui ansi"`
# straight into the tape (issue #426):
#
#   1. No credentials. Every `*_API_KEY` in the environment is unset before the
#      CLI starts, so a tape that "accidentally" reached a live provider fails
#      loudly instead of quietly producing a model-dependent recording. The
#      scripted path needs none: `harbor --demo` boots its own in-process mock
#      LLM (apps/Harbor.App.Cli/Demo/DemoLlmServer.cs) and a throw-away HOME.
#   2. No wall-clock in the captured pixels. Two tokens in the demo's own output
#      are nondeterministic — the throw-away HOME path (a fresh GUID per run) and
#      `✔ scene complete in 5.2s` (a stopwatch). Both are scrubbed here so the
#      recorded frames are a function of the output *content* only. Verified: two
#      consecutive runs of every scene are byte-identical after this filter.
#   3. A stable environment. TZ/LANG/LC_ALL/TERM are pinned so number and date
#      formatting cannot depend on the runner image, and the demo HOME is a fixed
#      path under TMPDIR so a leftover directory from a crashed run cannot change
#      what the next run renders.
#   4. Frame-locked pacing. VHS samples the terminal on a wall-clock cadence, so a
#      burst of output lands in the frames at a random offset — two recordings of
#      the same tape then differ even though the output is identical. Each line is
#      therefore held for DEMO_LINE_HOLD seconds (a few whole 1/GIF_FPS sampling
#      intervals), which guarantees every screen is sampled on every pass. Measured
#      on the hero tape: byte-identical output at 0.25 s per line; drifting frames
#      at 0.167 s and with no pacing at all.
#
# The `cellforge` backend is the exception to points 2 and 4, and the exception is
# structural rather than a hole in the guarantees (issue #440). CellForge is a
# cell-diff renderer: it takes over the alternate screen buffer and paints whole
# frames through one backend write, so
#
#   * stdout must stay the tty. A pipe here would make the renderer measure a
#     fallback 80x24 grid instead of the recorded window, and `scrub`/`pace`
#     would inject bytes straight into the middle of a painted frame. The two
#     nondeterministic tokens therefore never reach it at all: the throw-away
#     HOME prints as `<demo-home>` and the per-scene stopwatch is not printed
#     (the cell-diff playback replays the recorded turn instead of timing it).
#   * pacing is the renderer's own: `harbor demo --tui cellforge` paints one
#     frame per step and holds it for HARBOR_DEMO_STEP_MS, a whole number of
#     1/GIF_FPS sampling intervals — the same frame-locked contract, enforced
#     where the frames are produced.
#   * HARBOR_MASCOT=off keeps the ambient cat (a wall-clock blink) out of the
#     captured pixels.
#
# The equivalent manual invocation is printed with --explain and is exactly what
# the tapes run, minus the credential scrub and the line pacing:
#
#   ./apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli \
#       --demo --scene hero --tui ansi --chunk-delay 0
set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
LOCK="$REPO_ROOT/demo/toolchain.lock"
CLI_REL="apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli"

die() { echo "run-scene: $*" >&2; exit 1; }

explain=false
args=()
for arg in "$@"; do
  if [[ "$arg" == "--explain" ]]; then explain=true; else args+=("$arg"); fi
done
set -- ${args[@]+"${args[@]}"}

scene="${1:-}"
tui="${2:-ansi}"
[[ $# -gt 0 ]] && shift
[[ $# -gt 0 ]] && shift
extra=("$@")

[[ -n "$scene" ]] || { echo "Usage: run-scene.sh <hero|markdown|approval|all> [ansi|plain|cellforge] [-- args]" >&2; exit 2; }

[[ -f "$LOCK" ]] || die "missing $LOCK"
# shellcheck disable=SC1090
set -a; . "$LOCK"; set +a
chunk_delay="${DEMO_CHUNK_DELAY_MS:-0}"
line_hold="${DEMO_LINE_HOLD:-}"

if [[ "$explain" == true ]]; then
  printf '%s --demo --scene %s --tui %s --chunk-delay %s' "$CLI_REL" "$scene" "$tui" "$chunk_delay"
  if [[ ${#extra[@]} -gt 0 ]]; then printf ' %s' "${extra[@]}"; fi
  printf '\n'
  exit 0
fi

# --- 1. strip credentials ---------------------------------------------------
while IFS= read -r name; do
  [[ -n "$name" ]] || continue
  unset "$name"
done < <(compgen -e | grep -E '_API_KEY$' || true)
# The mock provider uses a synthetic key name; make sure no real one survives.
unset ANTHROPIC_API_KEY OPENAI_API_KEY KILO_API_KEY OPENROUTER_API_KEY DEEPSEEK_API_KEY \
      GROQ_API_KEY MISTRAL_API_KEY XAI_API_KEY TOGETHER_API_KEY FIREWORKS_API_KEY \
      CEREBRAS_API_KEY DEMO_API_KEY

# --- 2. stable environment --------------------------------------------------
export TZ=UTC
export LANG=C.UTF-8
export LC_ALL=C.UTF-8
export TERM="${TERM:-xterm-256color}"
export HARBOR_DEMO=1
export HARBOR_LOGLEVEL="${HARBOR_LOGLEVEL:-Warning}"
export HARBOR_SKIP_ONBOARDING=1
# The ambient cat blinks on a wall-clock cycle (MascotDirector reads
# Environment.TickCount64), which is exactly the kind of asynchronous motion the
# line renderer hides behind the held-output pacing. The cell-diff renderer has no
# such hiding place — its frames are the recording — so the documented kill-switch
# is pinned here for every backend.
export HARBOR_MASCOT=off
# Prompt is pinned so the recording never captures a cwd/hostname/branch line.
export PS1="${DEMO_PROMPT:-harbor-demo\$ }"
# Same fixed throw-away HOME every run: `harbor demo` overrides HOME itself, but
# the value is echoed in the banner, and a fresh GUID there is pure drift.
export TMPDIR="${TMPDIR:-/tmp}"
unset COLUMNS LINES

# The block cursor blinks on its own ~0.5 s cycle, asynchronous to the recorder, so
# two passes otherwise differ by exactly the cursor cell and nothing else (verified
# by differencing two passes: the text regions are pixel-identical). `tput cnorm`
# would print a visible command; writing the escape straight to the terminal does
# not, and costs the GIF nothing. Emitted before the demo starts so the very first
# captured screen is already cursorless.
printf '\033[?25l' > /dev/tty 2>/dev/null || printf '\033[?25l'

cli="$REPO_ROOT/$CLI_REL"
[[ -x "$cli" ]] || die "$CLI_REL is missing — run: dotnet build apps/Harbor.App.Cli -c Release"

# --- 3. scrub the two nondeterministic tokens, then hold each line -------------
#   - the throw-away demo HOME: /tmp/harbor-demo-<32 hex GUID>
#   - the per-scene stopwatch:     "scene complete in 5.2s"
# The scene text is fixed, so after scrubbing + pacing the observable screens are a
# pure function of that text rather than of the machine's speed.
#
# `scrub` and `pace` are separate functions so a line is printed *before* the hold:
# the hold has to start at the moment the line reaches the terminal, or the sampler
# offset simply moves instead of being locked.
scrub() {
  # -u is load-bearing: sed block-buffers when its stdout is a pipe, so without it
  # the whole scene would sit in sed's 4 KiB buffer and reach the terminal only
  # after the demo exited — i.e. after the recorder had already given up.
  sed -u -E \
    -e 's#/tmp/harbor-demo-[0-9a-f]{8,}#<demo-home>#g' \
    -e 's#scene complete in [0-9]+([.,][0-9]+)? ?s#scene complete in <t>#g'
}

pace() { # pace: one line, flush, then hold for a whole number of frame intervals
  local line
  while IFS= read -r line; do
    printf '%s\n' "$line"
    sleep "$line_hold"
  done
}

set -o pipefail
if [[ "$tui" == "cellforge" ]]; then
  # --- 3b. cell-diff playback: no pipe, no scrub, no pace ------------------
  # The renderer owns the alternate screen buffer and ships each frame through
  # one backend write, so nothing may sit between it and the terminal: a pipe
  # would replace the measured window with the 80x24 fallback grid and
  # `scrub`/`pace` would write into the middle of a painted frame. Both
  # nondeterministic tokens are therefore never produced (the HOME prints as
  # `<demo-home>`, the stopwatch is not printed), and the pacing moves *inside*
  # the renderer, which holds every painted frame for HARBOR_DEMO_STEP_MS — a
  # whole number of 1/GIF_FPS sampling intervals, exactly like `pace` above.
  #
  # HARBOR_DEMO_STEP_MS comes from the lock next to GIF_FPS, so the two cannot
  # drift apart: a step shorter than two sampling intervals would let a screen
  # slip between two captures and the record-twice check would then compare
  # different screen sequences.
  export HARBOR_DEMO_STEP_MS="${DEMO_CELLFORGE_STEP_MS:-250}"
  set +e
  "$cli" --demo --scene "$scene" --tui "$tui" --chunk-delay "$chunk_delay" "${extra[@]+"${extra[@]}"}"
  status=$?
  set -e
  # The shell prompt the recorder's terminal prints once this script returns
  # would repaint the restored console mid-tail, and where it lands is a race
  # against the sampler — one pass would show it, the next would not. Holding
  # past the tape's trailing Sleep means the prompt can only ever arrive after
  # the recorder has stopped. Costs nothing: by then the demo has painted
  # everything it is going to paint.
  sleep "${DEMO_CELLFORGE_EXIT_HOLD:-$(( ${DEMO_TAPE_TAIL_SECONDS:-34} + 30 ))}"
elif [[ -n "$line_hold" ]]; then
  "$cli" --demo --scene "$scene" --tui "$tui" --chunk-delay "$chunk_delay" "${extra[@]+"${extra[@]}"}" \
    | scrub | pace
  status=$?
else
  "$cli" --demo --scene "$scene" --tui "$tui" --chunk-delay "$chunk_delay" "${extra[@]+"${extra[@]}"}" \
    | scrub
  status=$?
fi

# If the CLI died, the pipeline's exit status would mask it under `set -e`; report both.
if [[ $status -ne 0 ]]; then
  echo "run-scene: demo exited with status $status" >&2
  exit $status
fi
