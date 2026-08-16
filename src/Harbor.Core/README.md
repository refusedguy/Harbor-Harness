# Harbor.Core

The **Application layer** of Harbor — orchestrates use cases by combining Domain contracts (`Harbor.Abstractions`) with Infrastructure implementations (providers, storage, tools, plugins). Contains the agent loop, event bus, registries, compaction engine, and system prompt builder.

> This is where Harbor's business logic lives. Domain describes *what*; `Harbor.Core` describes *how*.

## Layer

Application — sits above `Harbor.Abstractions` (Domain), below Infrastructure (`Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tools.Builtin`, `Harbor.Plugins.*`) and Presentation (`Harbor.Tui.*`). Infrastructure and Presentation depend on `Harbor.Core` for the agent loop, event bus, and registries.

## What's in it

| Subfolder        | Contents                                                                     |
|------------------|------------------------------------------------------------------------------|
| `Agents/`        | `AgentLoop` (the core orchestrator), `AgentRunner`, `AgentContext`           |
| `Configuration/` | `HarborOptions`, `ProviderOptions`, `StorageOptions` (Strongly-typed config) |
| `Events/`        | `IEventBus`, `InMemoryEventBus` (pub/sub)                                    |
| `Onboarding/`    | First-run setup, config bootstrap                                            |
| `Permissions/`   | `PermissionService` (default impl of `IPermissionService`)                   |
| `Providers/`     | `ProviderRegistry` (FrozenDictionary), `LlmClientFactory`                    |
| `Sessions/`      | `SessionManager`, `CompactionEngine` (anchored-summary fold)                 |
| `Tools/`         | `ToolRegistry` (FrozenDictionary), `ToolDispatcher`                          |

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.DependencyInjection` + `Logging` + `Configuration` + `Hosting.Abstractions` + `Options`
- `CommunityToolkit.HighPerformance`
- `NonBlocking` (lock-free `ConcurrentDictionary` alternative for hot registries)
- `DotNext` + `DotNext.Threading` (async locks, value-type delegates)
- `KZDev.PerfUtils` (FastHashSet / FastDictionary for hot paths)
- `ZLinq` + `ZLinq.DropInGenerator`

## Public API (highlights)

### AgentLoop — the orchestrator

```csharp
public sealed class AgentLoop
{
    public AgentLoop(
        ProviderRegistry providers,
        ToolRegistry tools,
        IEventBus eventBus,
        ISessionStore sessions,
        IPermissionService permissions,
        ILogger<AgentLoop> logger);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        SessionId sessionId,
        string userInput,
        CancellationToken ct);
}
```

Each `RunAsync` call:

1. Loads the session from `ISessionStore`.
2. Resolves the active provider + model.
3. Builds the system prompt (tools, agents, context).
4. Streams the LLM response as `AgentEvent`s.
5. If the LLM emits tool calls, dispatches each to `ToolRegistry` (via `PermissionService`).
6. Feeds tool results back to the LLM, loops until no more tool calls.
7. Persists the session (with compaction if `CompactionSummary` threshold is hit).

### IEventBus — pub/sub

```csharp
public interface IEventBus
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : AgentEvent;
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : AgentEvent;
}
```

The agent loop publishes `AgentEvent`s. TUI renderers, loggers, plugins, and the desktop GUIs subscribe.

### Registries — FrozenDictionary

`ProviderRegistry`, `ToolRegistry`, `AgentRegistry` use `FrozenDictionary` for O(1) lookup after a warm-up phase. Mutations (adding tools at runtime via plugins) go through a `NonBlocking` dictionary and a swap-and-freeze.

### CompactionEngine

```csharp
public sealed class CompactionEngine
{
    public Task<Result<CompactionSummary>> CompactAsync(
        IReadOnlyList<Message> messages,
        CancellationToken ct);
}
```

When a session exceeds `MaxMessages`, the engine folds old turns into an anchored Markdown summary (sections: User Intent, Work Done, Files Modified, Open Questions, Next Steps). The summary is the new anchor; recent turns are preserved verbatim.

## Usage

```csharp
// In your composition root (e.g. apps/Harbor.App.Cli/Hosting/HostBuilder.cs):
services.AddSingleton<IEventBus, InMemoryEventBus>();
services.AddSingleton<ProviderRegistry>();
services.AddSingleton<ToolRegistry>();
services.AddSingleton<AgentRegistry>();
services.AddSingleton<ISessionStore, JsonlSessionStore>();  // or InMemory / Sqlite
services.AddSingleton<IPermissionService, PermissionService>();
services.AddSingleton<AgentLoop>();
```

Then resolve `AgentLoop` and call `RunAsync`:

```csharp
var loop = host.Services.GetRequiredService<AgentLoop>();
await foreach (var evt in loop.RunAsync(sessionId, "Refactor Program.cs", ct))
{
    if (evt is AgentEvent.StreamDelta delta) Console.Write(delta.Text);
}
```

## Performance notes

- `ZLinq` drop-in generator replaces `System.Linq` for zero-allocation pipelines (the implicit `System.Linq` using is removed in the .csproj).
- `FrozenDictionary` for read-only registries after warm-up.
- `NonBlocking` dictionary for hot mutable registries (no lock contention).
- `ArrayPool<T>` for transient buffers in the agent loop.
- `IReadOnlyCollection<T>` on all public registry APIs — callers can't mutate the underlying storage.

## See also

- [../../docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/PATTERNS.md](../../docs/PATTERNS.md) — AgentLoop, EventBus, registries
- [../../docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) — performance numbers
- [../../docs/CODE_PRINCIPLES_AUDIT.md](../../docs/CODE_PRINCIPLES_AUDIT.md)
- [../Harbor.Abstractions/README.md](../Harbor.Abstractions/README.md) — Domain layer
