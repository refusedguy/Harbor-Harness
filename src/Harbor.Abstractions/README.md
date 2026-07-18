# Harbor.Abstractions

The **Domain layer** of Harbor — zero-side-effect contracts: interfaces, immutable records, enums, and value objects. Every other Harbor layer depends on `Harbor.Abstractions`; `Harbor.Abstractions` depends on nothing Harbor-internal.

> Design principle: **the Domain layer knows nothing about how it is used.** No I/O, no DI registration, no HTTP, no JSON parsing. Just pure contracts.

## Layer

Domain — the innermost layer of the [Clean Architecture](../../docs/ARCHITECTURE_LAYERS.md) onion. Every other layer points inward to this one.

## What's in it

| Subfolder       | Contents                                                              |
|-----------------|-----------------------------------------------------------------------|
| `Agents/`       | `AgentDefinition`, `IAgent`, `AgentId`, `AgentType` (code/plan/explore) |
| `Events/`       | `AgentEvent` (discriminated union), `StreamDeltaEvent`, `ToolCallEvent`, `CompactionEvent`, `ErrorEvent` |
| `Extensions/`   | `ResultExtensions`, `AsyncEnumerableExtensions`                       |
| `Models/`       | `Message`, `MessageRole`, `ToolCall`, `ToolResult`, `TokenUsage`, `SessionMetadata` |
| `Permissions/`  | `Permission`, `PermissionRule`, `IPermissionService`, `PermissionDecision` (Allow/Ask/Deny) |
| `Plugins/`      | `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin`, `PluginManifest` |
| `Providers/`    | `ILlmClient`, `LlmRequest`, `LlmResponse`, `ProviderDefinition`, `ProviderCapabilities` |
| `Sessions/`     | `ISessionStore`, `SessionId`, `SessionState`, `CompactionSummary`     |
| `Tools/`        | `ITool`, `ToolDefinition`, `ToolParameter`, `ToolInvocation`, `BuiltinTools` |
| `Tui/`          | `IUiSink` (cross-cutting; full TUI contracts live in `Harbor.Tui.Abstractions`) |

## What's NOT in it

- **No HTTP, no HttpClient, no JSON serialization.** Provider implementations live in `Harbor.Providers.*` (Infrastructure).
- **No DI registration.** `IServiceCollection` extensions live in `Harbor.Core` or composition roots.
- **No Roslyn, no reflection-based activation.** Plugin instantiation lives in `Harbor.Plugins.Instantiation`.
- **No file I/O.** Session storage lives in `Harbor.Storage.*`.
- **No UI code.** TUI renderers live in `Harbor.Tui.*`.
- **No business logic.** The agent loop lives in `Harbor.Core`.

If you find yourself adding any of the above to `Harbor.Abstractions`, **stop** — it belongs in a higher layer.

## Dependencies

Zero Harbor-internal dependencies. External packages only:

- `CSharpFunctionalExtensions` — `Result<T>`, `ValueObject` (Railway Oriented Programming)
- `CommunityToolkit.HighPerformance` — `Span<T>` helpers, `HashData`
- `MemoryPack` — high-performance binary serialization for events
- `ZLinq` + `ZLinq.DropInGenerator` — zero-allocation LINQ (replaces `System.Linq` via `<Using Remove="System.Linq"/>`)
- `Microsoft.Extensions.DependencyInjection.Abstractions` — `IServiceCollection` (interfaces only, no impl)
- `Microsoft.Extensions.Logging.Abstractions` — `ILogger` (interfaces only)
- `Microsoft.Extensions.Configuration.Abstractions` — `IConfiguration` (interfaces only)

> `AllowUnsafeBlocks` is `false`. The Domain layer is 100% safe code.

## Public API (highlights)

### Result<T> — Railway Oriented Programming

```csharp
public Result<ToolResult> Invoke(ToolInvocation inv, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(inv.Parameters.Path))
        return Result.Failure<ToolResult>("Path is required.");
    return Result.Success(new ToolResult(inv.Id, content, isError: false));
}
```

All fallible operations return `Result<T>` — no exceptions for expected failures. Exceptions are reserved for truly unexpected bugs (NullReference, OutOfMemory, etc.).

### AgentEvent — discriminated union

```csharp
public abstract record AgentEvent
{
    public sealed record StreamDelta(string Text) : AgentEvent;
    public sealed record ToolCallStarted(ToolInvocation Invocation) : AgentEvent;
    public sealed record ToolCallCompleted(ToolInvocation Invocation, ToolResult Result) : AgentEvent;
    public sealed record CompactionStarted(int OriginalMessageCount) : AgentEvent;
    public sealed record CompactionCompleted(CompactionSummary Summary) : AgentEvent;
    public sealed record Error(string Message, Exception? Inner) : AgentEvent;
    public sealed record Completed(TokenUsage Usage) : AgentEvent;
}
```

Subscribe via `IEventBus` (defined in `Harbor.Core` — the Domain defines the event shapes, the Application layer defines the bus).

### IPlugin — extension point

```csharp
public interface IPlugin
{
    string Name { get; }
    string Version { get; }
    void Initialize(IPluginLoadHost host);
    Task ShutdownAsync(CancellationToken ct);
}

public interface IToolPlugin : IPlugin
{
    IEnumerable<ToolDefinition> Tools { get; }
    Result<ToolResult> Invoke(ToolInvocation inv, CancellationToken ct);
}
```

## Usage

`Harbor.Abstractions` is referenced by **every** other Harbor project. You don't typically consume it directly — you consume the types it defines via higher-layer packages.

If you're writing a Harbor plugin, you'll reference `Harbor.Abstractions` (and possibly `Harbor.Plugins.Abstractions`) and implement `IPlugin` / `IToolPlugin` / `IProviderPlugin` / `IAgentPlugin` / `ITuiPlugin`.

## Design principles

1. **Purity** — no side effects, no I/O, no global state.
2. **Immutability** — every type is `sealed` or a `record`. Mutable state lives in `Harbor.Core` (the Application layer).
3. **Railway Oriented Programming** — `Result<T>` everywhere; exceptions reserved for bugs.
4. **Zero unsafe code** — `AllowUnsafeBlocks=false`.
5. **Performance** — `ZLinq` drop-in generator replaces `System.Linq` for zero-allocation pipelines. `FrozenDictionary` for registries (in `Harbor.Core`).
6. **Full XML docs** — every public API ships with `<summary>`/`<param>`/`<returns>`/`<remarks>`.
7. **No business logic** — Domain describes *what*; higher layers describe *how*.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md) — full layer diagram
- [../../docs/PATTERNS.md](../../docs/PATTERNS.md) — Result<T>, discriminated unions, etc.
- [../../docs/ANTIPATTERNS.md](../../docs/ANTIPATTERNS.md) — what NOT to do in the Domain layer
- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md) — `IPlugin` extension model
- [../../docs/CODE_PRINCIPLES_AUDIT.md](../../docs/CODE_PRINCIPLES_AUDIT.md)
