# Harbor hero demo — scripted agent run through the ansi renderer (mock LLM, no API keys).
#
# Local equivalent:      vhs demo/hero.tape
# Scripted run:          demo/run-scene.sh hero ansi
# CLI equivalent:        ./apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli \
#                          --demo --scene hero --tui ansi --chunk-delay 0
# CI regenerates all tapes: .github/workflows/demo.yml (pinned toolchain in
# demo/toolchain.lock; every GIF is frame-normalised by tools/demo_repro.py, so
# the frame count and duration are identical on every run).
#
# Determinism: run-scene.sh unsets every *_API_KEY, scrubs the throw-away HOME
# GUID and the "scene complete in N.Ns" stopwatch, and pins TZ/LANG/LC_ALL/PS1.
# --chunk-delay 0 makes the mock LLM emit all deltas in one burst, so a frame
# depends on the output content, not on when the sampler fired.
Output "assets/demo/hero-raw.gif"

Set FontFamily "JetBrains Mono"
Set FontSize 17
Set Width 1200
Set Height 620
Set Framerate 12
Set Padding 24
Set Shell bash
Set Theme { "name": "Catppuccin Mocha", "black": "#45475a", "red": "#f38ba8", "green": "#a6e3a1", "yellow": "#f9e2af", "blue": "#89b4fa", "magenta": "#f5c2e7", "cyan": "#94e2d5", "white": "#bac2de", "brightBlack": "#585b70", "brightRed": "#f38ba8", "brightGreen": "#a6e3a1", "brightYellow": "#f9e2af", "brightBlue": "#89b4fa", "brightMagenta": "#f5c2e7", "brightCyan": "#94e2d5", "brightWhite": "#a6adc8", "background": "#1e1e2e", "foreground": "#cdd6f4", "selection": "#585b70", "cursor": "#f5e0dc" }

# Quiet logs so the recording shows only the agent output.
Env HARBOR_LOGLEVEL "Warning"

# Type off-screen; record only the running demo (lazygit style).
Hide
Type "demo/run-scene.sh hero ansi"
Enter
Show

# The Sleep has to cover the whole run — the CLI boots for ~7 s and the line
# pacing in demo/run-scene.sh stretches the scene itself. A tail that is too
# short does not merely look worse: the recorder stops mid-scene and the GIF is
# silently missing most of the output. DEMO_TAPE_TAIL_SECONDS in the lock.
Sleep 20s
