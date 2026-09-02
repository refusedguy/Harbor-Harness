# Architecture

> High-level design of Harbor. For full details, see [specs/](../specs/).
>
> **Связанные документы:**
> - [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) — canonical Clean / Hexagonal / Onion layering rules + the allowed/forbidden ProjectReference matrix, enforced by `Harbor.Architecture.Tests`.
> - [PATTERNS.md](./PATTERNS.md) — каталог из 18 паттернов с примерами кода.
> - [ANTIPATTERNS.md](./ANTIPATTERNS.md) — 38 "не делайте так" с примерами.
> - [EXAMPLES.md](./EXAMPLES.md) — 40+ рецептов.
> - [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — 41 known violation + §ARCH-001..§ARCH-NNN layering audit.

## Design goals

1. **Modular** — every concern behind an interface, swappable via DI.
2. **NativeAOT-ready** — Core can be AOT-compiled; TUI runs JIT.
3. **Low memory** — <30MB RSS idle target (vs 1GB+ for Node.js equivalents).
4. **Plugin-extensible** — tools, providers, agents, UI all extensible.
5. **Testable** — ~1350 tests across 20+ test projects; interfaces make mocking easy.

## Solution structure

```
Harbor.slnx                          (.sln не существует; есть ещё Harbor.Samples.slnx)
├── apps/
│   ├── Harbor.App.Cli/              (entry point, DI wiring, ReplRunner, TuiMode)
│   └── Harbor.App.Avalonia/         (cross-platform desktop GUI)
├── src/                             (51 проектов; representative subset below)
│   ├── Harbor.Abstractions/         (zero-dep contract surface)
│   ├── Harbor.Abstractions.Contracts/ (models/formatters; бывший Harbor.Domain)
│   ├── Harbor.Application/          (AgentLoop, Configuration, Permissions…)
│   ├── Harbor.Core/                 (EventBus, registries helpers)
│   ├── Harbor.Ui.Framework*/        (TEA state + VMs + services, 9 проектов)
│   ├── Harbor.Storage.Jsonl|Memory|Sqlite/
│   ├── Harbor.Providers.Anthropic|OpenAI|Ollama|OpenAiCompatible|Shared/
│   ├── Harbor.Plugins.{Abstractions..Runtime,Host}/  (8 проектов plugin pipeline)
│   ├── Harbor.Ipc.{Abstractions,Client,Server,InProcess}/
│   ├── Harbor.Tui.{Abstractions,Ansi,Plain,ConsoleEx,Notifications}/
│   ├── Harbor.Tools.Builtin/        (14 builtin tools в Tools/)
│   └── … (Telemetry.*, Logging, Extensions, Hosting, CodeGen и др.)
├── contrib/                         (optional components: tui/, apps/, scripting/, tests/)
├── tests/                           (27 csproj dirs incl. benchmarks + E2E harnesses)
├── providers/                       (13 JSON LLM provider configs)
├── specs/                           (16 design documents)
└── docs/                            (architecture, development guides)
```

## Layered architecture

> **Canonical reference:** [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) — the
> authoritative matrix of allowed and forbidden `<ProjectReference>` edges, mechanically
> enforced by `tests/Harbor.Architecture.Tests` (46 tests: 21 reflection-based +
> 25 NetArchTest-based). The diagram below is the TL;DR; the full rules, Mermaid
> diagram, and audit history live in that document.

Harbor follows **Clean / Hexagonal / Onion Architecture** — dependency direction is
inward only. The innermost layer (Domain) references nothing but the BCL.

```
┌─────────────────────────────────────────────────────────────────┐
│  PRESENTATION (UI / CLI)                                        │
│  - Harbor.App.Cli (composition root) / Harbor.App.Avalonia      │
│  - contrib: App.Wpf / App.Maui / App.Blazor                     │
│  - In-solution TUI: Ansi, Plain, ConsoleEx, Notifications       │
│  - contrib/tui (optional): Spectre, Spectre.Fullscreen,         │
│    SpectreTui, TerminalGui, Termina, RazorConsole, Sixel        │
│  Depends on: Application + Abstractions                         │
└─────────────────────────────────────────────────────────────────┘
                                  ▲
                                  │ uses
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  APPLICATION (use cases, orchestration)                         │
│  - Harbor.Application (AgentLoop, Configuration, Permissions)   │
│  - Harbor.Core + Harbor.Registries                              │
│  - Harbor.Plugins.{Runtime, Hosting, Registration, …}           │
│  - contrib/scripting: Harbor.Scripting.* (ScriptHost, Bridge)   │
│  Depends on: Abstractions ONLY                                  │
└─────────────────────────────────────────────────────────────────┘
                                  ▲
                                  │ implements
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  INFRASTRUCTURE (adapters, I/O, external services)              │
│  - Harbor.Storage.Jsonl / Memory / Sqlite                       │
│  - Harbor.Providers.OpenAiCompatible / Anthropic / OpenAI /     │
│    Ollama / Shared                                              │
│  - Harbor.Tools.Builtin (все 14 инструментов в одном проекте,   │
│    каталог Tools/)                                              │
│  - MCP-клиент — src/Harbor.Tools.Builtin/Tools/Mcp/             │
│  Depends on: Abstractions ONLY (NOT Application)                │
└─────────────────────────────────────────────────────────────────┘
                                  ▲
                                  │ declares
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  DOMAIN / ABSTRACTIONS (the hexagon core)                       │
│  - Harbor.Abstractions (interfaces, events, value objs,         │
│    IAgent, IAgentRunner, IAgentLoop, ITool, IToolRegistry,      │
│    ILlmClient, ISessionStore, IProviderRegistry, IAgentRegistry,│
│    IEventBus, IPermissionService, ICompactionService,           │
│    PermissionRuleset, Identifiers, Plugins/IPlugin, …)          │
│  - Harbor.Abstractions.Contracts (models; namespace            │
│    `Harbor.Abstractions.Models`; бывший Harbor.Domain.dll —     │
│    переименован в F1 decoupling, ADR-007, commit fa8d3ae)       │
│  - Harbor.Tui.Abstractions (TUI interfaces, UiState, UiReducer, │
│    ViewRegistry, IPanels, ITuiViewModel, ITuiView, ITuiPlugin)  │
│  Depends on: NOTHING (only BCL + CSharpFunctionalExtensions +   │
│              Microsoft.Extensions.Logging.Abstractions etc.)    │
└─────────────────────────────────────────────────────────────────┘
```

**Key layering invariants** (enforced by `Harbor.Architecture.Tests` — reflection rules
+ NetArchTest + `FullLayerMatrixTests` data-table over all main-solution src assemblies):

1. `Harbor.Abstractions` references no other Harbor assembly.
2. `Harbor.Tui.Abstractions` references only `Harbor.Abstractions`.
3. `Harbor.Application` / `Harbor.Core` reference only Domain (+ registries for Core);
   never Infrastructure or Presentation.
4. `Harbor.Plugins.*` reference Domain (Runtime may also reference Tui.Abstractions);
   NOT Application.
5. contrib `Harbor.Scripting` references `Harbor.Abstractions` only (NOT `Harbor.Core`).
6. `Harbor.Providers.*` references `Harbor.Abstractions` only (NOT `Harbor.Application`).
7. `Harbor.Storage.*` references `Harbor.Abstractions` only (NOT `Harbor.Application`).
8. `Harbor.Tools.Builtin` references `Harbor.Abstractions` only (NOT `Harbor.Application`).
9. `Harbor.Tui.*` concrete renderers reference `Harbor.Abstractions` +
   `Harbor.Tui.Abstractions` only (NOT Application, NOT Infrastructure).
10. `apps/Harbor.App.Cli` references everything — it is the Composition Root
    (composition root'ы `apps/*` вне матрицы; исключения также CodeGen и Plugins.Host exe).

See [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) for the full allowed/forbidden
matrix and [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) §ARCH-001+ for the
audit trail of violations found and fixed.

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
    Version RequiredHarborVersion { get; }   // contract field on IPlugin
    void Initialize(PluginContext context);
    Task ShutdownAsync(CancellationToken ct = default);
}
// ... + IToolPlugin / IProviderPlugin / IAgentPlugin / ITuiPlugin —
// полный контракт: src/Harbor.Abstractions/Plugins/IPlugin.cs
```

Two loading paths:
- **In-process CS-source (default):** `.cs` files dropped in `~/.harbor/plugins/`
  are compiled in-memory by Roslyn (`Harbor.Plugins.Runtime/CsPluginLoader.cs`)
  and cached on disk by source SHA-256 (`Harbor.Plugins.Compilation/CachingCompiler.cs`).
- **Out-of-process:** `Harbor.Plugins.Host` is a standalone exe hosting plugins
  over MCP stdio (`McpPluginLoadHost`, `McpStdioServer`).

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

## Code principles

Harbor следует строгим принципам OOP/SOLID/GoF/FP/ROP/perf. Полный аудит с примерами нарушений и рекомендациями — [docs/CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md).

### Краткая сводка

| Принцип | Где применять | Эталонные реализации |
|---|---|---|
| **S**RP | Все классы | `UiReducer`, `MessageConverter`, `PermissionRuleset` |
| **O**CP | Все switch-dispatch | Strategy-паттерн (`ITool`, `ILlmClient`) |
| **L**SP | Все interface-impls | `JsonlSessionStore`, `MemorySessionStore`, `SqliteSessionStore` — взаимозаменяемы |
| **I**SP | Все интерфейсы | `ITool` (8 методов, все нужны) |
| **D**IP | Все ссылки на модули | Только `Harbor.Abstractions` в зависимостях |
| **FP** — immutability | Доменные модели | `record Session`, `record AgentMessage`, `record UiState` |
| **FP** — pure functions | Reducers | `UiReducer.Reduce`, `Pricing.CalculateCost` |
| **ROP** | Все public APIs что могут ошибиться | `Result<Session>`, `Result<ITool>`, `Result<ProviderId>` |
| **Perf** — pools | Hot paths | `ArrayPool<byte>`, `StringBuilderPool`, `StringPool.Shared` |
| **Perf** — frozen | Read-only collections | `FrozenDictionary` после `Freeze()` |
| **Perf** — span | Парсинг | `IdentifierValidation` (manual char-check) |
| **AOT** | Core/Storage/Providers | `MemoryPack` source-gen, planned `JsonSerializerContext` |

### Чек-лист для PR

См. [docs/DEVELOPMENT.md §Principles checklist](./DEVELOPMENT.md#principles-checklist).

### Известные нарушения (tech debt)

41 нарушение, 11 критических, разбито по 4 спринта. Полный список — [docs/CODE_PRINCIPLES_AUDIT.md §Prioritized plan](./CODE_PRINCIPLES_AUDIT.md).

## NativeAOT strategy

**Core** (`Harbor.Abstractions`, `Harbor.Abstractions.Contracts`, `Harbor.Application`, `Harbor.Storage.*`, `Harbor.Providers.*`, `Harbor.Tools.Builtin`) — designed to be AOT-compatible:
- No reflection emit.
- `System.Text.Json` source-gen (planned; MCP уже использует `McpJsonSerializerContext`).
- No `AssemblyLoadContext` collectible.

**TUI** (`Harbor.Tui.Ansi`, `Harbor.Tui.ConsoleEx`, optional `contrib/tui/Harbor.Tui.TerminalGui`) — JIT, runs in-process today; two-process mode planned:
- Can use any library.
- Planned: NDJSON over Unix domain sockets (v0.9, см. ROADMAP).
- Crash isolation — TUI crash doesn't kill Core.

## Performance targets

| Metric | Target | Notes |
|---|---|---|
| Cold start | <50ms | Core only |
| RSS idle | <30MB | Core only |
| Binary size | ~5-7MB | NativeAOT, stripped |
| Token-to-screen latency | <35ms | LLM network dominates |
| Test execution | <2s per project | per-project run; whole-solution test invocation breaks under the MTP host |

## Concrete code flow: one user prompt

Что происходит когда пользователь пишет `harbor ask "Print hello world"`? Пройдём
по каждому слою с реальным кодом.

### Step 1: `Program.cs` → `RunAskAsync`

`apps/Harbor.App.Cli/Program.cs:249`:

```csharp
private static async Task<int> RunAskAsync(string[] args, string? scriptPath = null)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: harbor ask <prompt> [--script <path>]"); return 1; }
    string prompt = string.Join(' ', StripLogArgs(args));
    using var host = HostBuilder.Build(args);
    await StartIpcAsync(host.Services).ConfigureAwait(false);
    var runner = new ReplRunner(host.Services.GetRequiredService<ILogger<ReplRunner>>());
    int exitCode = await runner.RunAskAsync(host.Services, prompt).ConfigureAwait(false);
    await StopIpcAsync(host.Services).ConfigureAwait(false);
    return exitCode;
}
```

### Step 2: `HostBuilder.Build` wires DI

`apps/Harbor.App.Cli/Hosting/HostBuilder.cs:27`:

```csharp
public static IHost Build(params string[] args)
{
    // ... create ~/.harbor/{sessions,cache}
    var builder = Host.CreateApplicationBuilder();
    ConfigureLogging(builder, args);
    RegisterCore(builder);                              // AgentLoop, EventBus, registries
    RegisterRegistries(builder, harborDir);             // Tools, Providers, Agents
    RegisterStorage(builder, sessionsDir, sqlitePath);  // Jsonl | Memory | Sqlite
    RegisterTui(builder);                               // Ansi | Plain | Spectre | Fullscreen | ...
    RegisterHttpClients(builder);
    return builder.Build();
}
```

`CreateToolRegistry`:

```csharp
var registry = new ToolRegistry();
var tb = new ToolRegistryBuilder(registry);
var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
tb.AddTool(() => new ReadTool(loggerFactory.CreateLogger<ReadTool>()));
tb.AddTool(() => new WriteTool(loggerFactory.CreateLogger<WriteTool>()));
// ... 6 more
registry.Freeze();   // snapshot to FrozenDictionary for O(1) lookups
```

### Step 3: `ReplRunner.RunAskAsync` → `DefaultAgent.PromptAsync`

REPL resolves `IAgent` from DI and calls `PromptAsync(session, prompt, ct)`:

```csharp
// DefaultAgent.PromptAsync (simplified):
public async Task<Result> PromptAsync(ISessionContext session, string prompt, CancellationToken ct = default)
{
    await session.AppendMessageAsync(UserMessage.Create(prompt), ct).ConfigureAwait(false);
    var agent = _agents.GetAgent(session.Session.Agent).Value;
    return await _loop.RunAsync(session, agent, ct).ConfigureAwait(false);
}
```

### Step 4: `AgentLoop.RunAsync` — orchestration

`src/Harbor.Application/Agents/AgentLoop.cs:102`:

```csharp
public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
{
    // 1. Resolve provider + model
    var client = _providers.GetClient(ProviderId.TryCreate(agent.ProviderId).Value).Value;
    var model = FindModel(await client.GetModelsAsync(ct).ConfigureAwait(false), agent.Model);

    // 2. Publish agent_start
    await _eventBus.PublishAsync(new AgentStartEvent(session.Session.Id, ..., model), ct)
        .ConfigureAwait(false);

    int turn = 0;
    while (!ct.IsCancellationRequested)
    {
        turn++;
        // 3. Compaction check
        if (_compaction.ShouldCompact(session.Messages, model))
            await _compaction.CompactAsync(...);

        // 4. Build system prompt + tools
        var tools = _tools.ResolveTools(agent.Name.Value, agent.Permission);
        string systemPrompt = await _promptBuilder.BuildAsync(...);

        // 5. Stream LLM with pooled StringBuilders
        var partial = AssistantMessage.Empty(session.Session.Id, model.Id);
        using var textBuffer = StringBuilderPool.Rent(4096);
        await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case TextDeltaEvent td: textBuffer.Builder.Append(td.Delta); break;
                case StepFinishEvent sf: finalUsage = sf.Usage; break;
                // ... tool call accumulation
            }
            await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct);
        }

        // 6. Execute tool calls (parallel or sequential)
        foreach (var tc in partial.ToolCalls)
        {
            var toolResult = await ExecuteToolCall(tc, session, agent, ct);
            await session.AppendMessageAsync(new ToolResultMessage(...), ct);
        }

        // 7. No tool calls? break.
        if (partial.ToolCalls.Length == 0) break;
    }

    await _eventBus.PublishAsync(new AgentEndEvent(...), ct);
    return Result.Success();
}
```

### Step 5: `ILlmClient.StreamAsync` — HTTP + SSE parsing

`src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs`:

```csharp
public async IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
{
    using var req = BuildHttpRequest(request);              // POST /v1/chat/completions
    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    resp.EnsureSuccessStatusCode();

    using var stream = await resp.Content.ReadAsStreamAsync(ct);
    using var reader = new StreamReader(stream);

    while (!reader.EndOfStream)
    {
        ct.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync(ct);
        if (!line.StartsWith("data: ")) continue;
        var json = line["data: ".Length..];
        if (json == "[DONE]") break;

        using var doc = JsonDocument.Parse(json);
        foreach (var evt in MapChunkToEvent(doc.RootElement))
            yield return evt;                               // ← streaming to AgentLoop
    }
}
```

### Step 6: Tool execution (with permissions)

`AgentLoop.ExecuteToolCall` (simplified):

```csharp
private async Task<ToolResult> ExecuteToolCall(ToolCallPart tc, ISessionContext session, AgentDefinition agent, CancellationToken ct)
{
    var toolResult = _tools.GetTool(ToolName.Create(tc.Name));
    if (toolResult.IsFailure) return ToolResult.Error(toolResult.Error);

    var tool = toolResult.Value;
    var validation = tool.ValidateArguments(tc.Args);
    if (validation.IsFailure) return ToolResult.Error(validation.Error);

    // Permission check
    var perm = await _permissions.CheckAsync(agent.Name.Value, tc.Name, tc.Args, ct);
    if (perm.IsFailure) return ToolResult.Error(perm.Error);
    if (perm.Value.Action == PermissionAction.Deny)
        return ToolResult.Error($"Permission denied for tool '{tc.Name}'.");

    // Execute
    await _eventBus.PublishAsync(new ToolExecutionStartEvent(tc.Id, tc.Name, tc.Args), ct);
    var result = await tool.ExecuteAsync(tc.Args, ctx, ct);
    await _eventBus.PublishAsync(new ToolExecutionEndEvent(tc.Id, result, result.IsError), ct);
    return result;
}
```

### Step 7: Render to terminal

`PlainTuiRenderer` (default for `HARBOR_TUI=plain`) subscribes to events:

```csharp
bus.Subscribe(async (AgentEvent e, CancellationToken ct) =>
{
    switch (e)
    {
        case AgentStartEvent:    Console.WriteLine($"[agent_start] session={e.SessionId}"); break;
        case TurnStartEvent ts:  Console.WriteLine($"[turn_start] turn={ts.TurnIndex}"); break;
        case MessageStartEvent:  Console.WriteLine($"[message_start] id={e.Message.Id}"); break;
        case MessageUpdateEvent mu when mu.LlmEvent is TextDeltaEvent td:
            Console.Write(td.Delta);   // ← streaming to terminal
            break;
        case MessageEndEvent:    Console.WriteLine($"\n[message_end] id={e.Message.Id}"); break;
        case AgentEndEvent:      Console.WriteLine($"[agent_end] new_messages={e.NewMessages.Count}"); break;
    }
    return Task.CompletedTask;
});
```

### Sequence diagrams (ASCII)

#### Streaming a text response

```
User        ReplRunner     AgentLoop      LlmClient         EventBus        PlainTui
 │              │              │              │                 │               │
 │─"ask hello"─▶│              │              │                 │               │
 │              │─PromptAsync─▶│              │                 │               │
 │              │              │─StreamAsync─▶│                 │               │
 │              │              │              │─POST /v1/chat──▶│               │
 │              │              │              │◀─────SSE chunk 1 (delta "Hel")─│
 │              │              │◀─TextDelta("Hel")───│         │               │
 │              │              │─PublishAsync(MessageUpdate)──▶│               │
 │              │              │              │                 │─onNext()─────▶│
 │              │              │              │                 │               │─Console.Write("Hel")
 │              │              │              │◀─────SSE chunk 2 (delta "lo")──│
 │              │              │◀─TextDelta("lo")────│         │               │
 │              │              │─PublishAsync(MessageUpdate)──▶│               │
 │              │              │              │                 │─onNext()─────▶│
 │              │              │              │                 │               │─Console.Write("lo")
 │              │              │              │◀─────SSE "[DONE]"──────────────│
 │              │              │◀─FinishEvent()│                 │               │
 │              │              │─PublishAsync(MessageEnd)─────▶│               │
 │              │              │              │                 │─onNext()─────▶│
 │              │              │              │                 │               │─Console.WriteLine("[message_end]")
 │              │              │─PublishAsync(AgentEnd)──────▶│               │
 │              │              │              │                 │─onNext()─────▶│
 │              │              │              │                 │               │─Console.WriteLine("[agent_end]")
 │              │◀─Result.Success()──│        │                 │               │
 │◀─exit code 0─│              │              │                 │               │
```

#### Tool execution

```
AgentLoop            ToolRegistry        PermissionService    Tool             EventBus
   │                     │                      │                │                 │
   │─GetTool("read")────▶│                      │                │                 │
   │◀─Result<ITool>──────│                      │                │                 │
   │─ValidateArguments(...)│                    │                │                 │
   │                     │                      │                │                 │
   │─CheckAsync("code","read",{"path":"..."})──▶│                │                 │
   │                     │                      │─Evaluate("read","src/...")       │
   │                     │                      │◀─Allow──────────│                 │
   │◀─Result<Allow>────────────────────────────│                │                 │
   │                     │                      │                │                 │
   │─PublishAsync(ToolExecutionStart)─────────────────────────────────────────────▶│
   │─ExecuteAsync(args, ctx, ct)────────────────────────────────▶│                 │
   │                     │                      │                │─File.ReadAllAsync
   │                     │                      │                │─format lines
   │◀─ToolResult.Success("[0001] using ...")─────────────────────│                 │
   │─PublishAsync(ToolExecutionEnd)──────────────────────────────────────────────▶│
```

#### Compaction

```
AgentLoop          CompactionService     LlmClient         EventBus          SessionStore
   │                    │                    │                 │                  │
   │─ShouldCompact(messages, model)─▶│       │                 │                  │
   │◀───true────────────────────────│       │                 │                  │
   │─PublishAsync(CompactionStarted)─────────────────────────▶│                  │
   │─CompactAsync(sessionId, msgs, model, ct)─▶│              │                  │
   │                    │─BuildSummaryRequest(messages)       │                  │
   │                    │─StreamAsync(summaryRequest)────────▶│                  │
   │                    │◀─TextDelta × N──────────────────────│                  │
   │                    │─BuildSummaryMessage(text)           │                  │
   │◀─Result<CompactionResult>──────│        │                 │                  │
   │─session.AppendMessageAsync(summaryMessage)─────────────────────────────────▶│
   │─PublishAsync(CompactionCompleted { Pruned=12, Saved=8000tok })─────────────▶│
```

## "Why X is designed this way" — 5 key decisions

### 1. Why `Result<T>` (ROP) instead of exceptions?

> **TL;DR**: 1000× faster on the happy path, composable, type-safe.

Exceptions are expensive on the failure path (stack walk, allocation). For *expected*
failures (file not found, invalid args, missing API key) we want cheap signalling.

```csharp
// ❌ Exceptions — slow on failure path
public Session Load(string id)
{
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("id");
    return _store.Load(id) ?? throw new NotFoundException(id);
}

// ✅ Result<T> — cheap, composable
public Result<Session> Load(string? id) =>
    SessionId.TryCreate(id)
        .Bind(sid => _store.GetAsync(sid))
        .Ensure(s => s is not null, "session not found");
```

**Cost**: ~30 ns per `Result.Failure` vs ~30 µs per `throw`. **Composability**:
`.Bind` chains happy-path without nested `if`.

**Tradeoff**: `Result<T>` is a struct (boxing risk); can't easily thread through
`async` returns without allocating `Task<Result<T>>`. Acceptable.

### 2. Why `EventBus` decoupling instead of direct calls?

> **TL;DR**: TUI can be swapped, skipped, or run in another process. Core doesn't know.

Without event bus, `AgentLoop` would call `tui.RenderAsync(event)` directly. Tightly
couples Core to TUI; can't run headless; can't add new subscribers (logger, plugin)
without modifying AgentLoop.

```csharp
// ❌ Direct coupling
public async Task RunAsync(...)
{
    await _tui.RenderAsync(messageStartEvent);
    await _logger.LogAsync(messageStartEvent);
    // ... and again for every new subscriber
}

// ✅ Event bus
public async Task RunAsync(...)
{
    await _eventBus.PublishAsync(messageStartEvent);
    // Subscribers (TUI, logger, plugins) handle it without AgentLoop knowing.
}
```

**Tradeoff**: indirection (1 vtable call per event); subscriber exceptions must be
caught (we use `ImmutableArray` snapshot + dead-subscriber removal).

### 3. Why `FrozenDictionary` after `Freeze()`?

> **TL;DR**: 2× faster lookups vs `ConcurrentDictionary`. Lock-free reads.

`ConcurrentDictionary` is great for write-heavy workloads, but its reads involve
hash-bucket locking. `FrozenDictionary` is built once, then read-only — its lookup
is a single hash + array index.

```csharp
public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<ToolName, ITool> _tools = new();
    private FrozenDictionary<ToolName, ITool>? _frozenTools;

    public Result<ITool> GetTool(ToolName name)
    {
        // Fast path: frozen (lock-free, O(1))
        var frozen = _frozenTools;
        if (frozen is not null && frozen.TryGetValue(name, out var t))
            return Result.Success(t);
        // Slow path: concurrent dict (still O(1), but with locking)
        if (_tools.TryGetValue(name, out var t2))
            return Result.Success(t2);
        return Result.Failure<ITool>($"Tool '{name}' is not registered.");
    }

    public void Freeze() { lock (_frozenLock) _frozenTools = _tools.ToFrozenDictionary(); }
}
```

**Numbers**: `ProviderRegistry.GetClient` = 0.18 µs (frozen) vs ~0.4 µs (concurrent).

**Tradeoff**: post-`Freeze()` writes are invisible until next `Freeze()` call (we
invalidate via `InvalidateFrozenSnapshot()`).

### 4. Why `MemoryPack` for serialization?

> **TL;DR**: 10× faster than `System.Text.Json`, 5× smaller. AOT-compatible.

JSON is human-readable but slow. `MemoryPack` is a binary format with source-generated
formatters — no reflection, no boxing, AOT-friendly.

```csharp
[MemoryPackable]
public sealed partial record Session(
    string Id,
    string ProjectId,
    string Directory,
    /* ... */) { /* ... */ }

// Serialize:
byte[] bytes = MemoryPackSerializer.Serialize(session);

// Deserialize:
Session s = MemoryPackSerializer.Deserialize<Session>(bytes);
```

**Used for**: `Session`, `AgentMessage`, `Usage`, `SessionMetadata` (27 `[MemoryPackable]` refs).

**Tradeoff**: not human-readable (but JSONL storage is text anyway — we keep JSON
for storage and MemoryPack for in-process IPC, planned for v0.7 two-process mode).

### 5. Why TEA (The Elm Architecture) for TUI?

> **TL;DR**: pure reducer = trivially testable. Single source of truth.

TUIs typically spread state across many views (`statusBar.Text = ...`,
`history.AddLine(...)`). Hard to test, hard to time-travel-debug.

```csharp
// ❌ Imperative UI updates
public void OnMessageEnd(MessageEndEvent e)
{
    _history.AddLine(new ChatLine(e.Message.GetText()));
    _statusBar.Status = "idle";
    _inputBox.Enabled = true;
    if (_history.Lines > 100) _history.RemoveFirst();
    // ... 5 more updates scattered across views
}

// ✅ TEA — single pure reducer
public static UiState Reduce(UiState state, AgentEvent e) => e switch
{
    MessageEndEvent me => state
        .AddLine(ChatRole.Assistant, me.Message.GetText())
        .WithStatus("idle"),
    AgentEndEvent => state with { Status = "idle", IsAgentRunning = false },
    _ => state
};
// Views just read UiState, no scattered mutations.
```

**Tradeoff**: more allocations (every event creates a new `UiState` via `with`).
Mitigated by `record` value equality + structural sharing.

---

## Anti-patterns we explicitly avoided

> Full list: [ANTIPATTERNS.md](./ANTIPATTERNS.md). Top 5:

1. **Exceptions for control flow** — forbidden. Use `Result<T>`.
   (Antipattern #4.)

2. **LINQ on hot path** — forbidden. Use `for` loop or `ZLinq`.
   (Antipattern #17.)

3. **Mutable singletons** — forbidden unless `lock`/`Interlocked`/`ConcurrentDictionary`.
   (Antipatterns #3, #29.)

4. **Reflection in AOT paths** — forbidden. Use source-gen.
   (Antipatterns #20, #30, #31, #32, #33.)

5. **Fire-and-forget async** — forbidden without `.ContinueWith(OnlyOnFaulted)`.
   (Antipattern #9.)

Known existing violations documented in [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md)
(41 findings, 11 critical). Don't add more of the same kind.

## Future architecture (v0.9+, two-process)

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
- [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) — layering matrix + architecture tests.
- [CLAUDE.md](../CLAUDE.md) — code conventions.
- [AGENTS.md](../AGENTS.md) — guide for AI agents.
- [Development Guide](./DEVELOPMENT.md) — how to contribute.
- [PATTERNS.md](./PATTERNS.md) — 18 patterns catalog with code.
- [ANTIPATTERNS.md](./ANTIPATTERNS.md) — 38 antipatterns we forbid.
- [EXAMPLES.md](./EXAMPLES.md) — 40+ recipes.
