# Harbor

> Modular .NET 10 AI coding agent harness — multiple providers, storages, TUIs, plugin hosting, MCP support, performance-first.

[![.NET](https://img.shields.io/badge/.NET-10.0-blue)]()
[![License](https://img.shields.io/badge/license-MIT-green)]()
[![Providers](https://img.shields.io/badge/providers-13%20JSON%20configs-blue)]()
[![Plugins](https://img.shields.io/badge/plugins-4%20samples-orange)]()

Harbor is a from-scratch reimagining of AI coding agents (kilocode, opencode, pi-agent, crush) built on .NET 10. The goal: maximum modularity (every concern behind an interface, swappable via DI), NativeAOT-readiness for the core, and performance-first engineering — without the 1GB+ RAM footprint of Node.js equivalents.

## Key features

- **LLM providers** — 3 native clients + 13 JSON-config providers:
  - Native: `Anthropic` (cache_control, extended thinking), `OpenAI` (Chat Completions + Responses API), `Ollama` (local NDJSON)
  - JSON configs: `openai-compatible` adapter plus `anthropic`, `kilocode` (with **FREE** models), `deepseek`, `groq`, `mistral`, `xai`, `together`, `fireworks`, `cerebras`, `openrouter`, `vllm`, `ollama` presets in [`providers/*.json`](./providers/)
- **Storage backends**: `Jsonl` (default, zero native deps), `Memory` (tests), `Sqlite` — switched via `HARBOR_STORAGE`
- **Terminal UIs**:
  - `Harbor.Tui.ConsoleEx` — the new second render path: own raw-mode input pipeline (kitty keyboard protocol, SGR mouse 1000/1002/1006, bracketed paste), cell-grid diff renderer (`DiffEngine`), zero-allocation steady-state budgets, virtualized chat timeline with streaming markdown and unified-diff blocks. Opt-in, see below.
  - `AnsiTuiRenderer` / `PlainTuiRenderer` — classic streaming/plain-text renderers
  - Additional interactive renderers (`Spectre.Tui` shell, `Fullscreen`, `Terminal.Gui`, `Termina`, `RazorConsole`) physically live in [`contrib/tui/`](./contrib/tui) but are compiled into the default CLI build
  - `Notifications` renderer (desktop OS notifications), Avalonia desktop app (`apps/Harbor.App.Avalonia`)
- **14 builtin tools** under [`src/Harbor.Tools.Builtin`](./src/Harbor.Tools.Builtin): `read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls`, `task` (sub-agent delegation), `webfetch`, `patch`, `notebook`, `ripgrep`, `tree`, `mcp`
- **MCP support** — Model Context Protocol servers over stdio in an out-of-process host: registry, JSON-RPC transport, argv parsing, source-generated serialization ([AOT-safe](./docs/BUILD.md)); a single `mcp` tool surface maps any configured server's tools into the agent; server instructions are aggregated into the system prompt
- **Sub-agents** — builtin agents `code`, `plan`, `explore` (own permissions per `PermissionRuleset`); the `task` tool delegates to child agents through `IAgentRegistry`
- **Plugin hosting** — layered architecture instead of one monolithic loader: `Harbor.Plugins.{Abstractions,Compilation,Instantiation,Registration,Hosting,Runtime,Host,Storage}`; CS-source plugins are compiled in-memory by Roslyn at startup (drop a `.cs` into `~/.harbor/plugins/`)
- **Event bus** — typed `IEventBus` pub/sub decoupling: renderers, loggers, plugins subscribe to `AgentEvent`s only, never call Core directly
- **Permissions** — `allow | ask | deny` per tool per glob, plus granular tool categories (`read/write/network/exec/mcp`) and per-agent rulesets
- **Compaction** — anchored-summary context compaction when the model's context window fills up
- **Remote/daemon mode** — headless IPC server (`harbor headless`, `harbor daemon start`) with pairing codes + terminal QR rendering; remote transport project `Harbor.Transport.Remote`
- **Telemetry** — `Harbor.Telemetry.Core` + OTLP exporter
- **Railway Oriented Programming** — `Result<T>` everywhere (`CSharpFunctionalExtensions`); exceptions reserved for truly exceptional failures
- **Performance techniques** — `FrozenDictionary` registries, `ArrayPool<T>`, `StringBuilderPool`, `Channel<T>` streaming, manual `for` loops on hot paths, zero `unsafe`
- **TUnit tests** — source-generated test discovery across ~25 test projects under [`tests/`](./tests)

## Quick start

```bash
# Install .NET 10, then:
git clone https://github.com/harbor-sh/harbor
cd harbor
dotnet build

# Option A — Kilocode free model (recommended, no credit card)
export KILO_API_KEY=klo_...
export HARBOR_MODEL=kilocode/tencent/hy3:free

# Option B — Anthropic
export ANTHROPIC_API_KEY=sk-ant-...

# Option C — OpenRouter (200+ models)
export OPENROUTER_API_KEY=sk-or-...

# Interactive REPL (Spectre.Tui shell by default)
dotnet run --project apps/Harbor.App.Cli

# One-shot ask
dotnet run --project apps/Harbor.App.Cli -- ask "What is 2+2?"

# ConsoleEx — the new second render path (opt-in)
HARBOR_TUI=consoleex dotnet run --project apps/Harbor.App.Cli

# Desktop GUI
dotnet run --project apps/Harbor.App.Avalonia
```

CLI verbs (see `help`): `ask <prompt> | setup | auth | config | providers | models [provider] | sessions | tui | storage | logs | daemon | status | help | version`.

> **Env var note**: Kilocode uses `KILO_API_KEY` (not `KILOCODE_API_KEY`). Get a free key at <https://kilo.ai>.

See [docs/GETTING_STARTED.md](./docs/GETTING_STARTED.md) for the full guide and [docs/BUILD.md](./docs/BUILD.md) for build variants (granular feature flags such as `no-plugins` / `no-all-providers` via the NUKE build).

## E2E verified — Kilocode free model

Harbor's documented end-to-end path runs against the Kilocode gateway using the free `tencent/hy3:free` model ($0 cost).

```bash
export KILO_API_KEY=klo_xxxxxxxxxxxxxxxxxxxxxx
export HARBOR_MODEL=kilocode/tencent/hy3:free
export HARBOR_TUI=plain   # plain is easiest to pipe / inspect
dotnet run --project apps/Harbor.App.Cli -- ask "Print hello world in 3 languages"
```

Captured output (regression baseline):

```
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

This exercises provider auto-discovery (`providers/kilocode.json`), env-var auth resolution, `AgentLoop` streaming, the event sequence (`agent_start → turn_start → message_* → message_end → turn_end → agent_end`), and the status bar. The E2E checklist lives in [AGENTS.md §E2E testing](./AGENTS.md#e2e-testing).

## Benchmarks

Numbers and methodology live in [docs/BENCHMARKS.md](./docs/BENCHMARKS.md). Historical spot-checks (2026-08-22, i5-8250U, Release JIT): cold start ~38 ms Debug JIT / ~28 MB RSS idle / ~5 MB binary — see that doc for current tables and bottlenecks before quoting anything.

## Architecture

Clean / Hexagonal layering enforced mechanically by [tests/Harbor.Architecture.Tests](./tests/Harbor.Architecture.Tests). Canonical reference: [docs/ARCHITECTURE_LAYERS.md](./docs/ARCHITECTURE_LAYERS.md).

```
Domain            Harbor.Abstractions (+ .Contracts), Harbor.Terminal.Abstractions,
                  Harbor.Desktop.Abstractions, Harbor.Diagnostics.Abstractions
Application       Harbor.Core, Harbor.Application, Harbor.Registries,
                  Harbor.Ipc.*, Harbor.Ui.Framework.* (TEA-style state/reducers/projection),
                  Harbor.Plugins.* (Abstractions/Compilation/Instantiation/
                                    Registration/Hosting/Runtime/Host/Storage)
Infrastructure    Harbor.Storage.{Jsonl,Memory,Sqlite}, Harbor.Providers.{Anthropic,
                  OpenAI,Ollama,OpenAiCompatible,Shared}, Harbor.Tools.Builtin,
                  Harbor.Logging, Harbor.Telemetry.*, Harbor.Transport.Remote,
                  Harbor.CodeGen, Harbor.Extensions
Presentation      apps/Harbor.App.Cli (composition root), apps/Harbor.App.Avalonia,
                  Harbor.Tui.{Plain,Ansi,ConsoleEx,Notifications},
                  contrib/tui/* (extra interactive shells compiled in by default)
```

All IDs are strongly-typed value objects (`SessionId`, `ProviderId`, `ToolName`, …) defined in `Harbor.Abstractions.Contracts.Models.Identifiers`. Agent state reaches every UI exclusively as `AgentEvent`s published on `IEventBus` — renderers never touch Core.

### Data flow

```
User prompt → IAgent.PromptAsync → AgentLoop.RunAsync → SystemPromptBuilder
           → ILlmClient.StreamAsync (IAsyncEnumerable<LlmEvent>) → IEventBus.PublishAsync
           → ToolCallStart/Delta/End → ToolRegistry → permissions (allow/ask/deny)
           → ITool.ExecuteAsync (parallel or sequential) → next turn / compaction
```

### Pattern catalog

| Pattern | Where |
|---|---|
| Strategy | `ILlmClient`, `ITool`, `ITuiRenderer`, `ISessionStore` |
| Registry | `ProviderRegistry`, `ToolRegistry`, `AgentRegistry`, `ViewRegistry` (frozen dictionaries) |
| Observer | `IEventBus` pub/sub for agent events |
| Value Object | `SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName` |
| Repository | `ISessionStore` (Jsonl / Memory / Sqlite) |
| Plugin | `IPlugin` + `IToolPlugin` / `IProviderPlugin` / `IAgentPlugin` / `ITuiPlugin` |
| Chain of Responsibility | `AgentLoop`: prompt → LLM stream → tools → next turn → compaction |

Full catalog with real code: [docs/PATTERNS.md](./docs/PATTERNS.md), forbidden practices: [docs/ANTIPATTERNS.md](./docs/ANTIPATTERNS.md).

## Testing

```bash
# Build first, then run tests per project (recommended):
dotnet test tests/Harbor.Core.Tests -c Release --no-build

# Warning: running dotnet test against the whole solution currently fails under
# the Microsoft.Testing.Platform host — always target a single test project.
```

Framework: [TUnit](https://github.com/thomhurst/TUnit). Shared helpers: [tests/Harbor.TestKit](./tests/Harbor.TestKit). Known/flaky tests are listed in [docs/ROADMAP.md](./docs/ROADMAP.md); don't treat those as your regression.

## Documentation

Users:
- [Getting Started](./docs/GETTING_STARTED.md) — install, configure, first prompt
- [Build & Publish](./docs/BUILD.md) — build variants, publish, distribute
- [Examples Cookbook](./docs/EXAMPLES.md) — 40+ "How do I...?" recipes
- [Tools Catalog](./docs/TOOLS_CATALOG.md) — every builtin tool: schema, examples, decision matrix

Developers:
- [Architecture](./docs/ARCHITECTURE.md) + [Architecture Layers](./docs/ARCHITECTURE_LAYERS.md) + [specs/](./specs/README.md)
- [Development Guide](./docs/DEVELOPMENT.md) — workflows + principles checklist
- [Plugin Development](./docs/PLUGIN_DEVELOPMENT.md) / [Plugin System](./docs/PLUGIN_SYSTEM.md)
- [Component Catalog](./docs/COMPONENT_CATALOG.md) — reusable UI components across Avalonia/Blazor/WPF
- [Pattern Catalog](./docs/PATTERNS.md) / [Antipatterns](./docs/ANTIPATTERNS.md) / [Code Principles Audit](./docs/CODE_PRINCIPLES_AUDIT.md)
- [Spectre.Tui Deep Dive](./docs/SPECTRE_TUI_DEEP_DIVE.md) — anatomy of the interactive shell (contrib/tui/Harbor.Tui.SpectreTui)
- For AI agents: [AGENTS.md](./AGENTS.md) (operations) and [CLAUDE.md](./CLAUDE.md) (conventions)

Design specs: 16+ documents in [specs/](./specs/README.md) — architecture, plugins, providers, tools, sessions, MCP, TUI, NativeAOT, benchmarks.

## Roadmap

See [docs/ROADMAP.md](./docs/ROADMAP.md) for the authoritative plan. Recent highlights:

- Done: ConsoleEx MVP (CE-0…CE-5) — second in-process terminal renderer with raw-mode input, cell-diff rendering, PTY-based e2e coverage
- Done: Result-rail refactors (ROP-B/C/D), plugin hosting split, MCP out-of-process integration, unified provider-preset catalog with health checks and live `/model` rebinding
- Next: sub-agent hardening, two-process architecture exploration (NativeAOT core + JIT UI over UDS), session branching/search

## License

MIT — see [LICENSE](./LICENSE).

## Acknowledgments

Architectural inspiration: [pi-agent](https://github.com/earendil-works/pi) (JSONL sessions, event protocol), [kilocode](https://github.com/kilo-org/kilocode) (permission patterns, compaction), [opencode](https://github.com/anomalyco/opencode), [crush](https://github.com/charmbracelet/crush).

Libraries: CSharpFunctionalExtensions, CommunityToolkit.Mvvm, CommunityToolkit.HighPerformance, MemoryPack, NonBlocking, TUnit, Spectre.Console, Microsoft.Data.Sqlite, ZLinq.
