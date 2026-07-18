# Harbor.Abstractions

The **thin facade** layer of Harbor — interface contracts only. After the round-6 architecture split, this project no longer contains domain models or extension helpers; it forwards to `Harbor.Domain` (pure domain models) and `Harbor.Extensions` (infrastructure helpers) transitively.

> Design principle: **the facade knows nothing about how it is implemented.** Interfaces, plugin contexts, agent definitions, and the slash-command router contract live here; the implementations live in higher layers (`Harbor.Application`, `Harbor.Registries`, `Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tui.*`).

## Layer

**Domain (facade).** The innermost layer of the [Clean Architecture](../../docs/ARCHITECTURE_LAYERS.md) onion. Every other layer points inward to this one. This project, in turn, points only at `Harbor.Domain` (models the interfaces reference) and `Harbor.Extensions` (pooling helpers re-exported transitively for downstream consumers).

## What's in it (post-split)

| Subfolder | Contents |
| --- | --- |
| `Agents/` | `IAgentRunner`, `IAgent`, `AgentState`, `IAgentLoop` (interfaces + the per-run state record), `AgentDefinition` (the static blueprint record), `IAgentRegistry`, `IAgentRegistryBuilder` |
| `Events/` | `IEventBus`, `IEventSubscriber` (the event-bus interface — the `AgentEvent` discriminated union itself lives in `Harbor.Domain/Events/`) |
| `Models/` | _(empty post-split)_ — `Session`, `Messages`, `MemoryPackFormatters`, `Identifiers/*` moved to `Harbor.Domain/Models/` |
| `Permissions/` | _(empty post-split)_ — `PermissionRuleset`, `PermissionRule`, `PermissionAction`, `PermissionRequest`, `PermissionResponse`, `IPermissionService` moved to `Harbor.Domain/Permissions/` |
| `Plugins/` | `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `PluginContext`, `IPluginHost` |
| `Providers/` | `ILlmClient`, `IProviderRegistry`, `IProviderRegistryBuilder`, plus the LLM-message contract types (`LlmRequest`, `LlmMessage` hierarchy, `LlmContentBlock` hierarchy, `ToolDefinition`) |
| `Sessions/` | `ISessionStore`, `ISessionContext`, `ISystemPromptBuilder`, `SystemPromptContext`, `ContextFile`, `SkillDescriptor`, `ICompactionService`, `CompactionResult`, `ITokenEstimator`, `HeuristicTokenEstimator` |
| `Tools/` | `ITool`, `ToolContext`, `ToolProgressUpdate`, `ToolDescriptor`, `ExecutionMode`, `IToolRegistry`, `IToolRegistryBuilder`, `IMcpRegistry` |
| `Tui/` | `IInputHandler`, `KeyPress`, `KeyPressEventArgs`, `ISlashCommand`, `ICommandContext`, `ISlashCommandRouter` (the full TUI contracts may move again to `Harbor.Ui.Framework` per Task A2 — left here for now) |
| `Extensions/` | _(empty post-split)_ — `ArrayPoolExtensions`, `CollectionExtensions`, `MemoryPackExtensions` moved to `Harbor.Extensions/` |
| `ZLinqDropInAssemblyAttribute.cs` | Assembly-level source-generator config (kept here because the interfaces' LINQ call sites resolve via ZLinq drop-in) |

## What's NOT in it (post-split)

- **No domain models.** `Session`, `AgentMessage`, `ModelInfo`, `Usage`, `Pricing`, `ToolResult`, `ToolResultEntry`, `FileAttachment`, `AgentEvent` and all 13 derived event types, `LlmEvent` and all 12 derived streaming-event types, `PermissionRuleset` / `PermissionRule` / `PermissionAction`, `SessionId` / `MessageId` / `ToolCallId` / `ProviderId` / `ModelRef` / `ToolName` / `AgentName` — all moved to `Harbor.Domain`.
- **No infrastructure helpers.** `ArrayPoolExtensions.RentScoped<T>`, `StringBuilderPool`, `RentedArray<T>`, `CollectionExtensions.ToFrozenSet<T>` / `ToFrozenDictionary<...>`, `MemoryPackExtensions.ToMemoryPackBytes<T>` / `FromMemoryPackBytes<T>` — all moved to `Harbor.Extensions`.
- **No HTTP, no HttpClient, no JSON serialization beyond `JsonElement`/`JsonDocument`.** Provider implementations live in `Harbor.Providers.*` (Infrastructure).
- **No DI registration.** `IServiceCollection` extensions live in `Harbor.Core` or composition roots.
- **No Roslyn, no reflection-based activation.** Plugin instantiation lives in `Harbor.Plugins.Instantiation`.
- **No file I/O.** Session storage lives in `Harbor.Storage.*`.
- **No UI code.** TUI renderers live in `Harbor.Tui.*`.
- **No business logic.** The agent loop lives in `Harbor.Application` (split out of `Harbor.Core` in round 5).

If you find yourself adding any of the above to `Harbor.Abstractions`, **stop** — domain models belong in `Harbor.Domain`, helpers belong in `Harbor.Extensions`, business logic belongs in `Harbor.Application` / `Harbor.Registries`.

## Dependencies

### Project references (the facade wiring)

| Project | Why |
| --- | --- |
| `Harbor.Domain` | Interfaces reference domain types: `ITool.ExecuteAsync` returns `Task<ToolResult>` (Domain), `ISessionStore.AppendMessageAsync` takes `AgentMessage` (Domain), `IEventBus.PublishAsync` takes `AgentEvent` (Domain), `IAgentRegistry.GetAgent` returns `AgentDefinition` (Domain). |
| `Harbor.Extensions` | The facade re-exports `ArrayPoolExtensions`, `StringBuilderPool`, `CollectionExtensions`, `MemoryPackExtensions` transitively so downstream consumers (e.g. `Harbor.Application`, `Harbor.Tools.*`) that already reference `Harbor.Abstractions` don't need to add a new `<ProjectReference Include="Harbor.Extensions">`. |

### Package references (the interface-only contracts)

- `CSharpFunctionalExtensions` — `Result<T>`, `ValueObject` (Railway Oriented Programming)
- `CommunityToolkit.HighPerformance` — `Span<T>` helpers, `HashData`
- `MemoryPack` — `[MemoryPackable]` types referenced by interface signatures (`ToolResult`, `AgentMessage`)
- `ZLinq` + `ZLinq.DropInGenerator` — zero-allocation LINQ (replaces `System.Linq` via `<Using Remove="System.Linq"/>`)
- `Microsoft.Extensions.DependencyInjection.Abstractions` — `IServiceCollection` (`PluginContext.Services`)
- `Microsoft.Extensions.Logging.Abstractions` — `ILogger` (`PluginContext.CreateLogger<T>`)
- `Microsoft.Extensions.Configuration.Abstractions` — `IConfiguration` (`PluginContext.Configuration`)

> `AllowUnsafeBlocks` is `false`. The facade is 100% safe code.

## Why split? (rationale)

Before the round-6 split, `Harbor.Abstractions` was a god-project: domain models + interfaces + extension helpers + permissions + events all in one assembly. This had three problems:

1. **Cross-cutting rebuilds.** Touching `Models/Session.cs` rebuilt every interface and every consumer of every interface, even though the interface signatures didn't change. With the split, only `Harbor.Domain` rebuilds; `Harbor.Abstractions` (and its 30+ consumers) stay cached.
2. **Layering ambiguity.** A new contributor couldn't tell from the namespace whether a type was a pure model or an interface. The split enforces the boundary at the assembly level — architecture tests in `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs` fail the build if anyone re-adds a model to `Harbor.Abstractions` or an interface to `Harbor.Domain`.
3. **Forced transitive deps.** Consumers that only needed pure models (e.g. `Harbor.Providers.Anthropic` only needs `ModelInfo`, `LlmRequest`, `LlmEvent`) were forced to pull in the full `Microsoft.Extensions.DependencyInjection.Abstractions` / `Logging.Abstractions` / `Configuration.Abstractions` stack that the interfaces pull in. After the split, these consumers can reference `Harbor.Domain` directly (a future optimization; the facade still works for now).

The split is **namespace-preserving**: every file moved to `Harbor.Domain` or `Harbor.Extensions` keeps its original `namespace Harbor.Abstractions.{Models,Events,Permissions,Models.Identifiers,Extensions};` declaration. Consumer code requires zero `using` changes.

## Public API (highlights — interface contracts)

### IEventBus — Observer pattern

```csharp
public interface IEventBus
{
    Task PublishAsync(AgentEvent @event, CancellationToken ct = default);
    IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler);
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent;
    IReadOnlyList<AgentEvent> GetScrollback(int maxEvents);
}
```

`AgentEvent` lives in `Harbor.Domain`; the bus interface lives here.

### ITool — Strategy pattern

```csharp
public interface ITool
{
    ToolName Name { get; }                              // ToolName lives in Harbor.Domain
    string DisplayName { get; }
    JsonDocument ParameterSchema { get; }
    ExecutionMode ExecutionMode { get; }
    Task<ToolResult> ExecuteAsync(                     // ToolResult lives in Harbor.Domain
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default);
    Result ValidateArguments(JsonElement args) => Result.Success();
}
```

### IPlugin — Plugin pattern

```csharp
public interface IPlugin
{
    string Name { get; }
    Version Version { get; }
    void Initialize(PluginContext context);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public interface IToolPlugin : IPlugin
{
    void RegisterTools(IToolRegistryBuilder builder);
}
```

## Usage

`Harbor.Abstractions` is referenced by **every** other Harbor project. You don't typically consume it directly — you consume the interface contracts it defines via higher-layer packages.

If you're writing a Harbor plugin, you'll reference `Harbor.Abstractions` (and possibly `Harbor.Plugins.Abstractions`) and implement `IPlugin` / `IToolPlugin` / `IProviderPlugin` / `IAgentPlugin`. The domain types your plugin touches (`ToolResult`, `AgentMessage`, `Session`) come transitively via `Harbor.Abstractions → Harbor.Domain`. The pool helpers your plugin uses (`StringBuilderPool`, `RentScoped`) come transitively via `Harbor.Abstractions → Harbor.Extensions`.

## Design principles (preserved from the pre-split era)

1. **Purity** — no side effects, no I/O, no global state.
2. **Immutability** — every type is `sealed` or a `record`. Mutable state lives in `Harbor.Application` / `Harbor.Registries`.
3. **Railway Oriented Programming** — `Result<T>` everywhere; exceptions reserved for bugs.
4. **Zero unsafe code** — `AllowUnsafeBlocks=false`.
5. **Performance** — `ZLinq` drop-in generator replaces `System.Linq` for zero-allocation pipelines.
6. **Full XML docs** — every public API ships with `<summary>`/`<param>`/`<returns>`/`<remarks>`.
7. **No business logic** — the facade describes *what*; higher layers describe *how*.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md) — full layer diagram
- [../../docs/PATTERNS.md](../../docs/PATTERNS.md) — Result<T>, discriminated unions, etc.
- [../../docs/ANTIPATTERNS.md](../../docs/ANTIPATTERNS.md) — what NOT to do in the Domain layer
- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md) — `IPlugin` extension model
- [../../docs/CODE_PRINCIPLES_AUDIT.md](../../docs/CODE_PRINCIPLES_AUDIT.md)
- [../Harbor.Domain/README.md](../Harbor.Domain/README.md) — the pure domain layer (post-split)
- [../Harbor.Extensions/README.md](../Harbor.Extensions/README.md) — the infrastructure-helper layer (post-split)
- [../../tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs](../../tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs) — enforces the split invariants
