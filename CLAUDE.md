# CLAUDE.md — Conventions for AI assistants working on Harbor

> This file is read by Claude Code (and other AI agents) when working on this codebase. It encodes the project's conventions, patterns, and gotchas.
>
> **Project state (branch dev):** two `.slnx` solutions (`Harbor.slnx` main, `Harbor.Samples.slnx`); tests run per-project as plain executables (`dotnet run --project tests/<Project> -c Release --no-build`) because `dotnet test` discovers zero tests under the Microsoft.Testing.Platform bridge in this repo. Known/flaky failures (Avalonia-12 headless "Stack empty", Linux IPC pipe timing, occasional ChatView flake) are tracked in [docs/ROADMAP.md](./docs/ROADMAP.md). See [docs/PROJECT_STATUS.md](./docs/PROJECT_STATUS.md) for the quick-reference card.

## Companion documents

- **[AGENTS.md](./AGENTS.md)** — operational guide for AI agents (read this for **how to make changes**, common tasks, E2E testing, what NOT to do). Cross-references this file.
- **[docs/PROJECT_STATUS.md](./docs/PROJECT_STATUS.md)** — quick-reference card: build/test status, recent milestones, known broken, top 3 next steps.
- **[docs/ROADMAP.md](./docs/ROADMAP.md)** — full roadmap with priorities, status, and tech-debt backlog.
- **[docs/COMPONENT_CATALOG.md](./docs/COMPONENT_CATALOG.md)** — reusable UI components (StatusBadge, ChatBubble, SessionRow) across Avalonia/Blazor/WPF.
- **[docs/ARCHITECTURE_LAYERS.md](./docs/ARCHITECTURE_LAYERS.md)** — canonical Clean / Hexagonal / Onion layering rules. The allowed/forbidden `<ProjectReference>` matrix is mechanically enforced by `tests/Harbor.Architecture.Tests`. **Read this before adding any `<ProjectReference>` to a `.csproj`.**
- **[docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md)** — detailed audit of OOP/SOLID/GoF/FP/ROP/perf with 41 findings and prioritized refactoring plan, plus §ARCH-001..§ARCH-NNN layering violations. Every `TODO(principles)` in code references this file.
- **[docs/SPECTRE_TUI_DEEP_DIVE.md](./docs/SPECTRE_TUI_DEEP_DIVE.md)** — full anatomy of the interactive shell (`contrib/tui/Harbor.Tui.SpectreTui`; compiled into the default CLI build) for adding features from opencode/kilocode/pi-agent.
- **[docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md)** — high-level design + principles summary.
- **[docs/DEVELOPMENT.md](./docs/DEVELOPMENT.md)** — how to contribute + **principles checklist** for PRs.
- **[docs/PATTERNS.md](./docs/PATTERNS.md)** — 18 pattern catalog with real code (Strategy, Registry, Observer, Builder, Adapter, Command, Specification, Value Object, Factory, Plugin, Repository, Chain of Resp, Flyweight, Object Pool, MVVM, Decorator, TEA, DU).
- **[docs/ANTIPATTERNS.md](./docs/ANTIPATTERNS.md)** — 38 antipatterns we forbid (with before/after code).
- **[docs/EXAMPLES.md](./docs/EXAMPLES.md)** — 40+ recipes ("How do I...?").
- **[docs/TOOLS_CATALOG.md](./docs/TOOLS_CATALOG.md)** — comprehensive reference for all 14 builtin tools: args schema, 3+ examples per tool, "when to use X vs Y" matrix, tool chains, JSON Schema tips, permission rationale, sub-agent `task` deep dive, MCP integration, and a full WebFetchTool walkthrough.
- **[docs/PLUGIN_DEVELOPMENT.md](./docs/PLUGIN_DEVELOPMENT.md)** — Roslyn `.cs` plugin system + 5 full examples.

> **TL;DR for AI agents:** read this file for conventions → read AGENTS.md for operational steps → consult CODE_PRINCIPLES_AUDIT.md before refactoring hot paths.

## Project overview

Harbor is a modular .NET 10 AI coding agent harness. The architecture prioritizes:
1. **Modularity** — every concern behind an interface, swappable via DI.
2. **NativeAOT-readiness** — Core can be published as NativeAOT; TUI runs JIT.
3. **Low memory footprint** — <30MB RSS idle target (vs 1GB+ for Node.js equivalents).
4. **Plugin extensibility** — tools, providers, agents, UI all extensible.
5. **Performance** — `FrozenDictionary`, `ArrayPool`, `IReadOnlyCollection`, zero unsafe.

## Solution layout

```
src/                                   — ~50 projects (all included in Harbor.slnx unless noted)
├── Harbor.Abstractions/               — base contracts (zero deps)
├── Harbor.Abstractions.Contracts/     — models, events, ValueObjects, PermissionRuleset
├── Harbor.Core/                       — EventBus, AgentLoop, config, onboarding, compaction
├── Harbor.Registries/                 — Agent/Tool/Provider registries (builtin agents: code, plan, explore)
├── Harbor.Application/                — sessions, permissions, configuration
├── Harbor.Hosting/                    — DI modules wired by the CLI (TuiModule, StorageModule, ...)
├── Harbor.Storage.{Jsonl,Memory,Sqlite}/  — session stores (HARBOR_STORAGE=jsonl|memory|sqlite)
├── Harbor.Providers.{Anthropic,OpenAI,Ollama,OpenAiCompatible,Shared}/ — LLM clients
├── Harbor.Tools.Builtin/              — 14 builtin tools under Tools/
│                                        (read/write/edit/bash/glob/grep/ls/task + webfetch/patch/notebook/ripgrep/tree/mcp)
├── Harbor.Terminal.Abstractions/      — ITuiRenderer, ITuiRenderContext, BaseTuiRenderer, views/VMs
├── Harbor.Tui.{Ansi,Plain}/           — ANSI streaming + plain-text renderers
├── Harbor.Tui.ConsoleEx/              — second in-process terminal renderer (raw-mode input,
│                                        cell-diff output; opt-in via HARBOR_TUI=consoleex)
├── Harbor.Tui.Notifications/          — desktop OS notifications renderer
├── Harbor.Ui.Framework*/              — TEA-style UI state/reducers/projection/services shared by apps
├── Harbor.Plugins.{Abstractions,Compilation,Instantiation,Registration,
│                    Hosting,Runtime,Host,Storage}/  — layered plugin hosting (Roslyn CS loader in Runtime)
├── Harbor.Ipc.{Abstractions,InProcess,Client,Server}/ + Harbor.Transport.Remote/ — daemon/remote IPC
└── ...Logging, Telemetry.{Core,Otlp}, Desktop.{Abstractions,Shared,Animations}, CodeGen, ...

apps/Harbor.App.Cli/                   — CLI entry point, slash commands, REPL, HostBuilder DI root
apps/Harbor.App.Avalonia/              — cross-platform desktop GUI

contrib/tui/                           — extra interactive shells compiled into the default CLI build:
                                         SpectreTui shell, Spectre.Fullscreen, TerminalGui, Termina, RazorConsole
contrib/apps/, contrib/scripting/      — WPF/Maui/Blazor apps + scripting stack (own Contrib.slnx)

samples/plugins/                       — 4 DLL-based sample plugins (WebSearch, TodoWrite, GitTools, FileTree)
samples/plugins-cs/                    — CS-source sample plugin (HelloWorldPlugin.cs)
samples/mcp/                           — sample MCP servers (node/python/rust/csharp-hello)

tests/                                 — 27 test/bench project directories (TUnit), incl. shared Harbor.TestKit
providers/   — 13 JSON LLM provider configs (embedded via <EmbedProviders>)
specs/       — design specification documents
docs/        — architecture, benchmarks, build, dev, getting started, plugin dev, roadmap, ...
```

## Layering rules — Clean / Hexagonal / Onion Architecture

> **Canonical reference:** [docs/ARCHITECTURE_LAYERS.md](./docs/ARCHITECTURE_LAYERS.md).
> **Mechanically enforced by:** `tests/Harbor.Architecture.Tests` (46 tests: 21 reflection-based + 25 NetArchTest-based).
> **User mandate:** *"слои должны быть по чистой архитектуре, гексогональная луковая называй как хочешь но это надо"*.

Harbor follows **Clean / Hexagonal / Onion Architecture**. The dependency direction is
**inward only**: an outer layer may reference an inner layer, never the reverse. The
innermost layer (Domain/Abstractions) references nothing but the BCL.

| Layer            | Harbor projects                                                                                              | May reference                                |
|------------------|--------------------------------------------------------------------------------------------------------------|----------------------------------------------|
| **Domain**       | `Harbor.Abstractions`, `Harbor.Tui.Abstractions`                                                             | BCL only (no other Harbor project, except Tui.Abstractions → Abstractions) |
| **Application**  | `Harbor.Core`, `Harbor.Plugins.Runtime`, `Harbor.Scripting`                                                  | Domain only (NOT each other, NOT Infrastructure, NOT Presentation) |
| **Infrastructure** | `Harbor.Storage.*`, `Harbor.Providers.*`, `Harbor.Tools.Builtin`                                           | Domain only (NOT `Harbor.Core`, NOT each other) |
| **Presentation** | `Harbor.App.Cli`, `Harbor.App.Avalonia`, `Harbor.Tui.Plain/Ansi/ConsoleEx/Notifications`, `contrib/tui/*` (SpectreTui shell, Fullscreen, TerminalGui, Termina, RazorConsole) | Domain only (NOT Application, NOT Infrastructure, NOT each other) |
| **Composition Root** | `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` (+ `src/Harbor.Hosting/Modules/*`) | Everything — the ONLY place that `new`s concrete impls |

### Hard rules (CI-enforced via `Harbor.Architecture.Tests`)

1. `Harbor.Abstractions` references ZERO other Harbor assemblies.
2. `Harbor.Tui.Abstractions` references only `Harbor.Abstractions`.
3. `Harbor.Core` references only `Harbor.Abstractions`.
4. `Harbor.Plugins.Runtime` references `Harbor.Abstractions` + `Harbor.Tui.Abstractions` only.
5. `Harbor.Scripting` (moved to `contrib/scripting`) references `Harbor.Abstractions` only.
6. `Harbor.Providers.*` references `Harbor.Abstractions` only — **NOT** `Harbor.Core`.
7. `Harbor.Storage.*` references `Harbor.Abstractions` only — **NOT** `Harbor.Core`.
8. `Harbor.Tools.Builtin` references `Harbor.Abstractions` only — **NOT** `Harbor.Core`.
9. `Harbor.Tui.*` concrete renderers reference `Harbor.Abstractions` + `Harbor.Tui.Abstractions` only.
10. `Harbor.App.Cli` may reference everything (it is the Composition Root host).

### Soft rules (code-review-enforced — not mechanically checkable)

- Concrete impl types (`AnthropicLlmClient`, `JsonlSessionStore`, `AgentLoop`,
  `DefaultAgent`, `InMemoryEventBus`, etc.) are `new`'d only inside
  `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` and the DI modules in
  `src/Harbor.Hosting/Modules/`. `Program.cs` resolves them from DI by interface.
- New interfaces go in `Harbor.Abstractions` (or `Harbor.Tui.Abstractions` for UI-only
  contracts), never in `Harbor.Core`.
- New value objects / records go in `Harbor.Abstractions/Models`, never in `Harbor.Core`.
- Application projects must not cross-reference each other (e.g. `Harbor.Scripting` must
  not reference `Harbor.Core`). If two Application projects need to share a type, the type
  belongs in Domain.

### Interface Segregation (ISP) — §ARCH-002

`IAgent` is split into `IAgentRunner` (minimal runner surface: `PromptAsync(string, ct)`,
`AbortSource`, `WaitForIdleAsync`) and `IAgent : IAgentRunner, IDisposable` (adds
`State`, `Subscribe`, `Steer`, `FollowUp`, `Initialize`, `PromptAsync(UserMessage, ct)`).
Callers that only need to "send a prompt and wait for idle" take `IAgentRunner`. This
keeps `Harbor.Tui.Abstractions` in the Domain layer — it never needs to reference
`Harbor.Core` for agent types.

### Before adding a `<ProjectReference>` — checklist

1. **Identify the layer** of the project you are editing (see table above).
2. **Identify the layer** of the project you want to reference.
3. **Check the matrix** in [ARCHITECTURE_LAYERS.md §2](./docs/ARCHITECTURE_LAYERS.md#2-allowed-and-forbidden-project-references).
   If the cell is ❌, you have a layering violation — fix the design first.
4. **Run `dotnet run --project tests/Harbor.Architecture.Tests/`** — if your reference is
   forbidden, the test for that project will fail with a clear list of violations.
5. **Update `Harbor.Architecture.Tests`** if the new project creates a new sub-category.

## Code conventions

### Naming
- **PascalCase** for public members, types, namespaces.
- **camelCase** for locals, parameters.
- **_camelCase** for private fields.
- File-scoped namespaces everywhere.
- `var` only when type is obvious.

### Patterns
- **CSharpFunctionalExtensions** `Result<T>` for operations that can fail. Avoid throwing exceptions for expected failures.
- **ValueObject** for strongly-typed identifiers: `SessionId`, `ProviderId`, `ToolName`, etc.
- **Records** for immutable data: `Session`, `AgentMessage`, `LlmEvent`.
- **Sealed classes** by default. Open for inheritance only when explicitly designed for it.
- **Interfaces** start with `I` (e.g. `ITool`, `ILlmClient`).

### Railway Oriented Programming (ROP)

Every operation that can fail returns `Result<T>` (or `Result`) with **two tracks**: a
*success* track and a *failure* track. The failure track short-circuits through `.Bind()`
/ `.Ensure()` / `.Map()` without nested `try`/`catch`. The success track is the happy path.

```csharp
// ❌ Don't throw for expected failures:
public Session Load(string id)
{
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("id");
    return _store.Load(id) ?? throw new NotFoundException(id);
}

// ✅ Return Result<T> and chain:
public Result<Session> Load(string? id) =>
    SessionId.TryCreate(id)
        .Bind(sid => _store.GetAsync(sid))
        .Ensure(s => s is not null, "session not found");

// ✅ Caller pattern-matches:
var result = Load(maybeId);
if (result.IsFailure) { logger.LogWarning(result.Error); return; }
UseSession(result.Value);
```

Rules:
- **All** public APIs that can fail return `Result<T>` / `Result`.
- Throw exceptions only for *truly exceptional* conditions (corrupted state, NRE).
- Use `Result.Success(value)` / `Result.Failure<T>(error)`.
- Chain with `.Bind()` (flatMap), `.Map()` (select), `.Ensure(predicate, error)`.
- `Value` throws if accessed on a failure; check `IsSuccess`/`IsFailure` first.
- Don't `catch` and re-wrap exceptions inside library code — propagate the `Result`.

### ZLinq

[ZLinq](https://github.com/Cysharp/ZLinq) is a zero-allocation LINQ replacement built on
`Span<T>`. Harbor's existing hot paths use manual `for` loops and pooled buffers to avoid
LINQ's per-call iterator + delegate allocations. **For new hot-path code, prefer ZLinq
over `System.Linq`** when:

- The sequence is enumerated >1,000 times per second.
- A benchmark shows `IEnumerable<T>` allocations dominating.
- The pipeline chains 3+ operators (`Select` → `Where` → `OrderBy` → …).

```csharp
using ZLinq;

// Standard LINQ — allocates iterator + delegate per call.
var names = tools.Where(t => ...).Select(t => t.Name.Value).ToArray();

// ZLinq — zero allocation on the hot path.
var names = tools.AsValueEnumerable().Where(t => ...).Select(t => t.Name.Value).ToArray();
```

Harbor does not yet hard-depend on ZLinq; add the package to a project only when a
benchmark justifies it. The library code is structured so that swapping `using System.Linq;`
for `using ZLinq;` + `.AsValueEnumerable()` is mechanical.

### CommunityToolkit.Mvvm patterns

TUI view models live in `Harbor.Terminal.Abstractions/ViewModels/` (with the TEA-style
shared VMs in `Harbor.Ui.Framework.ViewModels`) and inherit from
`ObservableObject`. Use the source generators — don't write INPC by hand:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

public sealed partial class MyViewModel : ObservableObject, ITuiViewModel
{
    // Generates a public observable property `Text` backed by `_text`.
    [ObservableProperty]
    private string _text = string.Empty;

    // Generates a `SubmitCommand` property of type IRelayCommand.
    [RelayCommand]
    private void Submit() { /* ... */ }

    // CanExecute is wired automatically from the named method.
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() { /* ... */ }
    private bool CanNext() => true;

    // Cross-property command invalidation:
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private int _currentIndex = -1;

    public string Id => "my-vm";
    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // Pattern-match on the event type and mutate observable properties.
        return Task.CompletedTask;
    }
}
```

Rules:
- The class MUST be `partial` (the source generator writes the other half).
- The backing field MUST be `_camelCase` and named `<PropertyName>` with leading underscore.
- Commands surface as `<MethodName>Command` (e.g. `Submit()` → `SubmitCommand`).
- View models are renderer-agnostic — never reference `Console` or `ITuiRenderContext`.
- `ITuiViewModel.UpdateFromEventAsync` is the **only** way agent events enter a VM.

### TUI plugin architecture (views / view models / renderers)

The TUI layer is a strict MVVM triad: **renderers** dispatch events to **view models**,
which hold state; **views** read VM state at render time and emit characters through the
**render context**. Nothing in this triad references `Harbor.Core` — agent state flows in
only through `AgentEvent` from `Harbor.Abstractions.Events`.

```
        ┌────────────────────────────────────────────────────────────┐
        │                     ITuiRenderer                           │
        │   (AnsiTuiRenderer / PlainTuiRenderer / SpectreTuiRenderer) │
        │                                                            │
        │   ┌────────────────┐      ┌──────────────────┐              │
        │   │  ViewRegistry  │◀────▶│ ViewModelRegistry│              │
        │   └────────────────┘      └──────────────────┘              │
        │           │                          ▲                       │
        │           │ binds by id              │ UpdateFromEventAsync │
        │           ▼                          │                       │
        │   ┌────────────────┐                  │                       │
        │   │   ITuiView     │──────────────────┘                       │
        │   │ (StatusBar,    │                                          │
        │   │  ChatHistory,  │   RenderAsync(ITuiRenderContext)         │
        │   │  Input, Diff)  │──────────────────────────────────────────▶│
        │   └────────────────┘                                          │
        └────────────────────────────────────────────────────────────┘
                                       ▲
                                       │ ITuiPlugin.RegisterTui(views, viewModels)
                                       │
                              ┌────────────────────┐
                              │   ITuiPlugin       │
                              │  (e.g. ClockPlugin)│
                              └────────────────────┘
```

Extension points:

- **Add a view** — implement `ITuiView` (or derive from `TuiViewBase<TViewModel>`), set a
  unique `Id` and `TuiViewPlacement`, and register it via `ViewRegistry.Register`. The
  base renderer auto-binds it to the matching VM by id.
- **Override a builtin view** — register a view with one of the builtin ids
  (`status-bar`, `chat-history`, `input`, `diff-preview`) *before*
  `BaseTuiRenderer.InitializeAsync` runs. The builtin is skipped when the id is taken
  (override-before-builtin).
- **Add a view model** — implement `ITuiViewModel` (or derive from `ObservableObject` +
  `ITuiViewModel`) and register it via `ViewModelRegistry.Register`. The base renderer
  fans every `AgentEvent` out to all VMs via `UpdateFromEventAsync`.
- **Custom placement logic** — override `BaseTuiRenderer.ShouldRenderPlacement` to control
  which events trigger a repaint of each placement.

Decoupling contract: **never** import `Harbor.Core` from a TUI assembly. All agent state
arrives via `AgentEvent`; all rendering goes through `ITuiRenderContext`.

### SOLID principles
- **S**ingle Responsibility — each class does one thing.
- **O**pen/Closed — extend via plugins, don't modify core.
- **L**iskov Substitution — any `ITool`/`ILlmClient`/`ITuiRenderer`/`ISessionStore` can replace another.
- **I**nterface Segregation — small, focused interfaces (`ITool`, `IToolPlugin`, `IProviderPlugin`).
- **D**ependency Inversion — depend on `Harbor.Abstractions`, not implementations.

#### Before / after — SOLID

**SRP (Single Responsibility):**

```csharp
// ❌ WRONG — god class doing 5 things
public sealed class AgentLoop
{
    public async Task RunAsync(...) { /* orchestrate */ }
    public void CoalesceStreaming(...) { /* parse */ }
    public void DispatchToolCalls(...) { /* tool dispatch */ }
    public void PublishEvents(...) { /* event publishing */ }
    public void CheckPermissions(...) { /* permissions */ }
}

// ✅ RIGHT — split into focused collaborators
public sealed class AgentLoop { /* orchestration only */ }
public sealed class StreamingCoalescer { /* text/thinking/tool-call accumulation */ }
public sealed class ToolCallDispatcher { /* tool execution */ }
public sealed class TurnEventPublisher { /* event publishing */ }
```

**OCP (Open/Closed):**

```csharp
// ❌ WRONG — switch on type, must edit when adding new provider
switch (providerId.Value)
{
    case "deepseek":  BuildDeepSeekRequest(req);  break;
    case "groq":      BuildGroqRequest(req);      break;
}

// ✅ RIGHT — strategy interface, add new providers without editing dispatcher
public interface IProviderCompatFlags { void CustomizeRequest(LlmRequest req); }
public sealed class DeepSeekCompat : IProviderCompatFlags { /* ... */ }
// dispatcher just calls: _compat[providerId].CustomizeRequest(req);
```

**LSP (Liskov):**

```csharp
// ❌ WRONG — subclass throws for methods it doesn't support (LSP violation)
public class ReadOnlyStore : ISessionStore
{
    public Task<Result> SaveAsync(...) => throw new NotSupportedException();
}

// ✅ RIGHT — interface split so impl doesn't have unsupported methods
public interface IReadOnlySessionStore { Task<Result<Session>> LoadAsync(string id, ...); }
public interface ISessionStore : IReadOnlySessionStore { Task<Result> SaveAsync(...); }
// Then ReadOnlyStore implements only IReadOnlySessionStore.
```

**ISP (Interface Segregation):**

```csharp
// ❌ WRONG — fat interface forcing impl to throw
public interface ITool
{
    Task<ToolResult> ExecuteAsync(...);
    Task<ToolResult> ExecuteStreamingAsync(...);   // not all tools stream
    Task<ToolResult> ExecuteBatchAsync(...);       // not all tools batch
}
public sealed class ReadTool : ITool
{
    public Task<ToolResult> ExecuteBatchAsync(...) => throw new NotSupportedException();
}

// ✅ RIGHT — segregated interfaces
public interface ITool { Task<ToolResult> ExecuteAsync(...); }
public interface IStreamingTool : ITool { IAsyncEnumerable<ToolResult> StreamAsync(...); }
public interface IBatchTool : ITool { Task<IReadOnlyList<ToolResult>> ExecuteBatchAsync(...); }
```

**DIP (Dependency Inversion):**

```csharp
// ❌ WRONG — high-level module depends on low-level concrete class
public sealed class AgentLoop
{
    private readonly JsonlSessionStore _store = new();   // ← concrete dep
    private readonly OpenAiCompatibleLlmClient _client;  // ← concrete dep
}

// ✅ RIGHT — depend on abstractions
public sealed class AgentLoop
{
    private readonly ISessionStore _store;        // interface from Harbor.Abstractions
    private readonly ILlmClient _client;          // interface from Harbor.Abstractions
    public AgentLoop(ISessionStore store, ILlmClient client, ...) { _store = store; _client = client; }
}
```

> **Known violations:** see [docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md) — 8 OOP/SOLID findings (2 critical: §OOP-001 thread-safety in `OpenAiCompatibleLlmClient`, §SOLID-001 god-class in `AgentLoop`).

### GOF patterns used
- **Strategy**: `ILlmClient`, `ITool`, `ITuiRenderer`, `ISessionStore` — swap implementations.
- **Registry**: `ProviderRegistry`, `ToolRegistry`, `AgentRegistry`, `ViewRegistry`, `ViewModelRegistry` — with `FrozenDictionary` for O(1) lookups.
- **Observer**: `IEventBus`, `InMemoryEventBus` — pub/sub with `ImmutableArray` snapshot.
- **Builder**: `ISystemPromptBuilder`, `IToolRegistryBuilder`, `IProviderRegistryBuilder`, `IAgentRegistryBuilder`.
- **Adapter**: `MessageConverter`, `OpenAiCompatibleLlmClient`, `ConfigAuthResolver`.
- **Command**: `IAgent`, `DefaultAgent`, `TaskTool`, `ISlashCommand` implementations.
- **Specification**: `PermissionRuleset` — encapsulates permission evaluation logic.
- **Value Object**: `SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName` (7 types via CSharpFunctionalExtensions).
- **Factory Method**: `Session.Create`, `ToolResult.Success/Error`, `ProviderId.TryCreate`.
- **Plugin**: `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin` (5 contracts).
- **Repository**: `ISessionStore` — abstracts persistence (Jsonl, Memory, Sqlite).
- **Chain of Responsibility**: `AgentLoop` (prompt → LLM stream → tool execution → next turn → compaction).
- **Flyweight**: `StringPool.Shared.GetOrAdd()` for tool name interning in `AgentLoop`.
- **Object Pool**: `StringBuilderPool`, `ArrayPool<T>.Shared` via `ArrayPoolExtensions.RentScoped`.
- **MVVM**: `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` via CommunityToolkit.Mvvm (28 usages).
- **Decorator**: `BaseTuiRenderer` decorates concrete renderers with view dispatch.

### Functional programming (FP)
- `Result<T>` for error handling — Railway Oriented Programming (ROP), no exceptions for expected failures.
- Immutable `record` types for messages, events, value objects.
- Pure functions where possible (e.g. `HeuristicTokenEstimator.Estimate`).
- `IAsyncEnumerable<T>` for streaming (LLM events, tool progress).
- Pattern matching with `switch` expressions on discriminated unions (`AgentEvent`, `LlmEvent`).
- `Channel<T>` for lock-free producer-consumer (FP-inspired message passing).

#### Before / after — FP

**Immutability:**

```csharp
// ❌ WRONG — mutable class with setters
public class Session
{
    public string Id { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }   // anyone can mutate
    public SessionMetadata Metadata { get; set; }
}
session.UpdatedAt = DateTimeOffset.UtcNow;   // ← silent mutation

// ✅ RIGHT — immutable record with `with` for changes
public sealed record Session(
    string Id,
    DateTimeOffset UpdatedAt,
    SessionMetadata Metadata);

var updated = session with { UpdatedAt = DateTimeOffset.UtcNow };   // explicit copy
```

**Pure functions:**

```csharp
// ❌ WRONG — side effects inside "pure" function
public static UiState Reduce(UiState state, AgentEvent e)
{
    File.AppendAllText("log.txt", e.ToString());   // ← I/O side effect
    return state with { Status = "running" };
}

// ✅ RIGHT — pure, side-effect-free
public static UiState Reduce(UiState state, AgentEvent e) => e switch
{
    AgentStartEvent => state with { Status = "running" },
    _ => state
};
// I/O goes in TuiEffectHost, not in the reducer.
```

**Fire-and-forget:**

```csharp
// ❌ WRONG — fire-and-forget, errors silently swallowed
_ = _eventBus.PublishAsync(evt, ct);

// ✅ RIGHT — await, or use ContinueWith(OnlyOnFaulted)
await _eventBus.PublishAsync(evt, ct).ConfigureAwait(false);

// ✅ OK — fire-and-forget with explicit fault handler
_ = Task.Run(async () =>
{
    try { await SomeAsync(ct); }
    catch (Exception ex) { _logger.LogError(ex, "Background task failed"); }
});
```

> **Known violations:** see [docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md) — 7 FP findings (1 critical: §FP-003 fire-and-forget in `AgentLoop.ToolContext.ReportProgress` + §FP-006 fire-and-forget in `TuiEffectHost.Run`). Also §FP-005 mutable `_scroll`/`_viewport` in `ChatScreen.Render` breaks TEA invariant.

### ROP (Railway Oriented Programming)

#### Before / after — ROP

```csharp
// ❌ WRONG — exceptions for expected failure
public Session Load(string id)
{
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("id");
    return _store.Load(id) ?? throw new NotFoundException(id);
}

// ✅ RIGHT — Result<T> for expected failure
public Result<Session> Load(string? id) =>
    SessionId.TryCreate(id)
        .Bind(sid => _store.GetAsync(sid))
        .Ensure(s => s is not null, "session not found");

// ❌ WRONG — `.Value` without `IsSuccess` check (crash on failure)
var agent = _agents.GetAgent(AgentName.TryCreate(agentName).Value);

// ✅ RIGHT — pattern-match
var nameResult = AgentName.TryCreate(agentName);
if (nameResult.IsFailure) return Result.Failure<PermissionResponse>(nameResult.Error);
var agentResult = _agents.GetAgent(nameResult.Value);
if (agentResult.IsFailure) return Result.Failure<PermissionResponse>(agentResult.Error);

// ❌ WRONG — returning null on failure
public AgentMessage? DeserializeMessage(string json)
{
    try { return JsonSerializer.Deserialize<AgentMessage>(json); }
    catch { return null; }
}

// ✅ RIGHT — Result<T> with error
public Result<AgentMessage> DeserializeMessage(string json)
{
    try { return Result.Success(JsonSerializer.Deserialize<AgentMessage>(json)!); }
    catch (JsonException ex) { return Result.Failure<AgentMessage>(ex.Message); }
}
```

### Performance

#### Before / after — Performance

**LINQ on hot path:**

```csharp
// ❌ WRONG — LINQ allocates iterator + delegate per call
foreach (var d in tools.Where(t => t.ExecutionMode == ExecutionMode.Parallel)
                       .Select(ToDescriptor)) { /* ... */ }

// ✅ RIGHT — manual for loop
for (int i = 0; i < tools.Count; i++)
{
    var t = tools[i];
    if (t.ExecutionMode == ExecutionMode.Parallel) { var d = ToDescriptor(t); /* ... */ }
}
```

**Reflection JSON:**

```csharp
// ❌ WRONG — reflection, IL2026 under AOT
var json = JsonSerializer.Serialize<Dictionary<string, object?>>(obj);

// ✅ RIGHT — Utf8JsonWriter or JsonSerializerContext
using var stream = new MemoryStream();
using var writer = new Utf8JsonWriter(stream);
writer.WriteStartObject();
writer.WriteString("name", obj.Name);
writer.WriteEndObject();
await writer.FlushAsync(ct);
```

**Per-line JsonDocument.Parse:**

```csharp
// ❌ WRONG — 10k allocations per file
foreach (var line in File.ReadLines(path))
{
    using var doc = JsonDocument.Parse(line);   // alloc per line
    var type = doc.RootElement.GetProperty("type").GetString();
}

// ✅ RIGHT — Utf8JsonReader (struct, zero alloc)
foreach (var line in File.ReadLines(path))
{
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
    while (reader.Read())
    {
        if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("type"))
        {
            reader.Read();
            var type = reader.GetString();
        }
    }
}
```

**Unpooled buffers:**

```csharp
// ❌ WRONG — alloc per call
var sb = new StringBuilder();
foreach (var s in items) sb.AppendLine(s);

// ✅ RIGHT — pooled
using var sb = StringBuilderPool.Rent();
foreach (var s in items) sb.Builder.AppendLine(s);
```

**`string.Split` on hot path:**

```csharp
// ❌ WRONG — allocates string[] + N substrings
foreach (var part in line.Split(',')) { /* ... */ }

// ✅ RIGHT — span-based, zero alloc
ReadOnlySpan<char> span = line.AsSpan();
int start = 0;
while (start < span.Length)
{
    int comma = span.Slice(start).IndexOf(',');
    if (comma < 0) { Process(span.Slice(start)); break; }
    Process(span.Slice(start, comma));
    start += comma + 1;
}
```

### Async
- `async`/`await` everywhere. No `.Result`, no `.Wait()`.
- `CancellationToken` passed to all async methods.
- `IAsyncEnumerable<T>` for streaming (LLM events, tool progress).
- `ConfigureAwait(false)` in library code.
- Use `Channel<T>` for producer-consumer (see `OpenAiCompatibleLlmClient`).

### JSON
- `System.Text.Json` only. No Newtonsoft.
- Source-gen `JsonSerializerContext` for AOT-compatibility (planned).
- `JsonPropertyName` attributes for snake_case JSON properties on internal records.

### Error handling
- `Result.Success(value)` / `Result.Failure<T>(error)` for expected failures.
- Throw exceptions only for truly exceptional conditions (null refs, corrupted state).
- `try`/`catch` only at boundaries (HTTP, file I/O, process spawn).
- Log caught exceptions with `ILogger<T>`.
- **Never use `yield` inside `try`/`catch`** — C# forbids it. Extract to a separate method (see `MapChunkFromDocument` pattern).

### Performance

Harbor is performance-obsessed. These techniques are applied throughout:

| Technique | Where | Count |
|---|---|---|
| `FrozenDictionary` / `FrozenSet` | ProviderRegistry, ToolRegistry (after `Freeze()`) | 20 refs |
| `NonBlocking.ConcurrentDictionary` | ProviderRegistry, ToolRegistry, AgentRegistry (lock-free) | 9 refs |
| `ArrayPool<T>.Shared` | InMemoryEventBus (dead subscribers), ProviderRegistry (task array) | 12 refs |
| `StringBuilderPool` | AgentLoop (streaming coalesce), CompactionService, SystemPromptBuilder | 6 refs |
| `Channel<T>` | EventBus scrollback, DefaultAgent steering/followup, all LLM clients | 7 refs |
| `ImmutableArray<T>` | InMemoryEventBus subscriptions (atomic lock-free snapshot) | 6 refs |
| `StringPool.Shared` | AgentLoop (tool name interning / flyweight) | 3 refs |
| `[StructLayout(Sequential)]` | InMemoryEventBus.Subscription (cache-friendly iteration) | 1 ref |
| `ConfigureAwait(false)` | All async library code | 193 refs |
| `MemoryPack` `[MemoryPackable]` | All domain models (Session, Messages, Usage, etc.) | 27 refs |
| `ZLinq` drop-in | Harbor.Core (replaces System.Linq) | 4 refs |
| `IReadOnlyCollection<T>` | Public APIs (no defensive copies) | 43 refs |
| Pre-sized `List<T>(capacity)` | All List allocations in Core | verified |

Rules:
- Use `FrozenDictionary` / `FrozenSet` for read-only collections built once.
- Use `IReadOnlyCollection<T>` / `IReadOnlyList<T>` in public APIs.
- Use `ArrayPool<T>.Shared` for rented buffers (see `ArrayPoolExtensions`).
- Use `StringBuilderPool.Rent()` for concatenation in hot paths.
- Use `Channel<T>` for backpressure-aware streaming.
- Prefer manual `for` loops or ZLinq over `System.Linq` in hot paths.
- Use `ArgumentList.Add(arg)` instead of `Arguments = "..."` for `ProcessStartInfo`.
- **No `unsafe` code** — verified by `MA0046` analyzer (set to error).

> **Known performance / low-level issues:** see [docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md) §5–§6 — 9 perf + 6 low-level findings. Top-3: §PERF-002 reflection-based JSON in `BuildRequest`, §PERF-005 per-line `JsonDocument.Parse` in `GetMessagesAsync`, §PERF-007 `lock` per `UiStore.Dispatch`. Hot-path code MUST use `Utf8JsonReader` + `JsonSerializerContext` source-gen, not reflection.

### Full-screen TUI development

`FullscreenTuiRenderer` (physically in `contrib/tui/Harbor.Tui.Spectre.Fullscreen`,
compiled into the default CLI build) is a full-screen interactive
renderer using Spectre.Console 0.57.2 `Live` display. It owns the REPL lifecycle.

| Hotkey | Action |
|---|---|
| `Enter` | Submit prompt |
| `Alt+Enter` | Newline (multi-line input) |
| `↑` / `↓` | Input history navigation (when idle) / scroll chat (when agent running) |
| `PageUp` / `PageDown` | Scroll chat history by 10 lines |
| `Home` / `End` | Scroll to top / bottom of chat |
| `Tab` | Autocomplete slash-commands |
| `Ctrl+L` | Refresh screen |
| `Ctrl+C` | Force quit |
| `Esc` | Abort agent (when running) / clear input (when typing) / quit (when empty) |

Features:
- **Scrollable chat history** with `▲ N lines above` / `▼ N lines below` indicators.
- **Markdown rendering**: code blocks (```` ``` ````), `**bold**`, `*italic*`, `` `code` ``,
  `# headers`, `- lists`, `1. numbered`, `[links](url)`.
- **Word-aware wrapping** (breaks at word boundaries, falls back to char wrap for long tokens).
- **Input history** persisted per session.
- **Visual polish**: boxed messages with role icons (👤 🤖 🔧 📦 ❌ ⚙️), color-coded borders.
- **Status bar**: provider/model, agent, cost, tokens, status with color-coded icon.

When adding a new interactive renderer, implement `IInteractiveTuiRenderer` — `Program.cs`
will delegate the REPL to it instead of running the default line-buffered loop.

## Build & test

```bash
# Build everything
dotnet build

# Run a specific test project — tests MUST be run per project, as plain
# executables (`dotnet test` discovers zero tests in this repo — broken MTP
# bridge). TUnit uses --treenode-filter (forwarded after --), NOT --filter.
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --minimum-expected-tests 1

# Run the CLI
dotnet run --project apps/Harbor.App.Cli -- help
```

## Common tasks

### Add a new builtin tool

> **Full reference:** [docs/TOOLS_CATALOG.md](./docs/TOOLS_CATALOG.md) — every builtin
> tool's args schema, 3+ examples, "when to use X vs Y" matrix, and a complete
> WebFetchTool walkthrough showing how to design a tool from scratch.

1. Create `src/Harbor.Tools.Builtin/Tools/<Name>/<Name>Tool.cs`.
2. Implement `ITool` interface (sealed class, XML docs on every public member,
   `ConfigureAwait(false)` everywhere, `ArrayPool`/`StringBuilderPool` for buffers).
3. Register in `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` — in `CreateToolRegistry`,
   `tb.AddTool(() => new YourTool(loggerFactory.CreateLogger<YourTool>()))`.
   If the tool needs a DI dependency, construct it eagerly and pass the instance —
   `ToolContext.Services` is not populated by the default `AgentLoop` (see
   `McpToolTool` registration for the pattern).
4. Add a permission rule to `PermissionRuleset.Default` in
   `src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs`.
5. Add tests in `tests/Harbor.Tools.Builtin.Tests/<Name>ToolTests.cs` — one file per
   tool. See `WebFetchToolTests.cs` / `McpToolToolTests.cs` for canonical patterns.
6. Build + test.

See `src/Harbor.Tools.Builtin/Tools/Read/ReadTool.cs` as a reference.

Minimal real example:

```csharp
// src/Harbor.Tools.Builtin/Time/TimeTool.cs
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;

namespace Harbor.Tools.Builtin;

public sealed class TimeTool : ITool
{
    public ToolName Name => ToolName.Create("time");
    public string DisplayName => "Time";
    public string Description => "Returns current UTC time";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "time: Get current UTC time";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
    public JsonDocument ParameterSchema =>
        JsonDocument.Parse("""{"type":"object","properties":{}}""");
    public Result ValidateArguments(JsonElement args) => Result.Success();
    public Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct = default)
        => Task.FromResult(ToolResult.Success(DateTimeOffset.UtcNow.ToString("O")));
}
```

Register in `apps/Harbor.App.Cli/Hosting/HostBuilder.cs`:

```csharp
tb.AddTool(() => new TimeTool(loggerFactory.CreateLogger<TimeTool>()));
```

### Add a new LLM provider (JSON-only)

1. Create `providers/<name>.json` with provider config.
2. Set env var `<NAME>_API_KEY`.
3. Done. Provider is auto-discovered at startup.

```jsonc
// providers/myllm.json
{
  "id": "myllm",
  "displayName": "MyLLM",
  "baseUrl": "https://api.myllm.com/v1",
  "apiType": "openai-compatible",
  "authType": "bearer",
  "authEnvVar": "MYLLM_API_KEY",
  "modelsUrl": "https://api.myllm.com/v1/models",
  "modelsPath": "data",
  "modelMapping": { "id": "id", "displayName": "name", "contextWindow": "context_length" }
}
```

```bash
export MYLLM_API_KEY=...
export HARBOR_MODEL=myllm/my-model-id
```

### Add a new native LLM provider

1. Create `src/Harbor.Providers.<Name>/` project.
2. Reference `Harbor.Abstractions.Contracts`.
3. Implement `ILlmClient`.
4. Register in the DI modules (`src/Harbor.Hosting/Modules/`, see `ProviderFactories.cs` / `JsonProviderDiscovery.cs`).
5. Add to solution: `dotnet sln Harbor.slnx add src/Harbor.Providers.<Name>/...`.

```csharp
public sealed class MyLlmClient : ILlmClient
{
    public ProviderId ProviderId { get; } = ProviderId.Create("myllm");

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken ct = default)
        => Result.Success<IReadOnlyList<ModelInfo>>(new[]
        {
            new ModelInfo("my-model", "My Model", 128_000, 8_192)
        });

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextStartEvent("1");
        yield return new TextDeltaEvent("1", "Hello");
        yield return new TextEndEvent("1", "Hello");
        yield return new StepFinishEvent(1, "stop", new Usage(10, 5));
    }
}
```

### Add a new storage backend

1. Create `src/Harbor.Storage.<Name>/` project.
2. Implement `ISessionStore`.
3. Register in the DI modules — the `HARBOR_STORAGE` switch lives in
   `src/Harbor.Hosting/Modules/StorageModule.cs`.
4. Add tests.

```csharp
public sealed class RedisSessionStore : ISessionStore
{
    public Task<Result<Session>> LoadAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
    public Task<Result> SaveAsync(Session session, CancellationToken ct = default) { /* ... */ }
    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) { /* ... */ }
    public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, string? title = null, CancellationToken ct = default) { /* ... */ }
    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) { /* ... */ }
    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
}
```

### Add a new TUI renderer

1. Create `src/Harbor.Tui.<Framework>/` project (or `contrib/tui/...` for optional shells).
2. Implement `ITuiRenderer` from `Harbor.Terminal.Abstractions` (or extend
   `BaseTuiRenderer` / `IInteractiveTuiRenderer` if you need full-screen or
   own the input loop).
3. Register in DI: add to the renderer switch in
   `src/Harbor.Hosting/Modules/TuiModule.cs` (`AddHarborTui`). Also add a
   `ProjectReference` to `Harbor.App.Cli.csproj`.
4. Update `Program.PrintTuiOptions()` to list the new option.
5. Done — no other changes needed (event-bus decoupling).

For the full catalog of built-in renderers (terminal, desktop GUI, web,
mobile, non-interactive) and a comparison table + decision tree, see
**[docs/ALTERNATIVE_UIS.md](./docs/ALTERNATIVE_UIS.md)**. It covers every
renderer including the alternative UI projects: `Harbor.Tui.Wpf`,
`Harbor.Tui.Avalonia`, `Harbor.Tui.Maui`, `Harbor.Tui.Blazor`,
`Harbor.Tui.Sixel`, and `Harbor.Tui.Notifications`.

```csharp
public sealed class MyTuiRenderer : ITuiRenderer
{
    public Task InitializeAsync(CancellationToken ct = default) { /* setup */ return Task.CompletedTask; }

    public Task PublishAsync(AgentEvent e, CancellationToken ct = default)
    {
        // Render the event however you want — Console, GUI, web, etc.
        Console.WriteLine($"[{e.GetType().Name}]");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

### Add a CS-source plugin (preferred)

CS-source plugins are compiled in-memory via Roslyn at startup. No `.csproj`, no DLL.

1. Drop a `.cs` file into `~/.harbor/plugins/` (user-global) or `<project>/.harbor/plugins/` (project-local).
2. The file must contain a public class implementing `IPlugin` (and `IToolPlugin` / `IProviderPlugin` / `IAgentPlugin` / `ITuiPlugin`) with a parameterless constructor.
3. Add `using Harbor.Abstractions.Models.Identifiers;` if using `ToolName`.
4. Add `using Microsoft.Extensions.Logging;` for `LogInformation` extension.
5. The plugin can reference any type already loaded in the host AppDomain (Harbor.Abstractions, System.Text.Json, CSharpFunctionalExtensions, Microsoft.Extensions.Logging, etc.).
6. See `samples/plugins-cs/HelloWorldPlugin.cs` for a canonical example.
7. See [docs/PLUGIN_SYSTEM.md](./docs/PLUGIN_SYSTEM.md) for the full reference.

Compiled assemblies are cached by source SHA-256 in `~/.harbor/plugins/cache/`. Compilation errors are logged to console + `~/.harbor/logs/plugins.log`.

### Add a DLL-based sample plugin (legacy path)

1. Create `samples/plugins/Harbor.Plugin.<Name>/`.
2. Implement `IPlugin` (and `IToolPlugin` / `IProviderPlugin` / `IAgentPlugin`).
3. Reference `Harbor.Abstractions`.
4. Add `Microsoft.Extensions.Logging` package.
5. Add `using Harbor.Abstractions.Models.Identifiers;` if using `ToolName`.
6. Add `using Microsoft.Extensions.Logging;` for `LogInformation` extension.
7. Add to solution.

DLL-based plugins are kept as a legacy alternative for projects that need full .NET project features (NuGet deps, unit tests, strong naming).

## Testing conventions

- **TUnit** is the only test framework. No xUnit, no NUnit.
- Tests live in `tests/<Project>.Tests/`.
- Test methods: `public async Task MethodName_StateUnderTest_ExpectedBehavior()`.
- Use `Assert.That(actual).IsEqualTo(expected)` style.
- For expected exceptions, use `try`/`catch` (TUnit 0.50 `.Throws<T>()` has limitations).
- Tests must be deterministic — no `DateTime.Now`, no network calls (except integration tests).
- Use `Path.GetTempPath()` + GUID for temp files. Clean up in `finally`.

## Gotchas

### NativeAOT
- `AssemblyLoadContext` collectible does NOT work under NativeAOT. Use out-of-process plugins.
- **Roslyn-based CS-source plugins do NOT work under NativeAOT** — `Microsoft.CodeAnalysis.CSharp` requires JIT. CS plugins are JIT-only.
- Reflection-based JSON serialization gives IL2026 warnings. Always use source-gen.
- `Microsoft.Data.Sqlite` pulls in native `e_sqlite3`. JSONL storage avoids this.

Real error if you try `AssemblyLoadContext` collectible under AOT:

```
$ dotnet publish -c Release -r linux-x64 /p:PublishAot=true
ILCompiler.CompilerException: AssemblyLoadContext.LoadFromAssemblyPath is not supported
  in NativeAOT (collectible ALCs require reflection emit).
  at (historical) Harbor.Plugins.DllPluginLoader.Load(string path)
```

Fix: use Roslyn `.cs` plugin loader (works under JIT), or out-of-process plugin host.

### CSharpFunctionalExtensions
- `ValueObject` is a class, not a record. Cannot use record syntax for inheritance.
- `Result<T>` is a struct — be careful with boxing.

Real error — calling `.Value` on a failure Result:

```csharp
var result = AgentName.TryCreate("BAD NAME WITH SPACES");
// result.IsFailure = true, result.Error = "Invalid AgentName..."
var name = result.Value;   // ← throws InvalidOperationException!
// System.InvalidOperationException: Result has no value
//    at CSharpFunctionalExtensions.Result`1.get_Value()
```

Fix: always check `result.IsSuccess` first, or use `result.Match(ok, fail)`.

### TUnit 0.50
- `Assert.That(() => method()).Throws<T>()` doesn't compile. Use try/catch.
- `IsNotNull()` doesn't work for value types. Use `.IsTrue()` or `string.IsNullOrEmpty()` instead.
- Source-generated test discovery — tests must be `public async Task` in a `public class`.

Real compile error:

```
tests/MyTests.cs(15,18): error CS1503: Argument 1: cannot convert from
  'System.Func<System.Threading.Tasks.Task>' to 'System.Action'
   at TUnit.Core.Assert.That(Action actual, ...)
```

Fix — use `try`/`catch`:

```csharp
try { await ThrowsAsync(); /* should throw */ await Assert.That(false).IsTrue(); }
catch (MyException) { /* expected */ }
```

### JSON serialization
- `JsonSerializerDefaults.Web` uses camelCase. Internal records need `[JsonPropertyName]` for PascalCase.
- `JsonElement` from `JsonDocument.Parse` must be consumed before the document is disposed.

Real error — `JsonElement` accessed after `JsonDocument` disposed:

```csharp
JsonElement elem;
using (var doc = JsonDocument.Parse(json)) { elem = doc.RootElement; }
// doc is disposed now
var name = elem.GetProperty("name").GetString();   // ← ObjectDisposedException!
// System.ObjectDisposedException: Cannot access a disposed object.
```

Fix: clone the element before exiting the `using` block:

```csharp
JsonElement elem;
using (var doc = JsonDocument.Parse(json)) { elem = doc.RootElement.Clone(); }
var name = elem.GetProperty("name").GetString();   // ← works
```

### Process invocation
- `ProcessStartInfo.Arguments` is a single string — quoting is tricky.
- Use `ArgumentList.Add(arg)` for safe per-arg passing.
- Always `RedirectStandardError = true` even if you don't expect errors.

Real shell-injection bug (the bad way):

```csharp
var psi = new ProcessStartInfo
{
    FileName = "git",
    Arguments = $"log --author=\"{userInput}\"",   // ← injection: userInput = "\"; rm -rf /; \""
    UseShellExecute = false
};
```

Fix — `ArgumentList.Add`:

```csharp
var psi = new ProcessStartInfo { FileName = "git", UseShellExecute = false };
psi.ArgumentList.Add("log");
psi.ArgumentList.Add($"--author={userInput}");   // ← .NET handles quoting
```

### Streaming with `IAsyncEnumerable`
- **Cannot use `yield` inside `try`/`catch`** — C# forbids it (CS1626).
- Pattern: extract the body to a separate method that returns `IEnumerable<T>`, call it from the `try` block.
- Or use `Channel<T>` for full backpressure-aware streaming (see `OpenAiCompatibleLlmClient`).

Real compile error:

```csharp
public async IAsyncEnumerable<LlmEvent> StreamAsync(...)
{
    try
    {
        yield return new TextDeltaEvent("1", "Hello");   // ← CS1626
    }
    catch (Exception ex)
    {
        yield return new ErrorEvent(ex.Message);          // ← CS1626
    }
}

// error CS1626: Cannot yield a value in the body of a try block with a catch clause
```

Fix — use `Channel<T>`:

```csharp
public async IAsyncEnumerable<LlmEvent> StreamAsync(...)
{
    var channel = Channel.CreateUnbounded<LlmEvent>();
    _ = Task.Run(async () =>
    {
        try { await PumpAsync(channel.Writer, ...); }
        catch (Exception ex) { await channel.Writer.WriteAsync(new ErrorEvent(ex.Message)); }
        finally { channel.Writer.Complete(); }
    });
    await foreach (var e in channel.Reader.ReadAllAsync()) yield return e;
}
```

### Plugin development
- **Prefer CS-source plugins** (drop a `.cs` into `~/.harbor/plugins/`) over DLL-based plugins. See [docs/PLUGIN_DEVELOPMENT.md](./docs/PLUGIN_DEVELOPMENT.md).
- Add `using Harbor.Abstractions.Models.Identifiers;` — `ToolName` lives there, not in `Tools`.
- Add `using Microsoft.Extensions.Logging;` — `LogInformation` is an extension method.
- Use `internal static HttpClient` (not `private`) if accessed from a separate tool class.
- CS plugins run **in-process with full trust** — only drop reviewed source files.
- Compilation errors are logged to console + `~/.harbor/logs/plugins.log`. Bump `HARBOR_LOGLEVEL=Debug` for verbose output.

Real plugin compile error:

```
warn: Harbor.Plugins.Runtime.CsPluginLoader[0]
      Failed to compile ~/.harbor/plugins/webhook.cs:
      (12, 17): error CS0103: The name 'HttpClient' does not exist in the current context
      (15, 32): error CS0246: The type 'JsonDocument' could not be found
```

Fix — add the missing usings at the top of the `.cs` file:

```csharp
using System.Net.Http;
using System.Text.Json;
```

### Provider config
- `id` field must be lowercase alphanumeric + dash.
- JSON configs are embedded as resources via `<EmbedProviders>true</EmbedProviders>` in `Harbor.App.Cli.csproj`.
- After `dotnet publish`, builtin providers work without external JSON files.

Real error — invalid `id` field:

```jsonc
{ "id": "MyLLM", "baseUrl": "..." }   // ← uppercase not allowed
```

```
warn: Harbor.Core.Configuration.JsonConfigStore[0]
      Failed to load provider config 'MyLLM': id must be lowercase alphanumeric + dash.
      Got: 'MyLLM'
```

Fix — use lowercase id:

```jsonc
{ "id": "myllm", "baseUrl": "..." }
```

### Memory leaks

Long-running sessions can leak if:

1. Plugin subscribes to `IEventBus` on every tool call (forgetting to unsubscribe).
   Symptom: `_subscriptions` array grows unbounded in `InMemoryEventBus`.
   Fix: subscribe once in `Initialize`, never in `ExecuteAsync`.

2. `ConcurrentDictionary<sessionId, T>` in a plugin grows without cleanup
   (e.g. TodoWritePlugin never removes entries when session ends).
   Symptom: RSS grows linearly with session count.
   Fix: subscribe to `AgentEndEvent`, remove the session's entry.

3. Streaming `StringBuilder` not returned to pool (forgot `using var`).
   Symptom: `StringBuilderPool` runs out, falls back to `new StringBuilder()`.
   Fix: always `using var buf = StringBuilderPool.Rent();` — never `var buf = ...`.

Diagnose with:

```bash
dotnet tool install -g dotnet-gcdump
dotnet-gcdump collect -n Harbor.App.Cli -o harbor.dump
# Open in PerfView or dotnet-heapstat
```

## Architecture decision records

See `specs/` for the full design rationale. Key decisions:

1. **Two-process architecture** (v2 spec) — Core (NativeAOT) + TUI (JIT) communicate via Unix domain sockets.
2. **JSONL-first storage** — no native deps, append-only, git-friendly. SQLite is optional.
3. **Generic OpenAI-compatible adapter** — covers 90% of providers without code.
4. **Native providers for special cases** — Anthropic (cache_control), OpenAI (Responses API), Ollama (NDJSON).
5. **Event bus decoupling** — TUI subscribes to events, can be replaced/swapped.
6. **Result<T> for error handling** — no exceptions for expected failures.
7. **FrozenDictionary for registries** — O(1) lookups after `Freeze()` is called.
8. **No unsafe code** — verified by `MA0046` analyzer set to error.

## Code review checklist

> **See also:** [docs/DEVELOPMENT.md §Principles checklist](./docs/DEVELOPMENT.md#principles-checklist) — full version with OOP/SOLID/GoF/FP/ROP/perf/low-level/concurrency/AOT sections.
> **See also:** [docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md) — known violations; don't introduce more of the same kind.

- [ ] Builds without warnings (treat warnings as errors).
- [ ] All async methods accept `CancellationToken`.
- [ ] No `.Result` or `.Wait()`.
- [ ] No `_ = SomeAsync()` fire-and-forget (use `await` + `try/catch`, or `.ContinueWith(OnlyOnFaulted)`).
- [ ] Public APIs have XML doc comments (`<summary>`, `<param>`, `<returns>`, `<remarks>`).
- [ ] New types follow SOLID principles (no `switch` on type for new OCP violations — use Strategy).
- [ ] Tests added for new functionality.
- [ ] No secrets in code or config.
- [ ] Uses `Result<T>` for failure cases, not exceptions (Railway Oriented Programming). **Never** call `.Value` on `Result` without checking `IsSuccess`.
- [ ] No `yield` inside `try`/`catch`.
- [ ] No `unsafe` code.
- [ ] Uses `FrozenDictionary` / `IReadOnlyCollection` where appropriate.
- [ ] Uses `ArgumentList.Add()` for process args.
- [ ] Uses `Utf8JsonReader` / `JsonSerializerContext` source-gen on hot paths (no `JsonSerializer.Serialize<T>` reflection).
- [ ] New view models use `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`).
- [ ] New TUI views never reach into `Harbor.Core` — only subscribe to `AgentEvent`.
- [ ] Hot paths use manual `for` loops or ZLinq, not `System.Linq`.
- [ ] Hot paths avoid `string.Split`, `string.Format`, `new StringBuilder()` — use `Span<T>` + `StringBuilderPool`.
- [ ] Singleton services with mutable instance state — thread-safe (`lock` / `Interlocked` / per-call local state).
- [ ] `ILlmClient` and `ITool` impls — explicitly thread-safe for concurrent calls.
- [ ] NativeAOT: 0 IL2026 warnings in `dotnet build -c Release`.
- [ ] **Layering:** every new `<ProjectReference>` is allowed per [ARCHITECTURE_LAYERS.md §2](./docs/ARCHITECTURE_LAYERS.md). Run `dotnet test tests/Harbor.Architecture.Tests/` — it must stay green.
- [ ] **Layering:** no concrete impl type (`AnthropicLlmClient`, `JsonlSessionStore`, `AgentLoop`, `DefaultAgent`, `InMemoryEventBus`, `SharpTsScriptEngine`, `JintScriptEngine`, `RoslynPluginCompiler`, …) is `new`'d outside `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` and `src/Harbor.Hosting/Modules/`. `Program.cs` resolves services by interface from DI.
- [ ] **Layering:** new interfaces go in `Harbor.Abstractions` (or `Harbor.Tui.Abstractions` for UI-only contracts), never in `Harbor.Core`.
- [ ] **Layering:** new value objects / records go in `Harbor.Abstractions/Models`, never in `Harbor.Core`.

## When in doubt

- Read the spec — `specs/14-architecture-revised.md` for current architecture.
- Check existing patterns — `Harbor.Tools.Builtin/Read/ReadTool.cs` is a good reference.
- Look at tests — `tests/Harbor.Abstractions.Tests/` for assertion patterns.
- Check `.editorconfig` for analyzer suppressions.
- Open an issue — don't break the architecture for a quick fix.
