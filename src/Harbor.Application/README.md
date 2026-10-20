# Harbor.Application

Harbor's application layer — pure use cases that orchestrate the agent
harness. Holds the business logic that turns a user prompt into a series of
LLM turns, tool invocations, compaction events, and permission checks.

## What's in here

| File                               | Responsibility                                                                                                          |
|------------------------------------|-------------------------------------------------------------------------------------------------------------------------|
| `Sessions/WorkspaceContextService.cs` | The injectable `IWorkspaceContextSource` — discovers the workspace context files (`AGENTS.md`, `CLAUDE.md`) and the skills the prompt renders. |
| `Sessions/WorkspacePaths.cs`       | `IWorkspacePaths` — the run-directory layout (`$HARBOR_HOME/runs/<runId>`), extracted from the workspace-materializer hub (#1610). |
| `Providers/ProviderHealthCheck.cs` | `IProviderHealthCheck` — probes provider endpoints before onboarding/picking a model.                                   |
| `Telemetry/`                       | Usage telemetry plumbing consumed by the app hosts.                                                                      |
| `Skills/`                          | `SkillFreshnessSeeder` + `SkillUpdater` — the skill-lifecycle side of the workspace context.                             |
| `FrontMatter.cs`                   | The single front-matter field reader shared by the skill seeder and the workspace-context service (#1580).              |
| `Observability/`                   | `TurnChangeOracle` — the harness-side oracle of what a turn changed in the workspace: a projection over the event bus that fingerprints a mutating tool's target before and after the call and reports what the FILESYSTEM says (#1745, [ADR-020](../../docs/adr/ADR-020-turn-change-oracle.md)). |

Five cohorts that used to live here have their own assemblies, with their
namespaces kept verbatim so no consumer had to rewrite a `using`:

| Cohort | Assembly | Moved by |
|---|---|---|
| `Agents/*`, `Agents/Pipeline/*`, `Resilience/` (implementations) | [`Harbor.AgentRuntime`](../Harbor.AgentRuntime) | #1623 |
| `Sessions/*` (compaction, prompt assembly, token tracking, workspace probes, session fork), `Skills/SkillProvider.cs` | [`Harbor.SessionContext`](../Harbor.SessionContext) | #1624 |
| `Configuration/*` (the config model, the config store, the auth store, the provider catalogue) | [`Harbor.PolicyConfig`](../Harbor.PolicyConfig) | #1625 |
| `Permissions/*` (the permission subdomain) | [`Harbor.Permissions`](../Harbor.Permissions) | #1615 |
| `Configuration/Onboarding*` (the wizard trio) | [`Harbor.Onboarding`](../Harbor.Onboarding) | #1614 |


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
* **Must not reference:** `Harbor.Registries`,
  `Harbor.Plugins.*`, `Harbor.Scripting.*`, `Harbor.Providers.*`,
  `Harbor.Storage.*`, `Harbor.Tools.Builtin`, `Harbor.Tui.*`, any app —
  and, since #1625, the sibling Application assemblies
  (`Harbor.Permissions`, `Harbor.PolicyConfig`, `Harbor.Onboarding`): the
  configuration family and the permission subdomain are reached by the
  composition root, not from here.
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
`Harbor.Application.Resilience`,
`Harbor.Application.Providers`, `Harbor.Application.Telemetry`. (The onboarding
trio declared the Configuration namespace too; #1614 moved it to the
`Harbor.Onboarding` leaf keeping the namespace verbatim.) Consumers that
no longer needs a `using` rewrite: the old `Harbor.Core` facade that once
re-exported them was deleted in #451. The `Harbor.Application.Permissions`
namespace is the #1615 extraction — the same namespace now lives in the
sibling `Harbor.Permissions` assembly. The `Harbor.Application.Configuration`
namespace is the #1625 extraction — it now lives in the sibling
`Harbor.PolicyConfig` assembly (and `PermissionConfigStoreAdapter`, which
implements the permissions subdomain's seam over the whole-config
`IConfigStore`, moved with it). The
`Harbor.Application.Agents` / `.Resilience` namespaces are the #1623
extraction (`Harbor.AgentRuntime`), and `Harbor.Application.Sessions` /
`Harbor.Application.Skills` are the #1624 extraction
(`Harbor.SessionContext`) — in both cases the namespace is unchanged and the
types simply live in the sibling assembly now, so a consumer needs the new
`ProjectReference` and no `using` edit.

## Performance intent

* `Harbor.AgentRuntime`'s `AgentLoop` coalesces streaming deltas in a pooled
  `StringBuilder` rented from `Harbor.Extensions.StringBuilderPool` (a
  `ConcurrentBag` pool in this solution) to keep per-delta allocations O(n)
  instead of O(n²). This bullet used to credit
  `CommunityToolkit.HighPerformance` for the pooling; it never provided it, and
  the phantom dependency has been removed from this `.csproj`.
* `Harbor.SessionContext`'s `CompactionService` uses index-based cut-points (no
  `List<T>` slices) and pooled buffers for the summarization prompt.
* `Harbor.SessionContext`'s `SystemPromptBuilder` rents a pooled
  `StringBuilder` per call.
* ZLinq drop-in source generator (`ZLinq.DropInGenerator`) emits
  zero-allocation LINQ extension methods; `System.Linq` is removed from
  the implicit-usings list so every LINQ call site resolves to ZLinq's
  pipeline without rewriting call sites.

## Related

* [`Harbor.Registries`](../Harbor.Registries) — concrete registry impls
  (AgentRegistry, ToolRegistry, ProviderRegistry, InMemoryEventBus,
  InMemoryMcpRegistry).
* [`Harbor.AgentRuntime`](../Harbor.AgentRuntime) — the agent loop
  (`AgentLoop`, `TurnRunner`, `ToolDispatcher`, pipeline behaviors).
* [`Harbor.SessionContext`](../Harbor.SessionContext) — the session context
  (compaction, prompt assembly, token tracking, workspace probes, session fork).
* [`Harbor.PolicyConfig`](../Harbor.PolicyConfig) — the configuration family
  (the config model, the config store, the auth store, the provider catalogue).
* [`docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) —
  the canonical layering matrix and design rationale.
