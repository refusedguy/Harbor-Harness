# Harbor hero demo — scripted agent run through the canonical CellForge renderer
# (fullscreen cell-diff; mock LLM, no API keys). This is the GIF the README leads
# with, so it has to show what a user actually gets.
#
# Local equivalent:      vhs demo/hero.tape
# Scripted run:          demo/run-scene.sh hero cellforge
# CLI equivalent:        ./apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli \
#                          --demo --scene hero --tui cellforge --chunk-delay 0
# CI regenerates all tapes: .github/workflows/demo.yml (pinned toolchain in
# demo/toolchain.lock; every GIF is frame-normalised by tools/demo_repro.py, so
# the frame count and duration are identical on every run).
#
# Determinism: run-scene.sh unsets every *_API_KEY, pins TZ/LANG/LC_ALL/PS1 and
# HARBOR_MASCOT=off. The cell-diff renderer owns the alternate screen, so there is
# no output pipe to scrub or pace: the throw-away HOME prints as `<demo-home>`, the
# per-scene stopwatch is not printed at all, and the playback itself holds every
# painted frame for DEMO_CELLFORGE_STEP_MS (a whole number of 1/GIF_FPS sampling
# intervals) while replaying the recorded turn in fixed chunks. --chunk-delay 0
# makes the mock LLM emit all deltas in one burst, so the recorded turn is a
# function of the reply text, not of when the sampler fired.
Output "assets/demo/hero-raw.gif"

Set FontFamily "JetBrains Mono"
Set FontSize 17
Set Width 1200
Set Height 620
Set Framerate 12
Set Padding 24
Set Shell bash
Set Theme { "name": "Catppuccin Mocha", "black": "#45475a", "red": "#f38ba8", "green": "#a6e3a1", "yellow": "#f9e2af", "blue": "#89b4fa", "magenta": "#f5c2e7", "cyan": "#94e2d5", "white": "#bac2de", "brightBlack": "#585b70", "brightRed": "#f38ba8", "brightGreen": "#a6e3a1", "brightYellow": "#f9e2af", "brightBlue": "#89b4fa", "brightMagenta": "#f5c2e7", "brightCyan": "#94e2d5", "brightWhite": "#a8adc8", "background": "#1e1e2e", "foreground": "#cdd6f4", "selection": "#585b70", "cursor": "#f5e0dc" }

# Quiet logs so the recording shows only the agent output.
Env HARBOR_LOGLEVEL "Warning"

# Type off-screen; record only the running demo (lazygit style).
Hide
Type "demo/run-scene.sh hero cellforge"
Enter
Show

# The Sleep has to cover the whole run — the CLI boots for ~7 s and the cell-diff
# playback then holds a fixed number of painted frames. A tail that is too short
# does not merely look worse: the recorder stops mid-scene and the GIF is silently
# missing most of the output. DEMO_TAPE_TAIL_SECONDS in the lock.
Sleep 34s
