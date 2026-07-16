# Architecture

> High-level design of Harbor. For full details, see [specs/](../specs/).

## Design goals

1. **Modular** — every concern behind an interface, swappable via DI.
2. **NativeAOT-ready** — Core can be AOT-compiled; TUI runs JIT.
3. **Low memory** — <30MB RSS idle target (vs 1GB+ for Node.js equivalents).
4. **Plugin-extensible** — tools, providers, agents, UI all extensible.
5. **Testable** — 65+ unit tests, interfaces make mocking easy.

## Solution structure

```
Harbor.sln
├── src/
│   ├── Harbor.Abstractions/         (zero deps, only CSharpFunctionalExtensions)
│   ├── Harbor.Core/                 (DI, EventBus, AgentLoop, registries)
│   ├── Harbor.Storage.Jsonl/        (JSONL session store)
│   ├── Harbor.Providers.OpenAiCompatible/ (generic LLM client)
│   ├── Harbor.Tools.Builtin/        (7 builtin tools)
│   ├── Harbor.Tui.Abstractions/     (TUI interfaces)
│   ├── Harbor.Tui.Ansi/             (ANSI streaming renderer)
│   └── Harbor.Cli/                  (entry point, wiring)
├── tests/
│   ├── Harbor.Abstractions.Tests/   (35 tests)
│   ├── Harbor.Core.Tests/           (10 tests)
│   ├── Harbor.Tools.Builtin.Tests/  (16 tests)
│   └── Harbor.Storage.Jsonl.Tests/  (5 tests)
├── providers/                       (13 JSON LLM provider configs)
├── specs/                           (16 design documents)
└── docs/                            (architecture, development guides)
```

## Layered architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Application Layer                         │
│                   Harbor.Cli (entry point)                   │
└──────────────────────────┬──────────────────────────────────┘
                           │
┌──────────────────────────┴──────────────────────────────────┐
│                      Core Layer                              │
│  ┌──────────────┐  ┌─────────────┐  ┌──────────────────┐   │
│  │  AgentLoop   │  │  EventBus   │  │  SystemPrompt    │   │
│  │  DefaultAgent│  │  InMemory   │  │  Builder         │   │
│  └──────────────┘  └─────────────┘  └──────────────────┘   │
│  ┌──────────────┐  ┌─────────────┐  ┌──────────────────┐   │
│  │  Registries  │  │ Permission  │  │  Compaction      │   │
│  │  (Prov/Tool/ │  │ Service     │  │  Service         │   │
│  │   Agent)     │  │             │  │                  │   │
│  └──────────────┘  └─────────────┘  └──────────────────┘   │
└──────────────────────────┬──────────────────────────────────┘
                           │
┌──────────────────────────┴──────────────────────────────────┐
│                  Abstractions Layer                          │
│  Harbor.Abstractions                                         │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐    │
│  │ ITool  │ │ILlmCli │ │ISessSt │ │IAgent  │ │IEventBu│    │
│  │        │ │  ent   │ │  ore   │ │        │ │  s     │    │
│  └────────┘ └────────┘ └────────┘ └────────┘ └────────┘    │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐    │
│  │ISlashC │ │ITuiRen │ │IPlugin │ │IPermSn │ │IAgentR │    │
│  │  md    │ │ derer  │ │        │ │  vc    │ │  eg    │    │
│  └────────┘ └────────┘ └────────┘ └────────┘ └────────┘    │
└─────────────────────────────────────────────────────────────┘
                           │
┌──────────────────────────┴──────────────────────────────────┐
│                  Implementations Layer                       │
│  ┌──────────────────┐  ┌──────────────────┐                 │
│  │ Harbor.Tools.    │  │ Harbor.Providers.│                 │
│  │ Builtin          │  │ OpenAiCompatible │                 │
│  │ (read/write/...) │  │ (generic client) │                 │
│  └──────────────────┘  └──────────────────┘                 │
│  ┌──────────────────┐  ┌──────────────────┐                 │
│  │ Harbor.Storage.  │  │ Harbor.Tui.Ansi  │                 │
│  │ Jsonl            │  │ (ANSI renderer)  │                 │
│  └──────────────────┘  └──────────────────┘                 │
└─────────────────────────────────────────────────────────────┘
```

## Key architectural decisions

### 1. Abstractions-first

`Harbor.Abstractions` has zero implementation dependencies (only `CSharpFunctionalExtensions`). All interfaces, models, events live here. Plugins and external code can reference just this package.

### 2. Event bus decoupling

Core publishes `AgentEvent` instances to `IEventBus`. Subscribers (TUI, loggers, plugins) receive typed events. This decouples Core from TUI — TUI can be swapped, run in another process, or skipped entirely.

```
AgentLoop → IEventBus.PublishAsync(event) → subscribers
                                            ├── TUI renderer
                                            ├── Logger
                                            └── Plugin handlers
```

### 3. Strategy pattern everywhere

Every swappable component is an interface:
- `ILlmClient` — LLM provider (Anthropic, OpenAI, OpenAI-compatible, etc.)
- `ITool` — tool implementation (read, write, bash, custom)
- `ITuiRenderer` — TUI rendering (ANSI, Terminal.Gui, custom)
- `ISessionStore` — storage backend (JSONL, SQLite, future)
- `ISystemPromptBuilder` — prompt assembly

### 4. Registry pattern

Three registries:
- `IProviderRegistry` — LLM clients, lazy-loaded.
- `IToolRegistry` — tools, filtered by permission.
- `IAgentRegistry` — agent definitions (code, plan, explore, custom).

### 5. Result<T> for error handling

Operations that can fail return `Result<T>` from CSharpFunctionalExtensions:
- No exceptions for expected failures.
- Composable: `result.Bind(...)`, `result.Match(...)`, `result.Ensure(...)`.
- Strongly-typed errors.

### 6. Value objects for identifiers

Strongly-typed IDs prevent mixing up `string` IDs:
- `SessionId`, `MessageId`, `ToolCallId`
- `ProviderId`, `ModelRef` (e.g. `anthropic/claude-opus-4`)
- `ToolName`, `AgentName`

All are `ValueObject` subclasses with `Create()` (throws) and `TryCreate()` (returns Result).

### 7. Permission system

`PermissionRuleset` evaluates `(toolName, argPath)` → `Allow|Ask|Deny`:
- Glob patterns: `src/*`, `*.env`, `*`.
- Per-agent defaults: `code`, `plan`, `explore`.
- Mergeable: user config overrides defaults.

### 8. Plugin contract

```csharp
public interface IPlugin
{
    string Name { get; }
    Version Version { get; }
    void Initialize(PluginContext context);
    Task ShutdownAsync(CancellationToken ct = default);
}

public interface IToolPlugin : IPlugin
{
    void RegisterTools(IToolRegistryBuilder builder);
}

public interface IProviderPlugin : IPlugin
{
    void RegisterProviders(IProviderRegistryBuilder builder);
}

public interface IAgentPlugin : IPlugin
{
    void RegisterAgents(IAgentRegistryBuilder builder);
}
```

Plugins discovered from `~/.harbor/plugins/*.dll` (JIT mode). AOT mode uses out-of-process plugin-host.

### 9. JSONL session storage

- Append-only, atomic writes.
- No native dependencies (no SQLite).
- Git-friendly (text format, line-by-line).
- Branching via `parentId` pointers (planned).

```jsonl
{"type":"session","version":1,"id":"abc","projectId":"...","directory":"/home/user/project",...}
{"type":"message","id":"m1","role":"user","createdAt":"...","payload":{"content":"Hello",...}}
{"type":"message","id":"m2","role":"assistant","createdAt":"...","payload":{"parts":[{"type":"text","text":"Hi!"}],...}}
```

### 10. Generic OpenAI-compatible adapter

90% of LLM providers speak OpenAI-compatible API. The `OpenAiCompatibleLlmClient` handles:
- Streaming SSE
- Tool calls (function calling)
- Reasoning models (o1, o3, DeepSeek-R1)
- Provider-specific quirks (DeepSeek, Groq, Mistral)

Provider config is JSON:
```jsonc
{
  "id": "openrouter",
  "baseUrl": "https://openrouter.ai/api/v1",
  "apiType": "openai-compatible",
  "modelsUrl": "https://openrouter.ai/api/v1/models",
  "modelMapping": { "id": "id", "contextWindow": "context_length" }
}
```

## NativeAOT strategy

**Core** (`Harbor.Abstractions`, `Harbor.Core`, `Harbor.Storage.Jsonl`, `Harbor.Providers.*`, `Harbor.Tools.Builtin`) — designed to be AOT-compatible:
- No reflection emit.
- `System.Text.Json` source-gen (planned).
- No `AssemblyLoadContext` collectible.

**TUI** (`Harbor.Tui.Ansi`, future `Harbor.Tui.TerminalGui`) — JIT, runs in separate process:
- Can use any library.
- Communicates with Core via Unix domain sockets (planned v0.7).
- Crash isolation — TUI crash doesn't kill Core.

## Performance targets

| Metric | Target | Notes |
|---|---|---|
| Cold start | <50ms | Core only |
| RSS idle | <30MB | Core only |
| Binary size | ~5-7MB | NativeAOT, stripped |
| Token-to-screen latency | <35ms | LLM network dominates |
| Test execution | <2s | 65 tests in ~300ms |

## Future architecture (v0.7+)

```
┌─────────────────────────────────────────────────────────────┐
│  Harbor Core (NativeAOT, ~5MB binary, <30MB RSS)            │
│  ┌─────────────────────────────────────────────────────┐    │
│  │  AgentLoop / EventBus / Tools / Providers            │    │
│  └─────────────────────────────────────────────────────┘    │
│                           │                                 │
│                    NDJSON over UDS                           │
│                           │                                 │
└───────────────────────────┼─────────────────────────────────┘
                            │
┌───────────────────────────┴─────────────────────────────────┐
│  Harbor TUI (JIT, Terminal.Gui v2, ~80MB process)            │
│  ┌─────────────────────────────────────────────────────┐    │
│  │  Streaming Markdown / SlashCmds / Input              │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

See [specs/14-architecture-revised.md](../specs/14-architecture-revised.md) for full design.

## References

- [Specifications](../specs/README.md) — 16 detailed design documents.
- [CLAUDE.md](../CLAUDE.md) — code conventions.
- [AGENTS.md](../AGENTS.md) — guide for AI agents.
- [Development Guide](./DEVELOPMENT.md) — how to contribute.
