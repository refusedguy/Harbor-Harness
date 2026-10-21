# Scopes: one implementation of a service per scope

> **Status:** shipped for the LLM provider (issue #1736). The mechanism is
> generic; the session store and the tool registry are not scoped yet.

Harbor resolves every service from one process-wide registry: one
`IProviderRegistry`, one `IToolRegistry`, one `ISessionStore`. That is enough
for a process that runs one agent. It is not enough for a process that runs
several agents with different needs: an agent that must work against a
read-only sandbox, a CI run that must talk to a fake provider, a dump tool that
must not touch the user's sessions.

A **scope** is the axis that lets one of those callers have its own
implementation of a service while everybody else keeps the default one. The
scope is a LABEL, not an object graph: it names a realm, and the realm decides
which implementation a lookup finds.

## What is declared, and where

Two tables, both explicit, both empty by default:

| Table | Question it answers | Who fills it |
|---|---|---|
| agent → scope | which realm does this agent run in? | the composition root, a plugin, a test |
| (scope, provider id) → client | which implementation does this realm see? | the composition root, a plugin, a test |

The types live here:

| Type | Assembly | Role |
|---|---|---|
| `ServiceScope` | `Harbor.Abstractions.Contracts` | the scope label; `ServiceScope.Default` is the realm the process-wide registries live in |
| `IScopedProviderResolver` | `Harbor.Abstractions` | the seam the agent loop reads: which scope an agent runs in, and which client that scope sees |
| `ScopedServiceRegistry<TKey, TService>` | `Harbor.Registries` | the generic (scope, key) → implementation table |
| `ScopedProviderResolver` | `Harbor.Registries` | the provider specialisation of the two tables above |

## The resolution order

The order is fixed, and it is the whole contract:

1. the binding declared for **the exact scope** and the provider id;
2. the binding declared for **`ServiceScope.Default`** and the same provider id;
3. the **process-wide** client the global registry holds.

Step 2 is what makes the feature additive: a scope that declares nothing
inherits the default scope's implementation, and a resolver whose tables are
empty resolves nothing at all — every lookup lands on step 3, which is exactly
what the process did before scopes existed. The product registers the resolver
with empty tables, so shipping the mechanism changes no behaviour.

The model catalog follows the same rule: a scope that declares its own client
is served THAT client's model list, and a scope that inherits is served the
process-wide cached catalog. A run that read one provider's catalog and called
another provider's client would fail on a model that is perfectly available.

## Declaring a scope

```csharp
// The composition root (or a plugin, or a test) declares the two tables.
var resolver = new ScopedProviderResolver(providerRegistry);

// The agent 'plan' runs in the scope 'readonly'.
resolver.BindScope(AgentName.Create("plan"), ServiceScope.Create("readonly"));

// In that scope, the provider 'anthropic' is the sandbox client.
resolver.BindProvider(ServiceScope.Create("readonly"), ProviderId.Create("anthropic"), sandboxClient);
```

The agent loop asks the resolver which scope the running agent is declared in,
and resolves the client through it. An agent no scope is declared for runs in
`ServiceScope.Default` and sees the process-wide client.

## Substituting in a test, without touching the product

A test that wants its own client binds one and hands the resolver to the
agent loop. Nothing in `src/` changes, and no global registry is polluted with
a fake provider id:

```csharp
var resolver = new ScopedProviderResolver(providers);
resolver.BindProvider(ServiceScope.Create("ci"), ProviderId.Create("test"), fakeClient);
resolver.BindScope(AgentName.Create("code"), ServiceScope.Create("ci"));

var loop = new AgentLoop(
    providers, tools, agents, promptBuilder, compaction, tokenTracker,
    retryPolicy, bus, permissions, converter, logger,
    scopedProviders: resolver);
```

`AgentLoop` and `TurnRunner` take the resolver as an OPTIONAL argument. A
directly constructed loop without one keeps the process-wide registry, which is
why every existing caller compiles unchanged.

## What this is not

* **Not an extension axis.** A plugin does not get a door to declare a scope
  in this slice. The `IScopedProviderResolver` seam is a host-level seam; a
  plugin door for it would have to be declared in the single axis registry
  (`src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs`) with both halves
  wired, which is a separate decision.
* **Not a merge of rules.** `PermissionRuleset` stays the policy: a scope
  chooses an IMPLEMENTATION, it does not widen or narrow what an agent is
  allowed to do.
* **Not an ambient context.** The scope travels with the run, as an argument
  the loop reads; there is no static "current scope" to leak between threads.
* **Not a second tool registry.** `IToolRegistry` is still process-wide. The
  generic `ScopedServiceRegistry<TKey, TService>` is what a scoped tool
  registry would be built on when it is needed.

## Where the tests live

* `tests/Harbor.Registries.Tests/ScopedServiceRegistryTests.cs` — the
  resolution order of the generic table.
* `tests/Harbor.Registries.Tests/ScopedProviderResolverTests.cs` — the two
  tables and the fallback to the process-wide client.
* `tests/Harbor.Core.Tests/ScopedProviderIsolationTests.cs` — the agent loop:
  a scoped agent gets the scoped client, an unscoped one keeps the
  process-wide client, and a loop without a resolver is unchanged.

## Related

* [docs/ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) — the layering the
  types above respect: the vocabulary in the Domain, the implementations in
  the Application layer, the wiring in the composition root.
* [docs/PLUGIN_SYSTEM.md](./PLUGIN_SYSTEM.md) — the extension axes, and why a
  scope is not one of them yet.
* [docs/PATTERNS.md](./PATTERNS.md) — the registry and strategy patterns the
  scoped tables reuse.
