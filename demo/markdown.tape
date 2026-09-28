# Streaming markdown demo — the agent's markdown reply (headings, fenced code,
# ordered list) rendered through the ansi renderer.
#
# Local equivalent:      vhs demo/markdown.tape
# Scripted run:          demo/run-scene.sh markdown ansi
# CLI equivalent:        ./apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli \
#                          --demo --scene markdown --tui ansi --chunk-delay 0
# CI regenerates all tapes: .github/workflows/demo.yml (pinned toolchain in
# demo/toolchain.lock; frame-normalised by tools/demo_repro.py).
#
# Determinism: the mock LLM streams the deltas in one burst (--chunk-delay 0) so
# the captured frames are a function of the reply text, not of the sampler's
# timing. run-scene.sh strips credentials, the temp-HOME GUID and the stopwatch.
Output "assets/demo/markdown-raw.gif"

Set FontFamily "JetBrains Mono"
Set FontSize 17
Set Width 1200
Set Height 620
Set Framerate 12
Set Padding 24
Set Shell bash
Set Theme { "name": "Catppuccin Mocha", "black": "#45475a", "red": "#f38ba8", "green": "#a6e3a1", "yellow": "#f9e2af", "blue": "#89b4fa", "magenta": "#f5c2e7", "cyan": "#94e2d5", "white": "#bac2de", "brightBlack": "#585b70", "brightRed": "#f38ba8", "brightGreen": "#a6e3a1", "brightYellow": "#f9e2af", "brightBlue": "#89b4fa", "brightMagenta": "#f5c2e7", "brightCyan": "#94e2d5", "brightWhite": "#a6adc8", "background": "#1e1e2e", "foreground": "#cdd6f4", "selection": "#585b70", "cursor": "#f5e0dc" }

Env HARBOR_LOGLEVEL "Warning"

Hide
Type "demo/run-scene.sh markdown ansi"
Enter
Show

# The Sleep has to cover the whole run — the CLI boots for ~7 s and the line
# pacing in demo/run-scene.sh stretches the scene itself. A tail that is too
# short does not merely look worse: the recorder stops mid-scene and the GIF is
# silently missing most of the output. DEMO_TAPE_TAIL_SECONDS in the lock.
Sleep 20s
