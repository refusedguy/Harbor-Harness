# Approval gate demo — the bash tool request hits the permission gate, holds,
# auto-approves, and the tool result is rendered. Recorded on the canonical
# CellForge cell-diff renderer, so the gate is the real timeline card
# (ApprovalGateView) rather than a printed box.
#
# Local equivalent:      vhs demo/approval.tape
# Scripted run:          demo/run-scene.sh approval cellforge
# CLI equivalent:        ./apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli \
#                          --demo --scene approval --tui cellforge --chunk-delay 0
# CI regenerates all tapes: .github/workflows/demo.yml (pinned toolchain in
# demo/toolchain.lock; frame-normalised by tools/demo_repro.py).
#
# The gate card itself is the scripted demo gate (apps/Harbor.App.Cli/Demo/
# DemoRuntime.cs): the asker resolves the request with the demo allow policy, and
# the playback replays the card onto the timeline for a fixed number of frames —
# the time a human would take to press a key — before committing it. No keyboard
# input, no credentials — run-scene.sh unsets every *_API_KEY.
Output "assets/demo/approval-raw.gif"

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
Type "demo/run-scene.sh approval cellforge"
Enter
Show

# The Sleep has to cover the whole run — the CLI boots for ~7 s and the cell-diff
# playback then holds a fixed number of painted frames. A tail that is too short
# does not merely look worse: the recorder stops mid-scene and the GIF is silently
# missing most of the output. DEMO_TAPE_TAIL_SECONDS in the lock.
Sleep 34s
