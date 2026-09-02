# Plan — Harbor.App.Cli

## Status: Stable

The CLI is the canonical Harbor app. The sibling desktop app (`apps/Harbor.App.Avalonia`) shares the same engine via DI; contrib TUI renderers plug in at build time.

## Done

- [x] `Microsoft.Extensions.Hosting` bootstrap (`HostBuilder.cs` + partials: CliConfig, Logging)
- [x] Interactive REPL (`ReplRunner`) with slash commands: `/help`, `/setup`, `/auth`, `/model` (rebinds live session w/o restart), `/agent`, `/config`, `/providers`, `/sessions`, `/tui`, `/storage`, `/exit` (`SlashCommandDispatcher.cs:82-113`)
- [x] One-shot mode (`harbor ask <prompt>`) — message persisted per turn, response streamed by renderer
- [x] Headless daemon mode (`harbor headless` / `daemon start`) with IPC server + pairing QR (`Program.cs:177-231`)
- [x] Top-level service commands wired via `ICommand`: `logs`, `daemon`, `status`
- [x] TUI renderers: baseline `plain` / `ansi` / `consoleex` always compiled in; contrib family (`spectre-tui` default, `spectre`, `fullscreen`, `terminal-gui`, `termina`, `razor`) behind `HarborWithSpectreTui` (`Harbor.App.Cli.csproj:216-224`)
- [x] Native providers Ollama baseline; Anthropic/OpenAI/OpenAiCompatible behind `HarborWithAllProviders`; 13+ JSON provider presets via discovery (`Harbor.Hosting/Modules/JsonProviderDiscovery.cs`)
- [x] Storage backends via config + `HARBOR_STORAGE` env var (`Harbor.Hosting/Modules/StorageModule.cs:14`) — CLI default `jsonl`, desktop default `memory`; Sqlite behind `HarborWithAllProviders`
- [x] Plugin loading via 6-layer plugin stack behind `HarborWithPlugins` (`Harbor.App.Cli.csproj:195-203`)
- [x] Permission system (`allow | ask | deny` per tool per glob)
- [x] Anchored-summary compaction
- [x] Session persistence per message (`DefaultAgent` appends to `ISessionStore`), not only on exit
- [x] Onboarding wizard run-on-first-launch gate (`ReplRunner.cs:48-79`)
- [x] ConsoleEx REPL runner: event-driven frame loop, Ctrl+C abort→repeat-to-exit semantics (`Repl/ConsoleExReplRunner.cs`)
- [x] IPC modes selectable at runtime via `HARBOR_MODE` = `inprocess` | `ipc-server` | `ipc-client`

## TODO

- [ ] Streaming of tool output inside interactive renderers (progressive tool-result rendering)
- [ ] Config file hot reload (watch `~/.harbor/config.json`)
- [ ] Shell completion (bash/zsh/powershell via `dotnet-suggest`)
- [ ] `/diff` command to view file changes from last tool call
- [ ] `/undo` for reversible tool calls (read-only safe)
- [ ] MCP **server** mode (MCP *client* tools already work — see `Harbor.Tools.Builtin` mcp trio)
- [ ] Telemetry opt-in (anonymous usage stats)
- [ ] Auto-update for `dotnet tool install --global`

## Known issues

- Scripting moved out of the main build — `--script` reports unsupported and points at `contrib/scripting` (`Program.cs:362-376`).
- Shell completion not yet wired (`dotnet-suggest` integration planned).
- Long sessions in `HARBOR_TUI=ansi` can flicker on Windows Terminal with `tmux`.
- Plugin compile errors not surfaced with file:line (planned P0).

## Next priorities

1. **P0**: Surface plugin compile errors with `path:line:col` diagnostics
2. **P1**: Shell completion (`dotnet-suggest`)
3. **P1**: `/diff` command
4. **P2**: MCP server mode
5. **P2**: Auto-update

## Roadmap

See [../../docs/ROADMAP.md](../../docs/ROADMAP.md) for the full release plan (v0.4 -> v1.0).
