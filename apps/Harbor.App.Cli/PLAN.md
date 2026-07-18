# Plan — Harbor.App.Cli

## Status: Stable

The CLI is the canonical Harbor app. All other apps (Avalonia, WPF, Blazor, MAUI) share the same engine via DI.

## Done

- [x] `Microsoft.Extensions.Hosting` bootstrap (`HostBuilder.cs`)
- [x] Interactive REPL with `/help`, `/model`, `/provider`, `/sessions`, `/load`, `/save`, `/clear`, `/compact`, `/tools`, `/plugins`, `/permissions`, `/quit`
- [x] One-shot mode (`--message`, `--stdin`)
- [x] All 9 TUI renderers wired (`HARBOR_TUI` env var)
- [x] All 4 native providers wired + 13 JSON-config providers
- [x] All 3 storage backends wired (`HARBOR_STORAGE` env var)
- [x] Plugin loading via `PluginHostBuilder` (full pipeline)
- [x] Scripting host (SharpTS research in progress)
- [x] Permission system (`allow | ask | deny` per tool per glob)
- [x] Anchored-summary compaction
- [x] Sub-agent delegation (`task` tool spawns `code` / `plan` / `explore`)
- [x] Session autosave on exit

## TODO

- [ ] Streaming TUI: better progressive rendering of tool output
- [ ] Config file hot reload (watch `~/.harbor/config.json`)
- [ ] Shell completion (bash/zsh/powershell via `dotnet-suggest`)
- [ ] `/diff` command to view file changes from last tool call
- [ ] `/undo` for reversible tool calls (read-only safe)
- [ ] MCP (Model Context Protocol) server mode
- [ ] Telemetry opt-in (anonymous usage stats)
- [ ] Auto-update for `dotnet tool install --global`

## Known issues

- `harbor --message` mode does not stream — buffers entire response.
- Shell completion not yet wired (`dotnet-suggest` integration planned).
- Long sessions in `HARBOR_TUI=ansi` can flicker on Windows Terminal with `tmux`.
- Plugin compile errors not surfaced with file:line (planned P0).

## Next priorities

1. **P0**: Surface plugin compile errors with `path:line:col` diagnostics
2. **P0**: Streaming one-shot mode (`--message --stream`)
3. **P1**: Shell completion (`dotnet-suggest`)
4. **P1**: `/diff` command
5. **P2**: MCP server mode
6. **P2**: Auto-update

## Roadmap

See [../../docs/ROADMAP.md](../../docs/ROADMAP.md) for the full release plan (v0.4 -> v1.0).
