# Harbor.AgentRuntime

## What's inside

The agent loop assembly: `AgentLoop`, `TurnRunner`, `ToolDispatcher`,
`DefaultAgent`, the run-level pipeline behaviors (`Agents/Pipeline/*`) and the
retry mechanics (`Resilience/*`) — a verbatim extraction from
`Harbor.Application` (#1623, plan A part 1/4 of the Application decomp split).
Namespaces are unchanged (`Harbor.Application.Agents`,
`Harbor.Application.Agents.Pipeline`, `Harbor.Application.Resilience`), so
existing usings keep compiling — same backward-compat convention as the
earlier verbatim extractions (#1464, #1475, #1480 and #1613's Hooks move).

`Resilience/RetryLlmClientDecorator.cs` is the one file here that is NOT from
the extraction: #1502 added it next to the concrete `RetryPolicy` it news up,
because `Harbor.Application` — where the decorator was written first — binds
no moved type (the extraction's edge is one-way, AgentRuntime → Application).
Its namespace is still `Harbor.Application.Resilience`, like its siblings.

## Public API

Consumers bind the same types they did before the extraction — the move is
rename-only (file contents + namespaces verbatim, only the assembly changed):

```csharp
public sealed class AgentLoop : IAgentLoop;        // Agents/ — the turn loop
internal sealed class TurnRunner;                  // Agents/ — one LLM turn: stream, coalesce, flush
public sealed class ToolDispatcher : IToolDispatcher;  // Agents/ — permission guard → execute → hook gates
public sealed class DefaultAgent : IAgent;         // Agents/ — the agent record the loop runs
public sealed class SubAgentRunner;                // Agents/ — task tool sub-agent fan-out
public sealed class RunBudgetTracker;              // Agents/ — per-run time/token/step budget
public sealed class AgentPipeline;                 // Agents/Pipeline/ — ordered IAgentBehavior chain
public interface IPipelineBehavior { ... }         // Agents/Pipeline/ — run-level cross-cuttings
// behaviors: LoggingBehavior, PermissionCheckBehavior, MaxStepsBehavior,
//             CompactionBehavior, SteeringDrainBehavior
public sealed class StreamingCoalescer : IDisposable;  // internal — text/thinking/tool-call delta coalescing
public sealed record UpdateFlushPolicy;          // #1527 — how many deltas may share one published update
// Resilience: RetryPolicy : IRetryPolicy, DefaultToolRetryDecider : IToolRetryDecider, RetryPolicyExtensions
public sealed class RetryLlmClientDecorator : ILlmClient;  // Resilience/ — #1502: preflight transient retry on the provider stream
```

Also internal but pinned by tests: `TurnRunner`, `MessageSnapshot`,
`MessageFlush`, `ToolTableBuilder`, `ListenerRegistry`, `BackgroundDrain`,
`BackgroundTaskRegistry`, `AgentSteeringQueue`, `DefaultSessionContext`,
`UserHookGates`, `ToolRetryPolicy`, `StreamingCoalescerJsonContext`,
`UpdateCoalescer`.

`UpdateCoalescer` (#1527) owns the per-delta `MessageUpdateEvent` publishes:
it buffers the deltas of a run and publishes them as one merged delta under
the `UpdateFlushPolicy` the loop hands down. The policy is a value, so a host
that needs a different granularity passes its own `UpdateFlushPolicy` to
`AgentLoop`; null keeps `UpdateFlushPolicy.Default` (32 deltas / 4096
characters / 100 ms). The merge never drops a delta: the last delta of a turn
and every terminal event are published, in order, before the event that
follows them.

## Wiring

Registered by the default DI graph in
`Harbor.Hosting/Modules/CoreModule.cs`: `IRetryPolicy` → `RetryPolicy`,
`IToolRetryDecider` → `DefaultToolRetryDecider`, `IToolDispatcher` →
`ToolDispatcher` (with an optional `IHookRunner` from Harbor.Hooks — `GetService`,
so a hooks-free host keeps working), the `IPipelineBehavior` list (registration
order is pipeline order, outermost first: logging wraps permission), then
`IAgentLoop` → `AgentLoop` and `DefaultAgent` (surfed by `TracingAgentProxy` for
`IAgent`). No `HARBOR_*` env var — activation is purely the DI graph of the
host app; the CLI (`apps/Harbor.App.Cli`) and desktop apps reference the
assembly through Harbor.Hosting.

## Usage

Nothing to call directly for most consumers — the host wires the loop and
drives it over events (`AgentStartEvent` → turns → `AgentEndEvent` on the
`IEventBus`). To run an agent programmatically:

```csharp
var agent = services.GetRequiredService<DefaultAgent>();
var runner = services.GetRequiredService<IAgentRunner>(); // forwards to IAgent
await runner.RunAsync(sessionId, prompt, ct);
```

The pipeline is composition-driven: cross-cutting run-level concerns register
as `IPipelineBehavior`s in the container (see CoreModule), not as literals in
`AgentLoop`'s constructor — a new run-level concern is one registration, no
loop edit.

## Dependencies

- **References:** `Harbor.Abstractions`, `Harbor.Diagnostics.Abstractions`
  (telemetry measurements the loop publishes), `Harbor.Extensions`
  (`StringBuilderPool` etc.) and `Harbor.Application` — the loop's vocabulary
  (`Harbor.Application.Hooks` `IHookRunner`, `.Sessions` `CompactionPolicy` /
  `MessageConverter`, `.Resources` `CoreResources`, `.Telemetry`) is read via
  that ProjectReference, and `Harbor.Hooks` compiles visible through it. The
  edge is one-way AgentRuntime → Application: Application binds no moved type.
- **Referenced by:** `Harbor.Hosting`, `apps/Harbor.App.Cli`,
  `apps/Harbor.App.Avalonia` and the test projects that bind the moved types.
  Follow-ups #1646/#1624/#1626 will extract the vocabulary pieces the residue
  still owns.

## Limitations

- Not a seam for plugins: `IPlugin`/`IToolPlugin` stay the extension axes;
  the loop assembly intentionally exposes no plugin surface.
- Internal details (`StreamingCoalescer`, `TurnRunner`, budget tracking) are
  implementation-only — accessible to `Harbor.Benchmarks` via
  `InternalsVisibleTo`, not public API.
- `Avalonia`/`Blazor` TUIs must not reference this assembly directly
  (Application layer is off-limits to Presentation — the matrix in
  `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs` pins it); they talk
  to the loop through the event bus.
- AOT: source-generated JSON only (`StreamingCoalescerJsonContext`,
  ZLinq drop-in generator) — no reflection emit, plain
  `System.Text.Json` reflection paths are excluded by the shared
  `Directory.Build.props` analyzers.
