# Harbor.App.Cli

The canonical CLI app for **Harbor** — the modular .NET 10 AI coding agent harness. Boots a `Microsoft.Extensions.Hosting` host, wires up every Harbor service via DI, and runs an interactive REPL or one-shot subcommands.

This was previously `src/Harbor.Cli/`. Moved to `apps/` to make the solution layout explicit: `src/` holds libraries, `apps/` holds runnable applications. The CLI is just one possible app — the sibling desktop app is `apps/Harbor.App.Avalonia`, and further terminal renderers (Spectre family, Terminal.Gui, Termina, RazorConsole) live in `contrib/tui/` and compile into this CLI when enabled.

## Layer

Composition Root — depends on every Harbor library (Domain, Application, Infrastructure, Presentation) and wires them into a runnable process.

## Install

### Prerequisite: .NET 10 SDK

```bash
# Linux/macOS
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh && ./dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

### Install as a .NET tool (recommended)

```bash
dotnet tool install --global Harbor.App.Cli
harbor --version
```

### Or run from source

```bash
git clone <repo>
cd harbor
export PATH="$HOME/.dotnet:$PATH"
dotnet run --project apps/Harbor.App.Cli -- --help
```

## Configure providers and API keys

Harbor ships JSON provider presets (13+ configs under `providers/`) and resolves each provider's API key through `AuthStore`
(`src/Harbor.Application/Configuration/AuthStore.cs:33-38`). Resolution order:

1. `~/.harbor/config.json` → `ApiKeys` dictionary (written by `harbor auth` or `/auth`)
2. The provider preset's env var (e.g. `KILO_API_KEY`, `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`)
3. Conventional env var (`<PROVIDER>_API_KEY`)

The onboarding wizard (`harbor setup`) stores the key for you.

Local Ollama needs no key (`HARBOR_PROVIDER=ollama`, `OLLAMA_HOST=http://localhost:11434`).

## Run

```bash
harbor [ask <prompt>|setup|auth|config|providers|models|sessions|daemon|status|logs|help|version] [--loglevel <lvl>]
```

| Command          | Description                                                                  |
|------------------|------------------------------------------------------------------------------|
| *(none)*         | Interactive REPL with the selected TUI renderer (default).                   |
| `ask <prompt>`   | One-shot: run the prompt through the agent loop and exit.                    |
| `headless`       | Full agent host + IPC server, no UI — blocks until SIGINT/SIGTERM (pairing QR printed). |
| `setup`          | Onboarding wizard (pick provider/model interactively).                       |
| `auth`           | Set/list stored API keys in `~/.harbor/config.json`.                         |
| `config`         | Inspect/update Harbor config.                                                |
| `providers`      | List registered providers with client-health status.                         |
| `models [prov]`  | List live models for a provider (or all).                                    |
| `sessions`       | List saved sessions.                                                         |
| `daemon`         | Start/stop the background headless daemon.                                   |
| `status`         | Show daemon/host status.                                                     |
| `logs`           | Manage per-run log files (`--list`, `--last`, `--follow`, `--clean`).        |
| `tui` / `storage`| Print available TUI renderers / storage backends.                            |

Log verbosity: `--loglevel <lvl>` (or `-ll` / `HARBOR_LOGLEVEL`). Console defaults to Information; the file log under `~/.harbor/logs/` always captures Debug.

### Interactive REPL (default)

```bash
harbor
# or
dotnet run --project apps/Harbor.App.Cli
```

Type messages, hit Enter to send. Use `/help` for commands. Onboarding wizard runs on first launch if not completed yet.

### Pick a TUI renderer

Resolved from `HARBOR_TUI` or `~/.harbor/cli.json` `defaultTuiRenderer`; falls back to `spectre-tui` (or `plain` in minimal builds without Spectre — `TuiMode.cs:74-97`).

```bash
HARBOR_TUI=ansi        harbor     # streaming renderer (always compiled in)
HARBOR_TUI=plain       harbor     # no colors, pipe-safe (always compiled in)
HARBOR_TUI=consoleex   harbor     # cell-diff renderer, own input pipeline (kitty/mouse/paste)
HARBOR_TUI=spectre-tui harbor     # official Spectre widget framework (default in full builds)
HARBOR_TUI=spectre     harbor     # rich panels
HARBOR_TUI=fullscreen  harbor     # spectre fullscreen mode
HARBOR_TUI=terminal-gui harbor    # mature widget set, menus, dialogs, mouse
HARBOR_TUI=termina     harbor     # ANSI precision, 24-bit color, reactive MVVM
HARBOR_TUI=razor       harbor     # .razor components, hot reload
```

`spectre*`, `terminal-gui`, `termina`, `razor` are compiled from `contrib/tui/*` when
`HarborWithSpectreTui=true` (the default; excluded in minimal builds).
One-shot commands (`ask`, `providers`, …) are always non-interactive regardless of `HARBOR_TUI`.

#### Interactive renderer notes

Interactive renderers own the alt-screen buffer; console logging is routed into the in-TUI
diagnostics panel (F12) instead of stdout. `consoleex` is the newest interactive renderer:
event-driven frame loop, virtualized timeline, streaming markdown with frozen tail,
Ctrl+C aborts the current agent turn and repeats to exit — see its README
([../../src/Harbor.Tui.ConsoleEx/README.md](../../src/Harbor.Tui.ConsoleEx/README.md)).
Enable it persistently with `"tui": "consoleex"` in `~/.harbor/config.json` or
`{ "defaultTuiRenderer": "consoleex" }` in `~/.harbor/cli.json`.

See [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) for the full comparison matrix.

## Common commands (inside REPL)

Dispatched by `SlashCommandDispatcher` (`apps/Harbor.App.Cli/Repl/SlashCommandDispatcher.cs:82-113`):

| Command            | Description                                                       |
|--------------------|-------------------------------------------------------------------|
| `/help`            | List all commands.                                                |
| `/model <name>`    | Switch model — rebinds the active session without restarting the REPL. |
| `/agent <name>` (alias `/mode`) | Switch the active agent/mode.                        |
| `/providers`       | List registered providers.                                        |
| `/sessions`        | List saved sessions.                                              |
| `/auth`            | Manage stored API keys.                                           |
| `/config`          | Inspect/update config.                                            |
| `/setup`           | Re-run the onboarding wizard.                                     |
| `/tui`             | Print available renderers.                                        |
| `/storage`         | Print storage backends.                                           |
| `/exit` (`/quit`)  | Exit.                                                             |

Messages are persisted to the session store as the conversation runs
(`DefaultAgent.AppendMessageAsync`), so nothing is lost on exit; compaction runs
automatically when the context grows (see `CompactionService`).

## Troubleshooting

### Missing API key error names every tried source

You didn't set a key for that provider. Run `harbor auth` (or `harbor setup`),
set the preset env var listed in the provider's JSON config (e.g. `KILO_API_KEY`), or
write it into `ApiKeys` in `~/.harbor/config.json`.

### `Error: 401 Unauthorized`

API key is wrong, expired, or doesn't have access to the requested model. Verify with `curl`:

```bash
curl https://api.anthropic.com/v1/messages \
  -H "x-api-key: $ANTHROPIC_API_KEY" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{"model":"claude-sonnet-4-5","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}'
```

### TUI looks broken on Windows cmd.exe

Use Windows Terminal. Legacy conhost doesn't render ANSI escapes correctly.
ConsoleEx additionally requires VT input/raw-mode bring-up on Windows (falls back to legacy automatically).

### Plugin load failed with Roslyn error

Check the compile error in the log (`HARBOR_LOGLEVEL=Debug`, or `harbor logs --follow`). Most common: missing `using` directive, or plugin references a type from an assembly the host doesn't expose.

### Session history piling up

Sessions accumulate in storage. Use `/sessions` to see them; start fresh or adjust
compaction settings — long context is folded into anchored summaries automatically.

## Project structure

```
apps/Harbor.App.Cli/
├── Program.cs                  # Entry point: thin dispatcher over subcommands + ReplRunner
├── Hosting/
│   ├── HostBuilder.cs          # Microsoft.Extensions.Hosting bootstrap, DI registration (+ partials CliConfig/Logging)
│   ├── TuiMode.cs              # Renderer id resolution, interactive vs non-interactive classification
│   ├── ConsoleExModule.cs      # DI wiring for the ConsoleEx renderer frame owner
│   └── Adapters.cs             # Renderer writer/reader adapters for commands & wizard
├── Repl/
│   ├── ReplRunner.cs           # Interactive + ask driver (delegates rendering to the active TUI)
│   ├── ConsoleExReplRunner.cs  # Event-driven frame-loop runner when ConsoleEx is active
│   └── SlashCommandDispatcher.cs # /help, /model, /agent, ... dispatch
├── Commands/                   # Top-level ICommand impls: daemon, status, logs, config, auth
├── Configuration/CliConfig.cs  # ~/.harbor/cli.json (default renderer, disabled tools, ...)
├── Logging/                    # File + console logging managers
└── Harbor.App.Cli.csproj
```

## Dependencies

Composition root — references every Harbor library, grouped per build flavor
(`Harbor.App.Cli.csproj:145-224`):

- Always: `Harbor.Abstractions`, `Harbor.Core`, `Harbor.Hosting` (+ transitively Domain/Application layers), `Harbor.Desktop.Abstractions`, `Harbor.Tools.Builtin`
- Always: storage `Jsonl` + `Memory`; providers `Ollama`; renderers `Tui.Plain`, `Tui.Ansi`, `Tui.ConsoleEx`
- Always: IPC quartet `Harbor.Ipc.{Abstractions,InProcess,Server,Client}` (mode via `HARBOR_MODE`: `inprocess` | `ipc-server` | `ipc-client`)
- `HarborWithPlugins=true`: `Harbor.Plugins.{Runtime,Storage,Compilation,Instantiation,Registration,Hosting}`
- `HarborWithAllProviders=true`: `Harbor.Storage.Sqlite`, `Harbor.Providers.{OpenAiCompatible,Anthropic,OpenAI}`
- `HarborWithSpectreTui=true` (default): contrib renderers `contrib/tui/Harbor.Tui.{Spectre,Spectre.Fullscreen,SpectreTui,TerminalGui,Termina,RazorConsole}`
- `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http`

Scripting was moved out of the main CLI: `--script` lives in `contrib/scripting` and reports as unsupported here.

## See also

- [../../docs/GETTING_STARTED.md](../../docs/GETTING_STARTED.md)
- [../../docs/CONFIGURATION.md](../../docs/CONFIGURATION.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — sibling GUI apps
- [../../docs/PLUGIN_DEVELOPMENT.md](../../docs/PLUGIN_DEVELOPMENT.md)
- [../../docs/TOOLS_CATALOG.md](../../docs/TOOLS_CATALOG.md)
