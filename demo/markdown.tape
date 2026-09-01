# Streaming markdown demo — tokens stream token-by-token through the ansi renderer.
# Run from the repo root after `dotnet build -c Release`:  vhs demo/markdown.tape
# CI regenerates all tapes automatically: .github/workflows/demo.yml

Output "assets/demo/markdown-compressed.gif"

Set FontFamily "JetBrains Mono"
Set FontSize 17
Set Width 1200
Set Height 620
Set FPS 12
Set Padding 24
Set Shell bash
Set Theme { "name": "Catppuccin Mocha", "black": "#45475a", "red": "#f38ba8", "green": "#a6e3a1", "yellow": "#f9e2af", "blue": "#89b4fa", "magenta": "#f5c2e7", "cyan": "#94e2d5", "white": "#bac2de", "brightBlack": "#585b70", "brightRed": "#f38ba8", "brightGreen": "#a6e3a1", "brightYellow": "#f9e2af", "brightBlue": "#89b4fa", "brightMagenta": "#f5c2e7", "brightCyan": "#94e2d5", "brightWhite": "#a6adc8", "background": "#1e1e2e", "foreground": "#cdd6f4", "selection": "#585b70", "cursor": "#f5e0dc" }

Env HARBOR_LOGLEVEL "Warning"

Hide
Type "./apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli --demo --scene markdown --tui ansi --chunk-delay 15"
Enter
Show

Sleep 7s
