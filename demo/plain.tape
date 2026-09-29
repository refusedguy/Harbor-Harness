# Renderer switch demo — the same scripted run through the plain-text renderer
# (the HARBOR_TUI=plain fallback used by pipes/CI). The other three tapes are
# recorded on the canonical CellForge cell-diff backend, so this one is what a
# non-tty consumer sees: identical content, no frame painting.
#
# Local equivalent:      vhs demo/plain.tape
# Scripted run:          demo/run-scene.sh hero plain
# CLI equivalent:        HARBOR_TUI=plain ./apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli \
#                          --demo --scene hero --tui plain --chunk-delay 0
# CI regenerates all tapes: .github/workflows/demo.yml (pinned toolchain in
# demo/toolchain.lock; frame-normalised by tools/demo_repro.py).
#
# The scene is the same as hero.tape, so the two GIFs differ only in renderer —
# that is the point of the tape. run-scene.sh sets HARBOR_TUI itself.
Output "assets/demo/plain-raw.gif"

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
Type "demo/run-scene.sh hero plain"
Enter
# Let the CLI's banner land before the screen is revealed. Without this the
# first captured frame races the banner: one pass shows the bare command line,
# the next already shows the banner under it, and the two passes disagree on
# how many screens the recording holds. The banner is written in a few hundred
# milliseconds, so a 2 s pause makes the revealed screen a fixed state.
Sleep 2s
Show

# The Sleep has to cover the whole run — the CLI boots for ~7 s and the line
# pacing in demo/run-scene.sh stretches the scene itself. A tail that is too
# short does not merely look worse: the recorder stops mid-scene and the GIF is
# silently missing most of the output. DEMO_TAPE_TAIL_SECONDS in the lock (the
# cell-diff tapes need the same value; this one simply holds a settled screen
# for longer than it needs to).
Sleep 34s
