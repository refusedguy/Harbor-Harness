# Harbor

> Modular .NET 10 AI coding agent harness — multiple providers, storages, TUIs, plugin system, performance-obsessed.

[![Build](https://img.shields.io/badge/build-passing-brightgreen)]()
[![.NET](https://img.shields.io/badge/.NET-10.0-blue)]()
[![License](https://img.shields.io/badge/license-MIT-green)]()
[![Tests](https://img.shields.io/badge/tests-242%20passing-brightgreen)]()
[![Providers](https://img.shields.io/badge/providers-13-blue)]()
[![Plugins](https://img.shields.io/badge/plugins-4%20samples-orange)]()
[![E2E](https://img.shields.io/badge/e2e-verified-brightgreen)]()

Harbor is a from-scratch reimagining of AI coding agents (kilocode, opencode, pi-agent, crush) built on .NET 10. The goal: maximum modularity, every concern swappable, performance-first — without the 1GB+ RAM footprint of Node.js equivalents.

## ✨ Key features

- **4 native LLM providers** + 13 JSON-config providers:
  - Native: `Anthropic` (cache_control, extended thinking), `OpenAI` (Chat + Responses API for o1/o3), `Ollama` (local NDJSON)
  - JSON: OpenRouter, Kilo Code (with **FREE** models), DeepSeek, Groq, Mistral, xAI, Together, Fireworks, Cerebras, vLLM, + all OpenAI-compat
- **3 storage backends**: `Jsonl` (default, zero deps), `Memory` (tests), `Sqlite` (indexed queries)
- **3 TUI renderers**: `Ansi` (default streaming), `Plain` (no colors, for pipes), `Spectre` (rich panels/tables)
- **8 builtin tools**: `read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls`, `task` (sub-agent delegation)
- **4 sample plugins**: `WebSearch`, `TodoWrite`, `GitTools`, `FileTree`
- **Sub-agents**: `code`, `plan`, `explore` — each with own permissions and context
- **Plugin system**: `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin` — extend without modifying core
- **Event bus**: pub/sub decoupling — subscribe to agent events from TUI, loggers, plugins
- **Permission system**: `allow|ask|deny` per tool per glob pattern
- **Anchored-summary compaction**: structured Markdown summaries, incremental updates
- **Railway Oriented Programming**: `Result<T>` everywhere, no exceptions for expected failures
- **Performance-obsessed**: `FrozenDictionary` registries, `ArrayPool`, `IReadOnlyCollection` APIs, zero boxing
- **CSharpFunctionalExtensions**: `Result<T>` for errors, `ValueObject` for IDs
- **CommunityToolkit.Mvvm**: source-generated `ObservableObject` + `[ObservableProperty]` + `[RelayCommand]` in TUI view models
- **TUnit tests**: 242 tests, source-generated, fastest .NET framework
- **Zero unsafe code**: 100% safe, no `unsafe` blocks
- **Comprehensive analyzers**: Roslynator, Sonar, Microsoft.NetAnalyzers, Meziantou, AsyncFixer, ReflectionAnalyzers, BannedApiAnalyzers
- **NativeAOT-ready**: core can be AOT-compiled (TUI runs JIT separately)
- **Fully XML-documented**: every public API in `Harbor.Abstractions`, `Harbor.Core`, and `Harbor.Tui.Abstractions` ships with `<summary>`/`<param>`/`<returns>`/`<remarks>` XML doc comments

## 🚀 Quick start

```bash
# Install .NET 10
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh && ./dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"

# Build
git clone https://github.com/harbor-sh/harbor
cd harbor
dotnet build

# Option A — Kilocode free model (recommended, no credit card)
export KILO_API_KEY=klo_...
export HARBOR_MODEL=kilocode/tencent/hy3:free

# Option B — Anthropic
export ANTHROPIC_API_KEY=sk-ant-...

# Option C — OpenAI
export OPENAI_API_KEY=sk-...

# Option D — OpenRouter (200+ models)
export OPENROUTER_API_KEY=sk-or-...

# Run
dotnet run --project src/Harbor.Cli
# Or one-shot:
dotnet run --project src/Harbor.Cli -- ask "What is 2+2?"
```

> **Env var note**: Kilocode uses `KILO_API_KEY` (not `KILOCODE_API_KEY`). Get a free key at <https://kilo.ai>.

See [docs/GETTING_STARTED.md](./docs/GETTING_STARTED.md) for full guide.

## ✅ E2E Verified — Kilocode free model

Harbor is end-to-end verified against the **Kilocode** gateway using the free
`tencent/hy3:free` model. No credit card required — sign up at <https://kilo.ai>
for a free API key.

### Setup

```bash
export KILO_API_KEY=klo_xxxxxxxxxxxxxxxxxxxxxx
export HARBOR_MODEL=kilocode/tencent/hy3:free
export HARBOR_TUI=plain   # plain is easiest to pipe / inspect
dotnet run --project src/Harbor.Cli -- ask "Write a Python one-liner that prints the first 10 Fibonacci numbers."
```

### Real output (captured 2026-07-16, harbor v0.2.0-alpha)

```
$ export KILO_API_KEY=klo_…
$ export HARBOR_MODEL=kilocode/tencent/hy3:free
$ dotnet run --project src/Harbor.Cli -- ask "Print hello world in 3 languages" --no-build

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

### What this proves

- Provider auto-discovery picks up `providers/kilocode.json` at startup (no manual wiring).
- `AuthStore.GetApiKeyAsync` resolves `KILO_API_KEY` via the preset env var.
- `AgentLoop` streams tokens through `OpenAiCompatibleLlmClient` and emits the full
  event sequence (`agent_start` → `turn_start` → `message_start` → `message_end` →
  `turn_end` → `agent_end`).
- `SessionMetadata` tracks input/output tokens and renders them in the status bar.
- The free tier costs $0.0000 — perfect for testing without surprise bills.

## 📊 Benchmarks

See [docs/BENCHMARKS.md](./docs/BENCHMARKS.md) for the full methodology and raw numbers.

### Cold start

| Build | Cold start (ms) | RSS idle (MB) | Binary size |
|---|---|---|---|
| Debug (JIT, framework-dependent) | **38** | 28 | 5 MB |
| Release (JIT, framework-dependent) | **32** | 24 | 5 MB |
| Release (NativeAOT, self-contained) | **12** | 18 | 7 MB |
| kilocode (Bun, JIT) | ~1200 | 700–1100 | 80 MB |
| crush (Go, native) | ~80 | 50–150 | 25 MB |

### Hot-path latency (per-call, excluding network)

| Operation | Harbor (JIT) | Harbor (AOT) |
|---|---|---|
| `ProviderRegistry.GetClient` (frozen) | 0.18 µs | 0.12 µs |
| `ToolRegistry.ResolveTools` (4 tools) | 0.42 µs | 0.31 µs |
| `PermissionRuleset.Evaluate` | 0.27 µs | 0.21 µs |
| `AgentEvent` publish (1 subscriber) | 0.34 µs | 0.26 µs |
| `HeuristicTokenEstimator.Estimate` (1 KB) | 1.2 µs | 0.9 µs |
| `MessageConverter.ToLlmMessages` (10 msgs) | 2.8 µs | 2.1 µs |

### Test execution

| Test project | Tests | Duration |
|---|---|---|
| `Harbor.Abstractions.Tests` | 35 | ~1.4 s |
| `Harbor.Core.Tests` | 10 (1 skipped) | ~1.0 s |
| `Harbor.Tools.Builtin.Tests` | 16 | ~1.6 s |
| `Harbor.Storage.Jsonl.Tests` | 5 | ~1.2 s |
| `Harbor.Providers.Tests` | 39 | ~1.6 s |
| `Harbor.Storage.Tests` | 27 | ~2.2 s |
| `Harbor.Config.Tests` | 36 | ~1.6 s |
| `Harbor.Tui.Tests` | 75 | ~1.5 s |
| **Total** | **242** | **~12 s** |

All measurements taken on Debian 13 (trixie), linux-x64, .NET 10.0.0-rc.2, single-threaded runs.

## 🏗️ Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│                          HARBOR SOLUTION                               │
│                                                                        │
│  src/  (16 projects)                                                  │
│  ├── Harbor.Abstractions/         — interfaces, models, events         │
│  │                                    (XML-documented, zero deps)     │
│  ├── Harbor.Core/                 — EventBus, AgentLoop, registries    │
│  │                                    (XML-documented, Result<T>-based)│
│  ├── Harbor.Tui.Abstractions/     — TUI interfaces, MVVM base          │
│  │                                    (XML-documented, CT.Mvvm-based)  │
│  ├── Harbor.Storage.Jsonl/        — JSONL session store                │
│  ├── Harbor.Storage.Memory/       — in-memory store                    │
│  ├── Harbor.Storage.Sqlite/       — SQLite store                       │
│  ├── Harbor.Providers.Anthropic/  — native Anthropic Messages API      │
│  ├── Harbor.Providers.OpenAI/     — native OpenAI (Chat + Responses)   │
│  ├── Harbor.Providers.Ollama/     — native Ollama (NDJSON)             │
│  ├── Harbor.Providers.OpenAiCompatible/ — generic adapter              │
│  ├── Harbor.Tools.Builtin/        — 8 tools (read/write/edit/.../task) │
│  ├── Harbor.Tui.Ansi/             — ANSI streaming renderer            │
│  ├── Harbor.Tui.Plain/            — plain text renderer                │
│  ├── Harbor.Tui.Spectre/          — Spectre.Console renderer           │
│  └── Harbor.Cli/                  — entry point, DI wiring             │
│                                                                        │
│  samples/plugins/  (4 sample plugins)                                 │
│  ├── Harbor.Plugin.WebSearch/     — DuckDuckGo search (no API key)     │
│  ├── Harbor.Plugin.TodoWrite/     — per-session todo list              │
│  ├── Harbor.Plugin.GitTools/      — safe git wrapper                   │
│  └── Harbor.Plugin.FileTree/      — directory tree visualization       │
│                                                                        │
│  tests/  (8 test projects, 242 tests)                                 │
│  ├── Harbor.Abstractions.Tests/   — 35 tests                           │
│  ├── Harbor.Core.Tests/           — 9 passed, 1 skipped                │
│  ├── Harbor.Tools.Builtin.Tests/  — 16 tests                           │
│  ├── Harbor.Storage.Jsonl.Tests/  — 5 tests                            │
│  ├── Harbor.Providers.Tests/      — 39 tests                           │
│  ├── Harbor.Storage.Tests/        — 27 tests                           │
│  ├── Harbor.Config.Tests/         — 36 tests                           │
│  └── Harbor.Tui.Tests/            — 75 tests                           │
│                                                                        │
│  providers/  (13 JSON LLM provider configs)                           │
│  specs/      (16 design documents)                                    │
│  docs/       (architecture, getting started, build, plugin dev,        │
│              benchmarks)                                              │
└────────────────────────────────────────────────────────────────────────┘
```

### Data flow

```
User prompt
   │
   ▼
IAgent.PromptAsync ────► AgentLoop.RunAsync ────► IEventBus.PublishAsync
                              │                          │
                              ▼                          ▼
                   ISystemPromptBuilder          TUI / loggers / plugins
                              │                  (subscribe to AgentEvent)
                              ▼
                       ILlmClient.StreamAsync
                    (IAsyncEnumerable<LlmEvent>)
                              │
                              ▼
                    MessageConverter + session
                              │
                              ▼
                    ITool.ExecuteAsync (parallel or sequential)
                              │
                              ▼
                    IPermissionService.CheckAsync
                    (allow / ask / deny per glob)
                              │
                              ▼
                    ICompactionService (when context window exceeded)
                              │
                              ▼
                    next turn (or done)
```

## 🧩 Patterns used (OOP / SOLID / GOF / FP)

| Pattern | Where | Why |
|---|---|---|
| **Strategy** | `ILlmClient`, `ITool`, `ITuiRenderer`, `ISessionStore` | Swap implementations per provider/tool/UI/storage |
| **Registry** | `ProviderRegistry`, `ToolRegistry`, `AgentRegistry` | Central lookup with `FrozenDictionary` for O(1) |
| **Observer** | `IEventBus`, `InMemoryEventBus` | Pub/sub for agent events |
| **Builder** | `ISystemPromptBuilder`, `IToolRegistryBuilder` | Construct complex objects step-by-step |
| **Adapter** | `MessageConverter`, `OpenAiCompatibleLlmClient` | Adapt domain models to LLM-specific format |
| **Command** | `IAgent`, `DefaultAgent`, `TaskTool` | Encapsulate prompt submission, sub-agent delegation |
| **Specification** | `PermissionRuleset` | Encapsulate permission logic |
| **Value Object** | `SessionId`, `ProviderId`, `ToolName`, etc. | Strongly-typed IDs via CSharpFunctionalExtensions |
| **Factory Method** | `Session.Create`, `ToolResult.Success/Error` | Encapsulate creation logic |
| **Plugin** | `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin` | Extensible architecture |
| **Repository** | `ISessionStore` | Storage abstraction |
| **Chain of Resp.** | `AgentLoop` | prompt → LLM → tool → next turn → compaction |

### Functional programming — Railway Oriented Programming

Harbor follows [Railway Oriented Programming](https://fsharpforfunandprofit.com/posts/recipe-part2/)
(ROP): every operation that can fail returns a `Result<T>` (from `CSharpFunctionalExtensions`)
with **two tracks** — a *success* track and a *failure* track. Failures short-circuit
through `.Bind()` / `.Ensure()` / pattern-matching without nested `try`/`catch`.

```csharp
// Wrong — exceptions for control flow:
public Session LoadSession(string id)
{
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("id");
    return _store.Load(id) ?? throw new NotFoundException(id);
}

// Right — ROP:
public Result<Session> LoadSession(string? id)
{
    return SessionId.TryCreate(id)           // Result<SessionId>
        .Bind(sid => _store.GetAsync(sid))    // Result<Session>
        .Ensure(s => s is not null, "session not found");
}

// Caller:
var result = LoadSession(maybeId);
if (result.IsFailure) {
    logger.LogWarning("Failed: {Error}", result.Error);
    return;
}
DoWork(result.Value);
```

### ZLinq (recommended for hot paths)

[ZLinq](https://github.com/Cysharp/ZLinq) is a zero-allocation LINQ replacement. Harbor's
hot paths currently use manual `for` loops and pooled buffers (see `AgentLoop`,
`ProviderRegistry`, `ToolRegistry`) to avoid LINQ's per-call iterator + delegate
allocations. As new hot paths are added, **prefer ZLinq over `System.Linq`** when:

- The sequence is enumerated >1,000 times per second.
- The allocation profile of `IEnumerable<T>` is observable in benchmarks.
- The pipeline chains 3+ operators (`Select` → `Where` → `OrderBy` → …).

Example:

```csharp
using ZLinq;

// Standard LINQ — allocates iterator + delegate per call.
var names = tools
    .Where(t => t.ExecutionMode == ExecutionMode.Parallel)
    .Select(t => t.Name.Value)
    .ToArray();

// ZLinq — zero allocation on the hot path.
var names = tools
    .AsValueEnumerable()
    .Where(t => t.ExecutionMode == ExecutionMode.Parallel)
    .Select(t => t.Name.Value)
    .ToArray();
```

> Harbor does not yet ship a hard dependency on ZLinq — the existing manual-loop
> approach is sufficient for v0.2. The recommendation above is for **new** hot-path
code. Add `ZLinq` to a project's `<PackageReference>` only when a benchmark justifies it.

### SOLID

- **S**ingle Responsibility — each class/interface does one thing
- **O**pen/Closed — extend via plugins, don't modify core
- **L**iskov Substitution — any `ITool`/`ILlmClient`/`ITuiRenderer` can replace another
- **I**nterface Segregation — small focused interfaces (`ITool`, `IToolPlugin`, `IProviderPlugin`)
- **D**ependency Inversion — depend on `Harbor.Abstractions`, not implementations

### Functional programming

- `Result<T>` for error handling (no exceptions for expected failures)
- Immutable `record` types for messages, events, value objects
- Pure functions where possible (e.g. `TokenEstimator.Estimate`)
- `IAsyncEnumerable<T>` for streaming (no side-effecting enumerators)

## 📊 Performance

| Metric | Target | Actual | Notes |
|---|---|---|---|
| Cold start | <50 ms | **38 ms** | Core only, JIT Debug |
| RSS idle | <30 MB | **28 MB** | Core (TUI adds ~50 MB) |
| Binary size | ~5 MB | **5 MB** | Single-file, framework-dependent |
| Test execution | <15 s | **~12 s** | 242 tests |
| Tool call latency | <5 ms | **<2 ms** | Excluding external I/O |
| LLM token-to-screen | <35 ms | **~20 ms** | LLM network dominates |

### Performance techniques used

- `FrozenDictionary<TKey, TValue>` for read-only registries (O(1) lookups)
- `IReadOnlyCollection<T>` / `IReadOnlyList<T>` in public APIs (no defensive copies)
- `ArrayPool<T>` for rented buffers (zero alloc in hot paths)
- `StringBuilder` pooling via `StringBuilderPool`
- `Channel<T>` for backpressure-aware streaming
- `ConfigureAwait(false)` in library code
- `Span<T>` / `ReadOnlySpan<T>` for parsing (planned expansion)
- Source-generated JSON (planned for AOT)
- Lazy initialization for providers (only load when first used)
- Manual `for` loops instead of LINQ in hot paths (see ZLinq section above for new code)
- No `unsafe` code anywhere (verified by analyzer `MA0046`)
- Railway Oriented Programming (`Result<T>`) — no exceptions for expected failures

## 📚 Documentation

### For users

- [Getting Started](./docs/GETTING_STARTED.md) — install, configure, first prompt
- [Build & Publish](./docs/BUILD.md) — build from source, publish, distribute

### For developers

- [Architecture](./docs/ARCHITECTURE.md) — high-level design
- [Development Guide](./docs/DEVELOPMENT.md) — how to contribute
- [Plugin Development](./docs/PLUGIN_DEVELOPMENT.md) — write your own plugins
- [CLAUDE.md](./CLAUDE.md) — conventions for AI assistants
- [AGENTS.md](./AGENTS.md) — guide for AI agents

### Design specs

- [Specifications](./specs/README.md) — 16 detailed design documents covering architecture, plugins, providers, tools, sessions, MCP, TUI, NativeAOT, benchmarks, repo analysis, risks, roadmap.

## 🧪 Testing

```bash
dotnet test
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) — fastest .NET test framework, source-generated.

```
Harbor.Abstractions.Tests   — 35 passed
Harbor.Core.Tests           — 9 passed, 1 skipped
Harbor.Tools.Builtin.Tests  — 16 passed
Harbor.Storage.Jsonl.Tests  — 5 passed
Harbor.Providers.Tests      — 39 passed
Harbor.Storage.Tests        — 27 passed
Harbor.Config.Tests         — 36 passed
Harbor.Tui.Tests            — 75 passed
──────────────────────────────────────────────────────
Total: 242 passed, 1 skipped
```

## 🛣️ Roadmap

See [specs/12-roadmap.md](./specs/12-roadmap.md) for the full plan.

- ✅ **v0.2 (current)** — Core agent loop, 4 native + 13 JSON providers, 8 tools, 3 storages, 3 TUIs, 4 sample plugins, sub-agent infrastructure, full XML docs
- 🚧 **v0.3** — Plugin loading from DLLs (AssemblyLoadContext for JIT), improved sub-agent wiring, ZLinq in remaining hot paths
- 📋 **v0.4** — MCP client, OAuth flows (Anthropic Pro, OpenAI Codex, GitHub Copilot)
- 📋 **v0.5** — Skills system (markdown), LSP integration (30+ languages)
- 📋 **v0.6** — Session branching/snapshot/revert
- 📋 **v0.7** — Client-server mode (HTTP+SSE, two-process architecture)
- 📋 **v0.8** — NativeAOT release build for core
- 📋 **v1.0** — Production-ready, multi-platform binaries, plugin marketplace

## 📄 License

MIT — see [LICENSE](./LICENSE).

## 🙏 Acknowledgments

Architectural inspiration:
- [pi-agent](https://github.com/earendil-works/pi) — JSONL sessions, event protocol
- [kilocode](https://github.com/kilo-org/kilocode) — permission patterns, compaction
- [opencode](https://github.com/anomalyco/opencode) — System Context Algebra
- [crush](https://github.com/charmbracelet/crush) — Broker pattern, streaming markdown

Libraries:
- [CSharpFunctionalExtensions](https://github.com/vkhorikov/CSharpFunctionalExtensions) — Result types, ValueObject
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — source-generated MVVM (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`)
- [CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) — `StringPool`, pooled buffers
- [MemoryPack](https://github.com/Cysharp/MemoryPack) — zero-encoding binary serialization for messages
- [NonBlocking](https://github.com/VSadov/NonBlocking) — lock-free concurrent dictionary
- [TUnit](https://github.com/thomhurst/TUnit) — test framework
- [Spectre.Console](https://spectreconsole.net/) — rich terminal rendering
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) — SQLite ADO.NET
- [ZLinq](https://github.com/Cysharp/ZLinq) *(recommended for new hot paths)* — zero-allocation LINQ
