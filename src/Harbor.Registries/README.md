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
| `Scopes/ScopedServiceRegistry.cs` | (internal) | The (scope, key) → implementation table behind scope isolation (#1736). One implementation per scope, with the default scope as the fallback every other scope inherits. |
| `Scopes/ScopedProviderResolver.cs` | `IScopedProviderResolver` | The scope-aware provider lookup (#1736): which scope an agent runs in, and which LLM client that scope sees. Two declared tables; empty tables mean the process-wide client. |
| `Events/InMemoryEventBus.cs`    | `IEventBus`         | In-memory pub/sub event bus. Fixed-capacity scrollback ring, lock-free snapshot reads, pooled dead-subscriber collection, and a zero-subscriber fast path gated on an enumerated mandatory/optional sink verdict (see `docs/EVENT_BUS_SINKS.md`). |
| `Events/EventBusScrollbackStore.cs` | (internal)      | The bus's bounded scrollback, extracted from `InMemoryEventBus` (#1512): the fixed-capacity ring, its lock, the head/count pair and the retention-arming flag (#518 — the first `GetScrollback` call arms the ring, and an unarmed ring costs nothing on the publish path). The bus keeps its public surface and delegates here. |
| `Events/SamplingMiddleware.cs`, `Events/TypeFilterMiddleware.cs` | middleware | Composable bus middleware. `TypeFilterMiddleware` declares `SinkKind = Mandatory` (a filter is a contract on what projections may see); `SamplingMiddleware` declares `Optional` (a throttle whose own job is to drop events). |

## Scope isolation (#1736)

Every registry above is process-wide: one provider id maps to one client for
the whole process. `Scopes/` adds the missing axis — a **scope**, a named realm
in which a service may have its own implementation while everybody else keeps
the default one. Two tables, both declared and both empty by default: which
scope an agent runs in, and which implementation a scope sees. The resolution
order is the exact scope, then the default scope, then the process-wide
registry — so a resolver with empty tables behaves exactly like the global
registry did before scopes existed, and shipping the mechanism changes no
behaviour.

The vocabulary (`ServiceScope`) is in `Harbor.Abstractions.Contracts` and the
seam (`IScopedProviderResolver`) is in `Harbor.Abstractions`, beside the other
registry contracts; the tables themselves are here, beside the registries they
extend. See [docs/SCOPES.md](../../docs/SCOPES.md) for the whole mechanism.

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
* **Must not reference:** `Harbor.Application`,
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
  `InMemoryEventBus`). Note the namespace family does NOT match the assembly
  name: these types ship in `Harbor.Registries.dll`.
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
* [`docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) —
  the canonical layering matrix and design rationale.
