# Harbor.App.Cli

The canonical CLI app for **Harbor** — the modular .NET 10 AI coding agent harness. Boots a `Microsoft.Extensions.Hosting` host, wires up every Harbor service via DI, and runs an interactive REPL or one-shot command mode.

This was previously `src/Harbor.Cli/`. Moved to `apps/` to make the solution layout explicit: `src/` holds libraries, `apps/` holds runnable applications. The CLI is just one possible app — sibling apps include `Harbor.App.Avalonia`, `Harbor.App.Wpf`, `Harbor.App.Blazor`, `Harbor.App.Maui`. The .NET namespace stays `Harbor.Cli.*` for backward compat with existing user code and docs.

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

## Configure API key

Harbor reads provider configuration from `appsettings.json`, environment variables, or `~/.harbor/config.json`.

### Option A: environment variable (quickest)

```bash
# Pick one provider:
export HARBOR_PROVIDER=anthropic
export Anthropic__ApiKey=sk-ant-...

# Or OpenAI:
export HARBOR_PROVIDER=openai
export OpenAI__ApiKey=sk-...

# Or OpenAI-compatible (OpenRouter, DeepSeek, Groq, etc.):
export HARBOR_PROVIDER=openai-compatible
export OpenAiCompatible__ApiKey=sk-or-...
export OpenAiCompatible__BaseUrl=https://openrouter.ai/api/v1
export OpenAiCompatible__Model=anthropic/claude-3.5-sonnet

# Or local Ollama (no API key needed):
export HARBOR_PROVIDER=ollama
export OLLAMA_HOST=http://localhost:11434
export HARBOR_MODEL=llama3.1:8b
```

### Option B: `~/.harbor/config.json`

```json
{
  "Harbor": {
    "Provider": "anthropic",
    "Model": "claude-3-5-sonnet-20241022"
  },
  "Anthropic": { "ApiKey": "sk-ant-..." }
}
```

## Run

### Interactive REPL (default)

```bash
harbor
# or
dotnet run --project apps/Harbor.App.Cli
```

Boots the default TUI (`SpectreTui` when stdout is a TTY; `Ansi` otherwise). Type messages, hit Enter to send. Use `/help` for commands.

### One-shot command mode

```bash
harbor --message "What files are in this repo?"
# or
echo "Explain Program.cs" | harbor --stdin
```

### Pick a TUI renderer

```bash
HARBOR_TUI=ansi       harbor     # default streaming
HARBOR_TUI=plain      harbor     # no colors, pipe-safe
HARBOR_TUI=spectre    harbor     # rich panels
HARBOR_TUI=spectre-fullscreen harbor
HARBOR_TUI=spectre-tui harbor     # interactive (default in TTY)
HARBOR_TUI=terminal-gui harbor    # experimental
HARBOR_TUI=termina     harbor     # experimental
HARBOR_TUI=razor-console harbor   # experimental
```

## Common commands (inside REPL)

| Command           | Description                                         |
|-------------------|----------------------------------------------------|
| `/help`           | List all commands.                                  |
| `/model <name>`   | Switch the active model on the fly.                 |
| `/provider <key>` | Switch the active provider (anthropic, openai, ...). |
| `/sessions`       | List saved sessions.                                |
| `/load <id>`      | Resume a saved session.                             |
| `/save`           | Save the current session (auto-saves on exit).      |
| `/clear`          | Clear the transcript (does not delete the session). |
| `/compact`        | Manually trigger anchored-summary compaction.       |
| `/tools`          | List registered tools.                              |
| `/plugins`        | List loaded plugins.                                |
| `/permissions`    | Open the permission editor.                         |
| `/quit`           | Exit.                                               |

## Troubleshooting

### `Error: HARBOR_PROVIDER not set`
You didn't pick a provider. Either set the env var or write `~/.harbor/config.json`.

### `Error: 401 Unauthorized`
API key is wrong, expired, or doesn't have access to the requested model. Verify with `curl`:

```bash
curl https://api.anthropic.com/v1/messages \
  -H "x-api-key: $Anthropic__ApiKey" \
  -H "anthropic-version: 2023-06-01" \
  -H "content-type: application/json" \
  -d '{"model":"claude-3-5-sonnet-20241022","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}'
```

### TUI looks broken on Windows cmd.exe
Use Windows Terminal. Legacy conhost doesn't render ANSI escapes correctly.

### Plugin load failed with Roslyn error
Check the compile error in the log (`HARBOR_LOGLEVEL=Debug`). Most common: missing `using` directive, or plugin references a type from an assembly the host doesn't expose.

### Memory keeps growing
Long sessions accumulate. Run `/compact` to fold old turns into a summary, or `/save` + restart.

## Project structure

```
apps/Harbor.App.Cli/
├── Program.cs             # Entry point: args parse, host build, run
├── Hosting/
│   └── HostBuilder.cs     # Microsoft.Extensions.Hosting bootstrap, DI registration
├── Repl/
│   ├── ReplLoop.cs        # Interactive REPL driver
│   └── ReplCommands.cs    # /help, /model, /provider, etc.
├── Commands/              # One-shot subcommands (--message, --stdin)
├── Logging/               # Console logging formatter
└── Harbor.App.Cli.csproj
```

## Dependencies

Composition root — references every Harbor library:

- `Harbor.Abstractions`, `Harbor.Core`
- `Harbor.Plugins.Runtime` + all 6 plugin layers
- `Harbor.Scripting.Hosting` + all 5 scripting layers
- `Harbor.Storage.Jsonl` / `Memory` / `Sqlite`
- `Harbor.Providers.OpenAiCompatible` / `Anthropic` / `OpenAI` / `Ollama`
- `Harbor.Tools.Builtin`
- All `Harbor.Tui.*` renderers
- `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http`

## See also

- [../../docs/GETTING_STARTED.md](../../docs/GETTING_STARTED.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — sibling GUI apps
- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)
- [../../docs/TOOLS_CATALOG.md](../../docs/TOOLS_CATALOG.md)
