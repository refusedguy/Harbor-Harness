# Harbor.SessionContext

## What's inside

The session-context cohort: compaction (`CompactionService`, `CompactionPolicy`,
`CompactionView`), system-prompt assembly (`SystemPromptBuilder`,
`CachingSystemPromptBuilder`), the domain↔LLM message bridge
(`MessageConverter`, `ToolResultContextTrim`), the tool-call repair that keeps
a `tool_use` paired with a `tool_result` (`UnresolvedToolCallRepair`), the
per-session token ledger
(`TokenTracker`), the workspace probes (`WorkspaceContextSource`,
`WorkspaceInspector`), the session fork (`SessionForkService`) and the isolated
run records (`IsolatedWorkspace`) — a verbatim extraction from
`Harbor.Application` (#1624, plan A part 2/4 of the Application decomp split).
Namespaces are unchanged (`Harbor.Application.Sessions`,
`Harbor.Application.Skills`), so existing usings keep compiling — same
backward-compat convention as the earlier verbatim extractions
(#1464, #1475, #1480, #1613's Hooks move and #1623's AgentRuntime move).

## Public API

Consumers bind the same types they did before the extraction — the move is
rename-only (file contents + namespaces verbatim, only the assembly changed):

```csharp
public sealed class CompactionService : ICompactionService;  // Sessions/ — anchored-summary compaction
public static class CompactionPolicy;                         // Sessions/ — the pure truncation/view statics
public sealed class CompactionView : ICompactionView;         // Sessions/ — the default view over the statics (#1612)
public sealed class SystemPromptBuilder : ISystemPromptBuilder;// Sessions/ — assembles the per-turn system prompt
public sealed class CachingSystemPromptBuilder : ISystemPromptBuilder; // Sessions/ — memoizing decorator
public sealed class MessageConverter;                         // Sessions/ — AgentMessage <-> LlmMessage
public sealed class TokenTracker : ITokenTracker;             // Sessions/ — the per-session token ledger
public static class WorkspaceContextSource;                   // Sessions/ — the static forwarder over the DI singleton
public sealed class WorkspaceInspector;                       // Sessions/ — workspace / git / process probes
public sealed class SessionForkService;                        // Sessions/ — branch a session into a child
public sealed record IsolatedWorkspace;                       // Sessions/ — the materialized run worktree
public static class ToolResultContextTrim;                    // Sessions/ — caps a tool result's context cost
public static class UnresolvedToolCallRepair;                // Sessions/ — closes a tool_use that never got its tool_result (#1751)
```

Also moved beyond the issue's list, because the cohort bound them in code and
keeping them behind would have flipped the assembly edge into a cycle:
`CompactionView` (the `ICompactionView` default, a forwarder over the
`CompactionPolicy` statics) and `SkillProvider` (the default `ISkillProvider`,
which reads the workspace context through `WorkspaceContextSource`).

Added by #1735, in the same folder and namespace:

```csharp
public sealed class SkillRegistry : ISkillRegistry;   // Skills/ — merges the providers, applies the policy
public sealed class SkillPolicyMatrix;                  // Skills/ — the invocation matrix (data, not code)
public sealed record SkillPolicyRule;                   // Skills/ — one row of that matrix
```

## The skill registry (#1735)

`ISkillProvider` is a SOURCE of skills, not a skill: the filesystem today, a
plugin's bundled catalog or a remote service tomorrow. `SkillRegistry` is the
single answer for all of them:

- **Merge.** Providers are ordered by their DECLARED `ISkillProvider.Priority`
  (higher first) and, at equal priority, by the provider name (ordinal). The
  first provider to offer a name keeps it; a later one is shadowed and logged.
  The order never depends on registration order, so adding a second source
  cannot change which skill wins on the existing ones. The project-over-global
  rule stays inside the workspace provider, which owns the two roots.
- **Policy.** Every skill carries an invocation policy — `ModelInvocable` and
  `UserInvocable` — declared where the skill is declared: the front-matter of
  the skill file (`model-invocable:` / `user-invocable:`), read by the
  workspace provider. A skill that declares neither is invocable by both, so
  the flags change nothing for the skills that do not ask for it.
- **Matrix.** `SkillPolicyMatrix` narrows by request context — source, mode,
  category — as DATA (a list of rows), never as code. It narrows and never
  widens: a skill the provider kept from a caller stays kept. A request no row
  matches is not narrowed at all, so "no policy for this context" and "a policy
  for this context" are two different, predictable answers. The product ships
  the empty matrix; the per-skill declaration is what narrows a skill today.
- **One answer for both surfaces.** The model-facing catalog (the prompt's
  `<available_skills>` block) and the model-facing call (the `skill` tool) go
  through the same registry, so a skill the policy keeps from the model is
  neither listed to it nor loadable by it.

## Wiring

Registered by the default DI graph in
`Harbor.Hosting/Modules/CoreModule.cs`: `ITokenTracker` → `TokenTracker`,
`ISystemPromptBuilder` → `SystemPromptBuilder`, `IWorkspaceContextSource` →
`WorkspaceContextSource.Instance`, `MessageConverter` → `MessageConverter`;
`ICompactionService` → `CompactionService` in
`Harbor.Hosting/Modules/IntelligenceModule.cs`. The skill provider and the
skill registry are built EAGERLY by
`Harbor.Hosting/Modules/RegistryFacade.cs` (`ISkillProvider` → `SkillProvider`,
`ISkillRegistry` → `SkillRegistry` over it) — the skill tool is constructed
while the tool registry is being built and needs the registry, and both are
plain objects that need no container. One instance of each is published, so
the registry the tool holds and the registry the agent loop resolves are the
same object. No `HARBOR_*` env var — activation is purely the DI graph of the
host app; the CLI (`apps/Harbor.App.Cli`) and desktop apps reference the
assembly through Harbor.Hosting.

## Usage

Nothing to call directly for most consumers — the host wires the prompt
builder, the tracker and the compactor, and the loop drives them over events.
To build a prompt or trim a transcript programmatically:

```csharp
var prompt = await promptBuilder.BuildAsync(context, ct);
var llm = messageConverter.ToLlmMessages(messages);
```

## Dependencies

- **References:** `Harbor.Abstractions` (the contract facade),
  `Harbor.Extensions` (`StringBuilderPool` etc.) and `Harbor.Application` —
  the vocabulary this cohort reads (`Harbor.Application.Resilience`
  `IRetryPolicy` / `RetryOptions` for the summary retry, and
  `Harbor.Application.Sessions` `IWorkspaceContextSource` /
  `WorkspaceContextService` for the workspace cache) comes through that
  ProjectReference. The edge is one-way SessionContext → Application:
  Application binds no moved type.
- **Referenced by:** `Harbor.AgentRuntime` (the loop reads `CompactionView`,
  `CachingSystemPromptBuilder`, `MessageConverter`, `WorkspaceContextSource`),
  `Harbor.ChangeTracking` (the run records), `Harbor.Hosting`, the CLI and
  desktop apps, and the test projects that bind the moved types. Follow-ups
  #1625/#1626 will extract the vocabulary pieces the residue still owns.

## Limitations

- Not a seam for plugins: `IPlugin`/`IToolPlugin` stay the extension axes;
  the session-context assembly intentionally exposes no plugin surface.
- `Avalonia`/`Blazor` TUIs must not reference this assembly directly
  (Application layer is off-limits to Presentation — the matrix in
  `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs` pins it); the
  desktop fork goes through the `ISessionForker` port.
- AOT: source-generated JSON only — no reflection emit, plain
  `System.Text.Json` reflection paths are excluded by the shared
  `Directory.Build.props` analyzers.
