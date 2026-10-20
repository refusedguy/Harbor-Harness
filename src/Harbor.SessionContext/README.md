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

## Wiring

Registered by the default DI graph in
`Harbor.Hosting/Modules/CoreModule.cs`: `ITokenTracker` → `TokenTracker`,
`ISystemPromptBuilder` → `SystemPromptBuilder`, `IWorkspaceContextSource` →
`WorkspaceContextSource.Instance`, `ISkillProvider` → `SkillProvider`,
`MessageConverter` → `MessageConverter`; `ICompactionService` →
`CompactionService` in `Harbor.Hosting/Modules/IntelligenceModule.cs`. No
`HARBOR_*` env var — activation is purely the DI graph of the host app; the
CLI (`apps/Harbor.App.Cli`) and desktop apps reference the assembly through
Harbor.Hosting.

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
