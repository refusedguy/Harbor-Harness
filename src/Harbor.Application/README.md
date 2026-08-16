# Harbor.Application

Harbor's application layer — pure use cases that orchestrate the agent
harness. Holds the business logic that turns a user prompt into a series of
LLM turns, tool invocations, compaction events, and permission checks.

## What's in here

| File                               | Responsibility                                                                                                          |
|------------------------------------|-------------------------------------------------------------------------------------------------------------------------|
| `Agents/AgentLoop.cs`              | The Chain-of-Responsibility turn loop: prompt → LLM stream → tool execution → compaction → repeat.                      |
| `Agents/DefaultAgent.cs`           | Stateful `IAgent` implementation that wraps `AgentLoop` with steering, follow-up, and event-bus plumbing.               |
| `Sessions/CompactionService.cs`    | Anchored-summary compaction: when a session approaches the context window, summarize the head and keep a recent tail.   |
| `Sessions/SystemPromptBuilder.cs`  | Assembles the system prompt: identity + tool policy + constraints + env + agent + tools + MCP + skills + context files. |
| `Sessions/MessageConverter.cs`     | Adapter from domain `AgentMessage`s to LLM-specific `LlmMessage`s.                                                      |
| `Permissions/PermissionService.cs` | Specification-pattern permission evaluator: looks up the agent's `PermissionRuleset` and applies allow/deny/ask.        |
| `Onboarding/OnboardingWizard.cs`   | First-run interactive wizard: pick provider → enter API key → pick model → pick agent → save config.                    |
| `Configuration/HarborConfig.cs`    | Application config model + `JsonConfigStore`, `AuthStore`, `ProviderPresets`.                                           |

## Why this project exists

Before the split, `Harbor.Core` was a god-project that mixed pure use cases
(AgentLoop, CompactionService) with concrete registry implementations
(ToolRegistry, ProviderRegistry, InMemoryEventBus). This made it impossible
to substitute a different registry set without recompiling the use cases,
and it created a circular-feeling dependency between the agent harness and
the infrastructure that backs it.

`Harbor.Application` is the pure-orchestration half of the split. Its only
dependency is `Harbor.Abstractions` — it never references Infrastructure
(`Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tools.Builtin`), never
references Presentation (`Harbor.Tui.*`), and never references sibling
Application projects (`Harbor.Plugins.Runtime`, `Harbor.Scripting.*`).

## Dependency rules

* **May reference:** `Harbor.Abstractions` only.
* **Must not reference:** `Harbor.Registries`, `Harbor.Core`,
  `Harbor.Plugins.*`, `Harbor.Scripting.*`, `Harbor.Providers.*`,
  `Harbor.Storage.*`, `Harbor.Tools.Builtin`, `Harbor.Tui.*`, any app.
* **Rationale:** use cases must be testable in isolation with stub/fake
  registries and providers. Pulling in concrete impls would couple the
  orchestration logic to specific infrastructure choices and break the
  test-seam property.

The architecture tests in `tests/Harbor.Architecture.Tests/` enforce these
invariants via `NetArchTest.Rules` and plain `System.Reflection` probes
over `Assembly.GetReferencedAssemblies()`.

## Namespaces

All types kept their original `Harbor.Core.*` namespaces for backward
compatibility — `Harbor.Core.Sessions`, `Harbor.Core.Agents`,
`Harbor.Core.Permissions`, `Harbor.Core.Onboarding`,
`Harbor.Core.Configuration`. The C# compiler resolves namespace contents
across referenced assemblies, so existing `using` directives resolve
unchanged when consumers switch from `Harbor.Core` to `Harbor.Application`.

## Performance intent

* `AgentLoop` coalesces streaming deltas in a pooled `StringBuilder` from
  `CommunityToolkit.HighPerformance` to keep per-delta allocations O(n)
  instead of O(n²).
* `CompactionService` uses index-based cut-points (no `List<T>` slices)
  and pooled buffers for the summarization prompt.
* `SystemPromptBuilder` rents a pooled `StringBuilder` per call.
* ZLinq drop-in source generator (`ZLinq.DropInGenerator`) emits
  zero-allocation LINQ extension methods; `System.Linq` is removed from
  the implicit-usings list so every LINQ call site resolves to ZLinq's
  pipeline without rewriting call sites.

## Related

* [`Harbor.Registries`](../Harbor.Registries) — concrete registry impls
  (AgentRegistry, ToolRegistry, ProviderRegistry, InMemoryEventBus,
  InMemoryMcpRegistry).
* [`Harbor.Core`](../Harbor.Core) — deprecated thin facade that combines
  both for backward compatibility.
* [`docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) —
  the canonical layering matrix and design rationale.
