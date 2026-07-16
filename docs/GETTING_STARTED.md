# Getting Started

> User-facing guide: install, configure, run your first prompt.

## Quick start (5 minutes)

### 1. Install .NET 10

Download from https://dot.net or use your package manager:

```bash
# Linux/macOS
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0

# Add to PATH
export PATH="$HOME/.dotnet:$PATH"
```

### 2. Build Harbor

```bash
git clone https://github.com/harbor-sh/harbor
cd harbor
dotnet build
```

### 3. Set your API key

Pick one provider:

```bash
# Kilocode (recommended — FREE models, no credit card)
export KILO_API_KEY=klo_...
export HARBOR_MODEL=kilocode/tencent/hy3:free

# Anthropic (Claude)
export ANTHROPIC_API_KEY=sk-ant-...

# OpenAI
export OPENAI_API_KEY=sk-...

# OpenRouter (200+ models)
export OPENROUTER_API_KEY=sk-or-...

# Local (Ollama) — no key needed
ollama serve  # in another terminal
```

> **Env var note**: Kilocode's env var is **`KILO_API_KEY`** (not `KILOCODE_API_KEY`).
> Get a free key at <https://kilo.ai>.

### 4. Run

```bash
# Interactive REPL
dotnet run --project src/Harbor.Cli

# One-shot prompt
# (E2E-verified free example using the Kilocode free model)
export KILO_API_KEY=klo_...
export HARBOR_MODEL=kilocode/tencent/hy3:free
dotnet run --project src/Harbor.Cli -- ask "Write a Python one-liner that prints the first 10 Fibonacci numbers."

# List available providers
dotnet run --project src/Harbor.Cli -- providers

# List available models
dotnet run --project src/Harbor.Cli -- models
dotnet run --project src/Harbor.Cli -- models kilocode
```

### 5. Real output (E2E verified, 2026-07-16)

Running the one-shot prompt above produces output like this (captured with
`HARBOR_TUI=plain`):

```
$ export KILO_API_KEY=klo_…
$ export HARBOR_MODEL=kilocode/tencent/hy3:free
$ export HARBOR_TUI=plain
$ dotnet run --project src/Harbor.Cli -- ask "Print hello world in 3 languages"

[agent_start] session=8f3c…
[turn_start] turn=1
[message_start] id=01HN…
Hello! Here are three ways to print "Hello, World!":

  1. Python:  print("Hello, World!")
  2. Rust:    println!("Hello, World!");
  3. Go:      fmt.Println("Hello, World!")

[message_end] id=01HN…
[turn_end] turn=1
[agent_end] new_messages=1

status: kilocode/tencent/hy3:free | agent: code | $0.0000 | 142↑ 87↓ | idle
```

The cost shows `$0.0000` because the `tencent/hy3:free` model is genuinely free on the
Kilocode gateway. The token counts (`142↑ 87↓`) come from the LLM's `StepFinishEvent`
and are aggregated into `SessionMetadata`.

## Configuration

### Default model

```bash
# Kilocode free model — recommended for getting started (no credit card)
export HARBOR_MODEL=kilocode/tencent/hy3:free

# Anthropic
export HARBOR_MODEL=anthropic/claude-sonnet-4-20250514

# OpenAI
export HARBOR_MODEL=openai/gpt-4o

# OpenRouter
export HARBOR_MODEL=openrouter/anthropic/claude-3.5-sonnet

# Local
export HARBOR_MODEL=ollama/llama3.2
```

### TUI renderer

```bash
# Default — ANSI escape codes, streaming
export HARBOR_TUI=ansi

# No colors — for pipes, CI, accessibility
export HARBOR_TUI=plain

# Rich panels/tables via Spectre.Console
export HARBOR_TUI=spectre
```

See all options: `harbor tui`

### Storage backend

```bash
# Default — append-only JSONL files (no native deps)
export HARBOR_STORAGE=jsonl

# In-memory (lost on exit, for tests)
export HARBOR_STORAGE=memory

# SQLite (indexed queries, native e_sqlite3 dep)
export HARBOR_STORAGE=sqlite
```

See all options: `harbor storage`

## Provider configuration

Provider JSON configs live in `providers/` (project-local) or `~/.harbor/providers/` (user-global).

### Builtin providers (native C# implementations)

| Provider | Special features |
|---|---|
| `anthropic` | cache_control, extended thinking, fine-grained tool streaming, beta features |
| `openai` | Chat Completions + Responses API (for o1/o3/GPT-5+), reasoning_effort |
| `ollama` | Local, NDJSON (not SSE), keep_alive parameter, no auth |

### JSON-only providers (generic OpenAI-compat)

13 included configs:

| Provider | Env var |
|---|---|
| `kilocode` (FREE models) | `KILO_API_KEY` |
| `openrouter` | `OPENROUTER_API_KEY` |
| `deepseek` | `DEEPSEEK_API_KEY` |
| `groq` | `GROQ_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `xai` | `XAI_API_KEY` |
| `together` | `TOGETHER_API_KEY` |
| `fireworks` | `FIREWORKS_API_KEY` |
| `cerebras` | `CEREBRAS_API_KEY` |
| `vllm` | (no auth) |
| `ollama` (via JSON) | (no auth) |
| `anthropic` (via JSON) | `ANTHROPIC_API_KEY` |
| `openai` (via JSON) | `OPENAI_API_KEY` |

### Add a new provider

Create `~/.harbor/providers/myprovider.json`:

```jsonc
{
  "id": "myprovider",
  "displayName": "My Provider",
  "baseUrl": "https://api.myprovider.com/v1",
  "apiType": "openai-compatible",
  "authType": "bearer",
  "authEnvVar": "MYPROVIDER_API_KEY",
  "modelsUrl": "https://api.myprovider.com/v1/models",
  "modelsPath": "data",
  "modelMapping": {
    "id": "id",
    "displayName": "name",
    "contextWindow": "context_length"
  }
}
```

Set env var and run:

```bash
export MYPROVIDER_API_KEY=...
harbor providers  # verify it's loaded
```

## Slash commands (in REPL)

| Command | Description |
|---|---|
| `/help` | Show available commands |
| `/providers` | List registered providers |
| `/models [provider]` | List models (all or by provider) |
| `/sessions` | List saved sessions |
| `/tui` | Show TUI renderer options |
| `/storage` | Show storage backend options |
| `/exit` | Quit |

## Session management

Sessions are auto-saved to `~/.harbor/sessions/` (JSONL) or `~/.harbor/sessions.db` (SQLite).

```bash
harbor sessions  # list
```

## Tools available to the agent

| Tool | Description |
|---|---|
| `read` | Read file contents (text or image, with line numbers) |
| `write` | Write/create files |
| `edit` | String replacement in a file (single or multi) |
| `bash` | Execute shell commands |
| `glob` | Find files by pattern |
| `grep` | Search file contents (regex) |
| `ls` | List directory contents |
| `task` | Delegate to a sub-agent (e.g. `explore`, `plan`) |

## Agents (modes)

| Agent | Permissions | Use case |
|---|---|---|
| `code` | Full access (read/write/edit/bash) | Default — coding tasks |
| `plan` | Read-only (no write/edit/bash beyond git) | Planning, exploration |
| `explore` | Read-only, fast | Quick codebase exploration (sub-agent) |

## Tips

### Use OpenRouter for model variety

```bash
export OPENROUTER_API_KEY=sk-or-...
export HARBOR_MODEL=openrouter/anthropic/claude-3.5-sonnet
# Switch models easily:
harbor  # then /models openrouter
```

### Use Ollama for offline/local

```bash
ollama pull llama3.2
ollama serve &
export HARBOR_MODEL=ollama/llama3.2
harbor
```

### Pipe output to other tools

```bash
HARBOR_TUI=plain harbor ask "List all TODO comments in src/" | grep TODO
```

### Use SQLite for long-running deployments

```bash
HARBOR_STORAGE=sqlite harbor
# Sessions in ~/.harbor/sessions.db
```

## Troubleshooting

### "Auth failed: Set $KILO_API_KEY" (or any provider env var)

The env var is not set. Run:
```bash
echo $KILO_API_KEY
export KILO_API_KEY=klo_...    # get one at https://kilo.ai (free)
```

> **Common gotcha**: Kilocode's env var is **`KILO_API_KEY`** — not `KILOCODE_API_KEY`.
> If you see a message asking for `KILOCODE_API_KEY`, you're looking at outdated docs;
> set `KILO_API_KEY` instead.

### "Auth failed: Set $ANTHROPIC_API_KEY"

The env var is not set. Run:
```bash
echo $ANTHROPIC_API_KEY
export ANTHROPIC_API_KEY=sk-ant-...
```

### "Cannot connect to Ollama"

Make sure `ollama serve` is running:
```bash
ollama serve  # in another terminal
curl http://localhost:11434/api/tags  # should return JSON
```

### Provider not showing in `harbor providers`

- Check `providers/<name>.json` exists and has valid `id` field.
- Run `harbor providers` to see load errors.
- Check JSON syntax: `cat providers/<name>.json | python3 -m json.tool`.

### Build fails with NU1903 (SQLite vulnerability)

Update `SQLitePCLRaw.lib.e_sqlite3` to latest in `src/Harbor.Storage.Sqlite/Harbor.Storage.Sqlite.csproj`.

## Next steps

- Read [BUILD.md](./BUILD.md) for build/publish instructions.
- Read [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md) to write your own plugins.
- Read [specs/](../specs/) for the full design rationale.
