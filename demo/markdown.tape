# Streaming markdown demo — the agent's markdown reply (headings, fenced code,
# ordered list) rendered through the canonical CellForge cell-diff renderer, where
# the stream coalescer reveals completed source lines into the timeline block.
#
# Local equivalent:      vhs demo/markdown.tape
# Scripted run:          demo/run-scene.sh markdown cellforge
# CLI equivalent:        ./apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli \
#                          --demo --scene markdown --tui cellforge --chunk-delay 0
# CI regenerates all tapes: .github/workflows/demo.yml (pinned toolchain in
# demo/toolchain.lock; every GIF is frame-normalised by tools/demo_repro.py).
#
# Determinism: the cell-diff backend owns the alternate screen, so there is no
# output pipe to scrub or pace. The mock LLM streams every delta in one burst
# (--chunk-delay 0) and the playback holds each painted frame for
# DEMO_CELLFORGE_STEP_MS (a whole number of 1/GIF_FPS sampling intervals), so the
# recorded screen sequence is a function of the reply text alone.
Output "assets/demo/markdown-raw.gif"

Set FontFamily "JetBrains Mono"
Set FontSize 17
Set Width 1200
Set Height 620
Set Framerate 12
Set Padding 24
Set Shell bash
Set Theme { "name": "Catppuccin Mocha", "black": "#45475a", "red": "#f38ba8", "green": "#a6e3a1", "yellow": "#f9e2af", "blue": "#89b4fa", "magenta": "#f5c2e7", "cyan": "#94e2d5", "white": "#bac2de", "brightBlack": "#585b70", "brightRed": "#f38ba8", "brightGreen": "#a6e3a1", "brightYellow": "#f9e2af", "brightBlue": "#89b4fa", "brightMagenta": "#f5c2e7", "brightCyan": "#94e2d5", "brightWhite": "#a8adc8", "background": "#1e1e2e", "foreground": "#cdd6f4", "selection": "#585b70", "cursor": "#f5e0dc" }

Env HARBOR_LOGLEVEL "Warning"

Hide
Type "demo/run-scene.sh markdown cellforge"
Enter
Show

# The Sleep has to cover the whole run — the CLI boots for ~7 s and the cell-diff
# playback then holds a fixed number of painted frames. A tail that is too short
# does not merely look worse: the recorder stops mid-scene and the GIF is silently
# missing most of the output. DEMO_TAPE_TAIL_SECONDS in the lock.
Sleep 34s
