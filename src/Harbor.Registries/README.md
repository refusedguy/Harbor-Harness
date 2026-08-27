# Harbor.Registries

Harbor's registry implementations — thread-safe, lock-free collections that
hold the agent's, tools', providers', and event-bus' runtime state. Each
registry implements an interface from `Harbor.Abstractions` so the use cases
in `Harbor.Application` can depend on the abstraction without pulling in
the concrete impl.

## What's in here

| File                            | Interface           | Responsibility                                                                                                                                                          |
|---------------------------------|---------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Agents/AgentRegistry.cs`       | `IAgentRegistry`    | Thread-safe agent-definition registry. Backed by `NonBlocking.ConcurrentDictionary` for lock-free scaling. Also exposes `AgentRegistryBuilder` for fluent registration. |
| `Tools/ToolRegistry.cs`         | `IToolRegistry`     | Thread-safe tool registry with a `FrozenDictionary` fast-lookup snapshot taken after startup. Also exposes `ToolRegistryBuilder`.                                       |
| `Tools/CompositeToolRegistry.cs`| `IToolRegistry`     | Fan-in over several tool sources (builtins + plugins) without re-freezing.                                                                                              |
| `Tools/InMemoryMcpRegistry.cs`  | `IMcpRegistry`      | In-memory MCP server registry. Tracks registrations but cannot actually invoke — production hosts swap in a real stdio JSON-RPC client.                                 |
| `Providers/ProviderRegistry.cs` | `IProviderRegistry` | Thread-safe provider registry with lazy `ILlmClient` instantiation and a frozen lookup table. Also exposes `ProviderRegistryBuilder`.                                   |
| `Events/InMemoryEventBus.cs`    | `IEventBus`         | In-memory pub/sub event bus. Bounded scrollback buffer (DropOldest `Channel<T>`), lock-free snapshot reads, pooled dead-subscriber collection.                          |
| `Events/SamplingMiddleware.cs`, `Events/TypeFilterMiddleware.cs` | middleware | Composable bus middleware (type filters, sampling) wrapping an inner bus.                          |

## Why this project exists

Before the split, `Harbor.Core` mixed pure use cases with concrete registry
implementations. The registries were tightly coupled to the use cases at
the assembly level, which made it impossible to:

* Substitute a different registry set (e.g. a persistent tool registry, a
  remote provider registry) without recompiling the use cases.
* Test use cases in isolation without spinning up the concrete registries.
* Reason about which pieces of state belonged to "configuration" vs.
  "use case" vs. "infrastructure".

`Harbor.Registries` is the infrastructure-half of the split. It holds
nothing but the concrete impls of the four core registries + the in-memory
event bus.

## Dependency rules

* **May reference:** `Harbor.Abstractions` only.
* **Must not reference:** `Harbor.Application`, `Harbor.Core`,
  `Harbor.Plugins.*`, `Harbor.Scripting.*`, `Harbor.Providers.*`,
  `Harbor.Storage.*`, `Harbor.Tools.Builtin`, `Harbor.Tui.*`, any app.
* **Rationale:** registries are infrastructure; they must be substitutable
  without affecting use cases. Pulling in `Harbor.Application` would
  re-couple infrastructure to business logic and recreate the god-project
  problem the split was meant to solve.

The architecture tests in `tests/Harbor.Architecture.Tests/` enforce these
invariants via `NetArchTest.Rules` and plain `System.Reflection` probes
over `Assembly.GetReferencedAssemblies()`.

## Namespaces

Registries use two namespace families:

* `Harbor.Abstractions.{Agents, Tools, Providers, Events}` — kept from the
  pre-split era so that interfaces and their original implementations shared
  a namespace (`AgentRegistry`, `ToolRegistry`, `ProviderRegistry`,
  `InMemoryEventBus`).
* `Harbor.Registries.{Events, Tools}` — newer members live here:
  `CompositeToolRegistry`, `InMemoryMcpRegistry`, `SamplingMiddleware`,
  `TypeFilterMiddleware`.

## Performance intent

* `NonBlocking.ConcurrentDictionary` replaces
  `System.Collections.Concurrent.ConcurrentDictionary` for lock-free
  scaling under write contention.
* `FrozenDictionary` snapshots taken at startup (`Freeze()`) give O(1)
  lookup with no dictionary overhead on the hot path.
* `InMemoryEventBus` uses `ImmutableArray<Subscription>` for O(1)
  lock-free snapshot reads with zero allocation; dead-subscriber
  collection uses a pooled array.
* ZLinq drop-in source generator (`ZLinq.DropInGenerator`) emits
  zero-allocation LINQ extension methods; `System.Linq` is removed from
  the implicit-usings list so every LINQ call site resolves to ZLinq's
  pipeline.

## Related

* [`Harbor.Application`](../Harbor.Application) — use cases that consume
  these registries via their abstractions.
* [`Harbor.Core`](../Harbor.Core) — deprecated thin facade that combines
  both for backward compatibility.
* [`docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) —
  the canonical layering matrix and design rationale.
