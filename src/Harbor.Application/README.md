# Harbor.Application

Harbor's application layer — pure use cases that orchestrate the agent
harness. Holds the business logic that turns a user prompt into a series of
LLM turns, tool invocations, compaction events, and permission checks.

## What's in here

| File                               | Responsibility                                                                                                          |
|------------------------------------|-------------------------------------------------------------------------------------------------------------------------|
| `Agents/AgentLoop.cs`              | The Chain-of-Responsibility turn loop: prompt → LLM stream → tool execution → compaction → repeat. Turn orchestration only — delegates streaming buffering and tool dispatch to its collaborators below. |
| `Agents/ToolDispatcher.cs`         | `IToolDispatcher` impl — executes a turn's tool calls: sequential fan-out when any tool declares `ExecutionMode.Sequential`, parallel otherwise; permission-gated, per-call timeout from the agent definition. |
| `Agents/StreamingCoalescer.cs`     | Streaming-buffer coalescing: accumulates text/thinking/tool-call deltas between raw LLM events and published message updates.           |
| `Agents/DefaultAgent.cs`           | Stateful `IAgent` implementation that wraps `AgentLoop` with steering, follow-up, and event-bus plumbing.               |
| `Sessions/CompactionService.cs`    | Anchored-summary compaction: when a session approaches the context window, summarize the head and keep a recent tail.   |
| `Sessions/SystemPromptBuilder.cs`  | Assembles the system prompt: identity + tool policy + constraints + env + agent + tools + MCP + skills + context files. |
| `Sessions/MessageConverter.cs`     | Adapter from domain `AgentMessage`s to LLM-specific `LlmMessage`s.                                                      |
| `Permissions/PermissionService.cs` | Specification-pattern permission evaluator: looks up the agent's `PermissionRuleset` and applies allow/deny/ask.        |
| `Sessions/CachingSystemPromptBuilder.cs` | Memoizing decorator over `ISystemPromptBuilder` — re-assembles the prompt only when inputs change.                    |
| `Sessions/TokenTracker.cs`         | `ITokenTracker` impl — per-turn usage aggregation (`TokenStats`) plus a running token estimate that keeps `ShouldCompact` O(1).   |
| `Sessions/WorkspaceContextSource.cs` | Injects workspace context files into the system prompt.                                                                |
| `Providers/ProviderHealthCheck.cs` | `IProviderHealthCheck` — probes provider endpoints before onboarding/picking a model.                                   |
| `Resilience/RetryPolicy.cs`        | `IRetryPolicy` impl — retries only transient failures (HTTP 408/429/5xx, network, provider timeouts) with capped exponential backoff (`BaseDelay · 2^(attempt−1)` + optional jitter); fatal errors propagate immediately. |
| `Resilience/RetryPolicyExtensions.cs` | Central retry policy helpers for transient provider errors — `ExecuteSafeAsync` adapts exception-based retries onto the `Result<T>` railway. |
| `Telemetry/`                       | Usage telemetry plumbing consumed by the app hosts.                                                                      |
| `Onboarding/OnboardingWizard.cs`   | First-run interactive wizard: pick provider → enter API key → pick live model → pick agent → save config.                |
| `Configuration/HarborConfig.cs`    | Application config model + `JsonConfigStore`, `AuthStore`, `ProviderPresets` (single preset catalog backing `/model`).    |

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

* **May reference:** `Harbor.Abstractions`, `Harbor.Extensions`,
  `Harbor.Diagnostics.Abstractions`.
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

Types declare namespaces matching their assembly of residence (ROP-D Z1):
`Harbor.Application.Agents`, `Harbor.Application.Sessions`,
`Harbor.Application.Permissions`, `Harbor.Application.Onboarding`,
`Harbor.Application.Configuration`, `Harbor.Application.Resilience`,
`Harbor.Application.Providers`, `Harbor.Application.Telemetry`. Consumers that
switch from the old `Harbor.Core` facade must update their `using` directives
once when they migrate.

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
