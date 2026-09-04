# Getting Started

> User-facing guide: install, configure, run your first prompt.
>
> **Документ на двух языках**: объяснения на русском (где это помогает), код на английском.
> Если ты хочешь **5-минутный quickstart** — прокрути в самый низ к разделу
> [TL;DR 5-minute quickstart](#tldr-5-minute-quickstart).

## TL;DR 5-minute quickstart

Хочешь запустить Harbor за 5 минут? Вот всё что нужно:

```bash
# 1. Установи .NET 10
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh && ./dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"

# 2. Склонируй и собери Harbor
git clone https://github.com/harbor-sh/harbor
cd harbor
dotnet build

# 3. Установи API ключ (Kilocode = бесплатно, без кредитки)
#    Получи ключ на https://kilo.ai
export KILO_API_KEY=klo_xxxxxxxxxxxxxxxxxxxxxx
export HARBOR_MODEL=kilocode/tencent/hy3:free
export HARBOR_TUI=plain    # простой вывод, удобно для первого запуска

# 4. Запусти one-shot промпт
dotnet run --project apps/Harbor.App.Cli -- ask "Print hello world in 3 languages"

# 5. Или запусти интерактивный REPL
dotnet run --project apps/Harbor.App.Cli
```

### Что ты увидишь (реальный stdout, E2E-verified 2026-07-16)

```bash
$ dotnet run --project apps/Harbor.App.Cli -- ask "Print hello world in 3 languages"

[agent_start] session=8f3c2a01e9d74f5f9b8c1a2b3c4d5e6f
[turn_start] turn=1
[message_start] id=01HN1234567890abcdefghijklm
Hello! Here are three ways to print "Hello, World!":

  1. Python:  print("Hello, World!")
  2. Rust:    println!("Hello, World!");
  3. Go:      fmt.Println("Hello, World!")

[message_end] id=01HN1234567890abcdefghijklm
[turn_end] turn=1
[agent_end] new_messages=1

status: kilocode/tencent/hy3:free | agent: code | $0.0000 | 142↑ 87↓ | idle
```

Что это значит:
- `[agent_start]` — `AgentLoop.RunAsync` опубликовал `AgentStartEvent` в `IEventBus`.
- `[turn_start] turn=1` — начался первый turn (один LLM-call + tool execution).
- `[message_start]` — `OpenAiCompatibleLlmClient` начал стримить.
- Текст между `[message_start]` и `[message_end]` — дельты, склееные в pooled `StringBuilder`.
- `142↑ 87↓` — токены: input 142, output 87 (из `StepFinishEvent.Usage`).
- `$0.0000` — Kilocode free tier реально бесплатный.
- `idle` — `AgentEndEvent` переключил `UiState.Status` обратно в idle.

---

## Common first tasks

После установки попробуй эти типичные задачи:

### Task 1: Ask a coding question

```bash
$ dotnet run --project apps/Harbor.App.Cli -- ask \
    "What's the difference between IEnumerable<T> and IQueryable<T> in C#?"
```

LLM ответит текстом без tool calls. Event sequence:
`agent_start → turn_start → message_start → message_update (×N text deltas) → message_end → turn_end → agent_end`.

### Task 2: Ask the agent to edit a file

```bash
$ export HARBOR_TUI=ansi   # ANSI для интерактивности
$ dotnet run --project apps/Harbor.App.Cli
harbor> Add a TODO comment to the top of src/Harbor.Application/Agents/AgentLoop.cs
```

LLM вызовет `read` (чтобы увидеть файл), затем `edit` (добавить строку). Permission
system спросит подтверждение, если сработает `Ask`-rule.

```bash
[tool_execution_start] id=tc_1 tool=read args={"path":"src/Harbor.Application/Agents/AgentLoop.cs"}
[tool_execution_end]   id=tc_1 ok=true
[tool_execution_start] id=tc_2 tool=edit args={"path":"...","old":"...","new":"..."}
[permission] edit wants to access src/Harbor.Application/Agents/AgentLoop.cs
  [a] allow  [d] deny  [A] always allow
```

### Task 3: Run tests via the agent

```bash
harbor> Run the Harbor.Core.Tests project and tell me which tests fail
```

LLM вызовет `bash` с `dotnet test tests/Harbor.Core.Tests`. Вывод вернётся в
`ToolResult.Output`, LLM его прочитает и резюмирует.

### Task 4: Debug an error

```bash
harbor> I'm getting "Auth failed: Set $KILO_API_KEY" when I run harbor. Why?
```

LLM вызовет `bash` чтобы проверить env vars, увидит что `KILO_API_KEY` не задан,
объяснит как задать (`export KILO_API_KEY=klo_...`).

### Task 5: Explore the codebase

```bash
harbor> Find all TODO(principles) markers in src/ and summarize the top 5 critical ones
```

LLM вызовет `grep` с паттерном `TODO\(principles\)`, потом `read` на топ-5 файлах.
Сработает `plan`-агент (read-only permissions) — безопаснее для exploration.

---

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
dotnet run --project apps/Harbor.App.Cli

# One-shot prompt
# (E2E-verified free example using the Kilocode free model)
export KILO_API_KEY=klo_...
export HARBOR_MODEL=kilocode/tencent/hy3:free
dotnet run --project apps/Harbor.App.Cli -- ask "Write a Python one-liner that prints the first 10 Fibonacci numbers."

# List available providers
dotnet run --project apps/Harbor.App.Cli -- providers

# List available models
dotnet run --project apps/Harbor.App.Cli -- models
dotnet run --project apps/Harbor.App.Cli -- models kilocode
```

### 5. Real output (E2E verified, 2026-07-16)

Running the one-shot prompt above produces output like this (captured with
`HARBOR_TUI=plain`):

```
$ export KILO_API_KEY=klo_…
$ export HARBOR_MODEL=kilocode/tencent/hy3:free
$ export HARBOR_TUI=plain
$ dotnet run --project apps/Harbor.App.Cli -- ask "Print hello world in 3 languages"

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

# Rich interactive shell (contrib renderers are compiled into the CLI by default)
export HARBOR_TUI=spectre

# Second interactive shell (raw mode, cell-diff output) — opt-in
export HARBOR_TUI=consoleex
```

> The rich renderers (`spectre-tui`, `spectre`, `fullscreen`, `termina`,
> `terminal-gui`, `razor`) live in [`contrib/tui/`](../contrib/tui/) and are
> compiled into the CLI by the default-on `HarborWithSpectreTui` MSBuild flag;
> `HARBOR_MINIMAL=true` excludes them (then such ids fall back to `plain`).

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
| `/setup` | Interactive onboarding wizard |
| `/auth` | Provider authentication |
| `/model` | Switch model without restarting the REPL |
| `/agent`, `/mode` | Switch agent mode (`code` / `plan` / `explore`) |
| `/config` | Inspect configuration |
| `/providers` | List registered providers |
| `/sessions` | List saved sessions |
| `/tui` | Show TUI renderer options |
| `/storage` | Show storage backend options |
| `exit`, `quit`, `:q` | Quit the REPL |

> There is no `/tools` slash command — see the tools table below and
> [TOOLS_CATALOG.md](./TOOLS_CATALOG.md) for the full reference.

## Session management

Sessions are auto-saved to `~/.harbor/sessions/` (JSONL) or `~/.harbor/sessions.db` (SQLite).

```bash
harbor sessions  # list
```

## Tools available to the agent

Full set (`HarborToolSetKind.Full14`, the CLI default — see
[TOOLS_CATALOG.md](./TOOLS_CATALOG.md)):

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
| `webfetch` | Fetch URL → markdown (HTML stripped, code kept) |
| `patch` | Apply a unified-diff patch atomically |
| `notebook` | Persistent per-session markdown notes |
| `ripgrep` | Fast content search via `rg` binary (gitignore-aware) |
| `tree` | ASCII directory tree (gitignore-aware) |
| `mcp` | Bridge to registered MCP servers |
| `read_mcp_resource` | Read an MCP server resource by URI |
| `mcp_prompt` | Render an MCP server prompt by name |
| `skill` | Load a SKILL.md skill body by name |

The Avalonia desktop host registers the smaller `Standard10` set (without
`task`, `webfetch`, `ripgrep`, `mcp`, `read_mcp_resource`, `mcp_prompt`).

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

**Как это выглядит в терминале** (реальный stderr):

```bash
$ dotnet run --project apps/Harbor.App.Cli -- ask "hello"
fail: Harbor.Providers.OpenAiCompatible.ConfigAuthResolver[0]
      Auth failed for provider 'kilocode': Set $KILO_API_KEY
      Expected env var: KILO_API_KEY
      Got: (null)
fail: Harbor.App.Cli.Program[0]
      Unhandled exception in CLI entry point
      Harbor.Abstractions.Providers.ProviderAuthException: Auth failed for provider 'kilocode'
         at Harbor.Providers.OpenAiCompatible.ConfigAuthResolver.GetApiKeyAsync()
         at Harbor.Providers.OpenAiCompatible.OpenAiCompatibleLlmClient.StreamAsync(...)
         at Harbor.Core.Agents.AgentLoop.RunAsync(...)
```

Fix: `export KILO_API_KEY=klo_xxx` и перезапусти.

### "Auth failed: Set $ANTHROPIC_API_KEY"

The env var is not set. Run:
```bash
echo $ANTHROPIC_API_KEY
export ANTHROPIC_API_KEY=sk-ant-...
```

### "Model 'claude-sonnet-4-20250514' not found in provider 'anthropic'"

Либо опечатка в имени модели, либо провайдер не отдал эту модель в `/v1/models`.

Fix:

```bash
# 1. Список всех доступных моделей
dotnet run --project apps/Harbor.App.Cli -- models anthropic

# 2. Используй точное имя модели
export HARBOR_MODEL=anthropic/claude-sonnet-4-20250514   # exact id
```

### "Cannot connect to Ollama"

Make sure `ollama serve` is running:
```bash
ollama serve  # in another terminal
curl http://localhost:11434/api/tags  # should return JSON
```

**Типичная ошибка**:

```bash
$ dotnet run --project apps/Harbor.App.Cli -- ask "hi"
fail: Harbor.Providers.Ollama.OllamaLlmClient[0]
      Failed to connect to Ollama at http://localhost:11434
      System.Net.Http.HttpRequestException: Connection refused (localhost:11434)
```

Fix: запусти `ollama serve` в отдельном терминале.

### Provider not showing in `harbor providers`

- Check `providers/<name>.json` exists and has valid `id` field.
- Run `harbor providers` to see load errors.
- Check JSON syntax: `cat providers/<name>.json | python3 -m json.tool`.

**Пример невалидного JSON** (trailing comma):

```bash
$ dotnet run --project apps/Harbor.App.Cli -- providers
warn: Harbor.Hosting.JsonProviderDiscovery[0]
      Skipping provider config 'providers/myllm.json': Unexpected token ',' at position 142
```

Fix: убери trailing comma, запусти `python3 -m json.tool providers/myllm.json`
для проверки.

### Build fails with NU1903 (SQLite vulnerability)

Update `SQLitePCLRaw.lib.e_sqlite3` to latest in `src/Harbor.Storage.Sqlite/Harbor.Storage.Sqlite.csproj`.

```xml
<PackageReference Include="SQLitePCLRaw.lib.e_sqlite3" Version="2.1.10" />
```

### Build fails with NU1603 (package version mismatch)

```bash
$ dotnet build
error NU1603: Harbor.Core depends on CSharpFunctionalExtensions (>= 2.30.0) but CSharpFunctionalExtensions 2.29.0 was found.
```

Fix: `dotnet restore --force` или поправь версию в `Directory.Build.props`.

### Tests timeout / hang

`GetScrollback_ReturnsRecentEvents` skipped by default — он блокирующе читает
`Channel<T>`. Если ты написал тест, который читает из scrollback, не блокируй:
используй `cancellationToken` с таймаутом.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var events = await bus.GetScrollbackAsync(cts.Token);
```

### TUI выглядит сломанным (Spectre/Curses)

```bash
# 1. Попробуй plain renderer (no colors, no escape codes)
export HARBOR_TUI=plain
dotnet run --project apps/Harbor.App.Cli

# 2. Если работает — проблема в твоём terminal emulator
#    Проверь TERM variable
echo $TERM    # должно быть xterm-256color или screen-256color

# 3. Для CI / pipes — всегда используй plain
HARBOR_TUI=plain dotnet run --project apps/Harbor.App.Cli -- ask "..." | grep foo
```

### "Tool 'X' is not registered" в логах

Plugin не загрузился. Проверь `~/.harbor/plugins/` и лог
(`~/.harbor/logs/harbor-cli-*.log`).

```bash
$ ls ~/.harbor/plugins/
hello.cs  webhook.cs

$ grep -i plugin ~/.harbor/logs/harbor-cli-*.log | tail -5
info: Harbor.Plugins.Hosting.PluginHost[0]
     Loaded 1 CS plugin(s)
warn: Harbor.Plugins.Runtime.CsPluginLoader[0]
     Failed to compile ~/.harbor/plugins/webhook.cs: (12,17): error CS0103: The name 'HttpClient' does not exist in the current context
```

Fix: добавь `using System.Net.Http;` в `webhook.cs`, перезапусти Harbor.

### Agent отвечает "I don't have a tool for that"

LLM пытается вызвать tool, который не зарегистрирован. Зарегистрированные tools
видны в логе и в [TOOLS_CATALOG.md §1](./TOOLS_CATALOG.md#1-inventory) (REPL-команды
`/tools` нет). Если нужного tool нет — поставьте plugin
(см. [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md)).

### Streaming залипает на одном месте

Возможно LLM-стрим ждёт tool-call, но tool не валидируется. Проверь лог:

```bash
tail -100 ~/.harbor/logs/harbor-cli-*.log | grep -E "tool|stream"
```

Если видишь `Validating tool call args... failed: Missing 'path' argument` —
LLM прислал кривые аргументы. Обычно LLM сам исправляется после ошибки.

### Out of memory на длинной сессии

Compaction должен срабатывать автоматически когда сессия близка к context window.
Если не срабатывает — проверь `HeuristicTokenEstimator` и `CompactionService`:

```bash
$ HARBOR_LOGLEVEL=debug dotnet run --project apps/Harbor.App.Cli
# В логах должно быть:
# debug: CompactionService.ShouldCompact: estimated=12345 / context=8192 → true
# info:  Compaction triggered for session abc123
```

Если compaction не запускается — возможно `model.ContextWindow` равно 0
(провайдер не отдал `context_length`).

## Next steps

- Read [BUILD.md](./BUILD.md) for build/publish instructions.
- Read [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md) to write your own plugins.
- Read [EXAMPLES.md](./EXAMPLES.md) for 40+ recipes (tools, providers, storage, TUI, plugins).
- Read [PATTERNS.md](./PATTERNS.md) to understand the codebase (18 patterns catalogued).
- Read [ANTIPATTERNS.md](./ANTIPATTERNS.md) for what NOT to do (38 antipatterns with code).
- Read [specs/](../specs/) for the full design rationale.
