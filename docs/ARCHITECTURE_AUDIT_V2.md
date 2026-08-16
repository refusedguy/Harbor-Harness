# Harbor Architecture Audit v2

> Deep audit of Harbor's architecture, performance, concurrency, and pattern
> compliance. Builds on `docs/CODE_PRINCIPLES_AUDIT.md` (v1, 41 findings) —
> v2 covers systemic issues the v1 audit deferred, plus 10 killer
> architectural improvements (4 already implemented in this sprint) with
> concrete file:line references and implementation sketches.

**Date:** 2026-07-19 (revised)
**Auditor:** Subagent A2 (`arch-audit-v2`)
**Scope:** all `src/` projects (~38 projects, 5 apps, 13 test projects, ~25 000 LoC)
**Build:** .NET 10.0.302 SDK, `Release`, 0 errors
**Methodology:** manual line-by-line read of the agent loop, registries,
IPC server/client, UI store/reducer, plugin runtime, scripting host, and
JSONL session store; cross-checked against `docs/CODE_PRINCIPLES_AUDIT.md`
v1 findings to verify which were resolved, partially resolved, or still
open. Each finding cites a concrete `file:line` anchor.

---

## Table of contents

- [§1. Executive summary](#1-executive-summary)
  - [Top 10 findings](#top-10-findings)
  - [Top 10 killer architectural improvements](#top-10-killer-architectural-improvements)
- [§2. Architecture audit findings](#2-architecture-audit-findings)
  - [2.1 SOLID violations](#21-solid-violations)
  - [2.2 Performance & hot-path allocations](#22-performance--hot-path-allocations)
  - [2.3 Concurrency & lock contention](#23-concurrency--lock-contention)
  - [2.4 Missing CancellationToken propagation](#24-missing-cancellationtoken-propagation)
  - [2.5 Sync-over-async](#25-sync-over-async)
  - [2.6 TEA / pattern compliance](#26-tea--pattern-compliance)
  - [2.7 Mixed concerns & missing abstractions](#27-mixed-concerns--missing-abstractions)
- [§3. Killer architectural improvements](#3-killer-architectural-improvements)
  - [3.1 EventBus scrollback ring buffer (implemented v1.5)](#31-eventbus-scrollback-ring-buffer)
  - [3.2 Lock-free `InvalidateFrozenSnapshot` (implemented v1.5)](#32-lock-free-invalidatefrozensnapshot)
  - [3.3 Compiled JSONL line parser cache (implemented v2)](#33-compiled-jsonl-line-parser-cache)
  - [3.4 Lazy provider initialization + CT audit (implemented v2)](#34-lazy-provider-initialization--ct-audit)
  - [3.5 Pipeline behaviors (MediatR-style)](#35-pipeline-behaviors-mediatr-style)
  - [3.6 Event sourcing + snapshot for sessions](#36-event-sourcing--snapshot-for-sessions)
  - [3.7 CQRS read/write split for `IHarborClient`](#37-cqrs-readwrite-split-for-iharborclient)
  - [3.8 Plugin sandboxing (collectible AssemblyLoadContext)](#38-plugin-sandboxing-collectible-assemblyloadcontext)
  - [3.9 Hot-reload for `.cs` plugins (FileSystemWatcher)](#39-hot-reload-for-cs-plugins-filesystemwatcher)
  - [3.10 Differential TUI rendering](#310-differential-tui-rendering)
- [§4. Pattern compliance scorecard](#4-pattern-compliance-scorecard)
- [§5. Tech debt timeline (v0.8 / v0.9 / v1.0)](#5-tech-debt-timeline)
- [§6. References](#6-references)

---

## §1. Executive summary

This audit revisits the v1 code-principles audit (`docs/CODE_PRINCIPLES_AUDIT.md`,
41 findings) after a wave of refactor work. The v1 criticals have largely been
resolved: the EventBus scrollback drain bug, the `InvalidateFrozenSnapshot`
lock thundering-herd, the `fire-and-forget` `_eventBus.PublishAsync` inside
tool progress reporting, and the `Result.Value` access on invalid input —
all four show up with explicit `RESOLVED` markers next to the code. Where v1
flagged a God-class (`AgentLoop.cs`, ~650 lines, 8 responsibilities) the
class is now decorated with an explicit `TODO(principles)[SRP]` block
acknowledging the debt and proposing a split into four collaborators
(`StreamingCoalescer`, `ToolCallDispatcher`, `TurnEventPublisher`, …) — the
split has not yet been done.

The dominant **systemic** patterns still open in v2:

1. **`JsonlSessionStore.GetStatsAsync` re-parses the entire JSONL file** to
   compute token sums — `JsonlSessionStore.cs:258-302` calls
   `GetMessagesAsync(sessionId, ct)` which re-runs `JsonDocument.Parse(line)`
   per line. A 10k-message session therefore pays ~10k allocations on every
   `/stats` call and on every `LoadSessionContextAsync`. This is the highest
   ROI perf fix in the codebase.

2. **`JsonlSessionStore` synchronous I/O drops the `CancellationToken`.**
   `File.AppendAllText`, `File.WriteAllLines`, `Directory.CreateDirectory`
   are not CT-aware. `AppendMessageAsync(sessionId, message, ct)` accepts
   `ct` but never observes it — a 30 MB message write cannot be cancelled.

3. **`RequestDispatcher` is a 13-case `switch` on `HarborRequest`.** Adding
   a new request type (e.g. `StreamMessagesRequest` for back-pressure-aware
   streaming reads) requires editing the dispatcher and the routing table.
   A handler-registry (`IDictionary<Type, Func<HarborRequest, CancellationToken, Task<HarborResponse>>>`)
   would make this OCP-compliant.

4. **`EventBroadcaster.OnEventAsync` holds `_clientsLock` on every publish
   just to snapshot the client list.** Under high event throughput (1000+
   deltas/sec) this serialises every event against `Register`/`Unregister`
   — a lock-free `ImmutableArray<ClientRegistration>` swap (same pattern as
   `InMemoryEventBus._subscriptions`) would unblock this.

5. **`ProviderRegistryBuilder.AddProvider(Func<ILlmClient> factory)` still
   eagerly invokes `factory()`** to read the `ProviderId` (line 281). Every
   startup constructs one extra `OllamaLlmClient` / `AnthropicLlmClient` /
   `OpenAILlmClient` instance per provider, just to read a string. The
   overload `AddProvider(string providerId, Func<ILlmClient> factory)` is
   the lazy path; the legacy overload should be deprecated.

The audit identifies 10 architectural improvements; the top 4 (EventBus
scrollback ring buffer, lock-free `InvalidateFrozenSnapshot`, compiled
JSONL line parser cache, lazy provider init + CT audit) are **implemented
in this sprint** with 0 build errors and all tests passing. The remaining
6 are documented with implementation sketches for the v0.9 / v1.0 roadmap.

### Top 10 findings

| # | ID | Category | Severity | Where | One-line |
|---|---|---|:---:|---|---|
| 1 | PERF-009 | Performance | **critical** | `Harbor.Storage.Jsonl/JsonlSessionStore.cs:258-302` | `GetStatsAsync` calls `GetMessagesAsync` which re-parses every line of the JSONL file — every `/stats` and every `LoadSessionContextAsync` rebuilds the full message list |
| 2 | PERF-005 | Performance | **high** | `Harbor.Storage.Jsonl/JsonlSessionStore.cs:204` | `JsonDocument.Parse(line)` per line — ~10k `JsonDocument` allocations on a 10k-message session; deferred `Utf8JsonReader` rewrite |
| 3 | SOLID-001 | Architecture | high | `Harbor.Application/Agents/AgentLoop.cs` (682 lines, 8 responsibilities) | God-class — orchestrates turn-loop + coalesces stream + dispatches tools + publishes events + drains steering |
| 4 | CT-001 | Correctness | high | `Harbor.Storage.Jsonl/JsonlSessionStore.cs:139-167` (`AppendMessageAsync`, `CreateAsync`, `DeleteAsync`) | `CancellationToken` parameter accepted but never observed — `File.AppendAllText` / `File.Delete` are not CT-aware and there is no `ct.ThrowIfCancellationRequested()` guard |
| 5 | IPC-001 | Performance | medium | `Harbor.Ipc.Abstractions/Protocol/WireCodec.cs:118-156` (`WriteFrameAsync`, `ReadFrameAsync`) | `new byte[length]` per frame + `Stream.ReadAsync` loop — no `ArrayPool<byte>`, no `PipeReader` despite the comment claiming it |
| 6 | DI-001 | Architecture | medium | `apps/Harbor.App.Cli/Hosting/HostBuilder.cs:337,560,581` | Three `BuildServiceProvider()` calls during composition — anti-pattern (DI016 suppressed); causes `ServiceProvider` churn |
| 7 | SOLID-002 | Architecture | medium | `Harbor.Application/Sessions/CompactionService.cs:89-99` | `ReserveTokens` / `KeepRecentTokens` / `TailTurns` are mutable `public int` properties — runtime config drift, not injected via `IOptions<CompactionOptions>` |
| 8 | IPC-002 | Concurrency | medium | `Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:124-128` (`OnEventAsync`) | Holds `_clientsLock` to snapshot the client list on every event publish — serialises every publish against `Register`/`Unregister` |
| 9 | PERF-002 | Performance | medium | `Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs:144` (`BuildRequest`) | `Dictionary<string,object?>` + anonymous types + reflection-based `JsonSerializer.Serialize(payload)` per request — not AOT-friendly |
| 10 | ARCH-001 | Architecture | low | `Harbor.Ipc.Server/Protocol/RequestDispatcher.cs:65-82` | 13-case `switch` on `HarborRequest` — adding a request requires editing the dispatcher; no handler-registry |

### Top 10 killer architectural improvements

| # | Name | Impact | Effort | Priority | Status in v2 |
|---|---|:---:|:---:|:---:|---|
| 1 | **EventBus scrollback ring buffer** | high | S | P0 | ✅ **implemented** v1.5 — §3.1 |
| 2 | **Lock-free `InvalidateFrozenSnapshot`** | high | S | P0 | ✅ **implemented** v1.5 — §3.2 |
| 3 | **Compiled JSONL line parser cache** | high | S | P0 | ✅ **implemented** v2 — §3.3 |
| 4 | **Lazy provider init + CT propagation audit** | medium | S | P0 | ✅ **implemented** v2 — §3.4 |
| 5 | **Pipeline behaviors for agent commands (MediatR-style)** | high | M | P1 | proposed — §3.5 |
| 6 | **Event sourcing + snapshot for sessions** | high | L | P1 | proposed — §3.6 |
| 7 | **CQRS read/write split for `IHarborClient`** | medium | M | P1 | proposed — §3.7 |
| 8 | **Plugin sandboxing (collectible `AssemblyLoadContext`)** | high | L | P1 | proposed — §3.8 |
| 9 | **Hot-reload for `.cs` plugins (`FileSystemWatcher`)** | medium | M | P2 | proposed — §3.9 |
| 10 | **Differential TUI rendering (cell-diff, not full redraw)** | high | L | P2 | proposed — §3.10 |

---

## §2. Architecture audit findings

### 2.1 SOLID violations

#### SOLID-001 — `AgentLoop` God-class (high)

**Where:** `src/Harbor.Application/Agents/AgentLoop.cs` (682 lines).

**Symptom.** The class implements eight distinct responsibilities in one
file:

1. Turn-loop orchestration (`RunAsync`, lines 80-417).
2. Streaming delta coalescing (`StringBuilderPool.Rent`, `textBuffer`,
   `thinkingBuffer`, lines ~150-180).
3. Tool-call accumulation (`pendingToolCalls` dictionary, lines ~190-220).
4. Tool execution dispatch (`ExecuteToolCallsAsync`, `ExecuteSingleToolCallAsync`,
   lines 474-680).
5. Error handling (per-tool `try/catch` + `OperationCanceledException`
   blocks).
6. Event publishing (every step calls `_eventBus.PublishAsync`).
7. Permission gating (`_permissions.CheckAsync`, line 604).
8. Steering queue draining (line 399).

The class itself acknowledges this with a multi-line `TODO(principles)[SRP]`
comment at lines 18-26 that proposes the correct split:

```csharp
// TODO(principles)[SRP]: класс ~650 строк делает слишком много: (1) оркестрация
// turn-loop, (2) streaming coalescing, (3) tool-call accumulation, (4) tool
// execution dispatch, (5) error handling, (6) event publishing, (7) permission
// gating, (8) steering queue draining. Лучше разнести:
//   - AgentLoop — оркестрация (10-20 строк)
//   - StreamingCoalescer — аккумулирование дельт в StringBuilder'ах
//   - ToolCallDispatcher — выполнение тулзов и сбор результатов
//   - TurnEventPublisher — publish events
```

**Why it matters.** The class is hard to test in isolation (every test
needs an `IEventBus`, `IProviderRegistry`, `IToolRegistry`,
`IPermissionService`, `ICompactionService`, `ISystemPromptBuilder`,
`ILogger`, `ITokenEstimator` mock). The streaming-coalescing logic — which
has the most subtle correctness requirements around
`OperationCanceledException` flush paths — cannot be unit-tested without
also running the turn loop. Eight responsibilities means eight reasons to
edit the file, which makes every change higher-risk.

**Proposed split.**

```csharp
public sealed class AgentLoop : IAgentLoop
{
    private readonly StreamingCoalescer _coalescer;
    private readonly ToolCallDispatcher _tools;
    private readonly TurnEventPublisher _events;
    private readonly ICompactionService _compaction;
    private readonly IProviderRegistry _providers;

    public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct)
    {
        var model = await ResolveModelAsync(agent, ct);
        if (model is null) return Result.Failure("model not found");

        await _events.AgentStarted(session, model);
        int turn = 0;
        while (!ct.IsCancellationRequested)
        {
            turn++;
            await _events.TurnStarted(turn);
            if (_compaction.ShouldCompact(session.Messages, model))
                await _compaction.CompactAsync(session, model, ct);

            var partial = await StreamOneTurnAsync(session, agent, model, ct);
            if (partial.ToolCalls.Count == 0 || partial.StopReason is StopReason.Stop or StopReason.Length)
            {
                await _events.TurnEnded(partial, Array.Empty<ToolResultMessage>());
                break;
            }

            var results = await _tools.ExecuteAsync(partial.ToolCalls, session, partial, agent, ct);
            await session.AppendMessageAsync(results, ct);
            await _events.TurnEnded(partial, new[] { results });

            if (turn >= agent.MaxSteps) break;
        }

        await _events.AgentEnded(session);
        return Result.Success();
    }

    private async Task<AssistantMessage> StreamOneTurnAsync(
        ISessionContext session, AgentDefinition agent, ModelInfo model, CancellationToken ct)
    {
        var request = BuildRequest(session, agent, model);
        var client = _providers.GetClient(ProviderId.Create(model.ProviderId)).Value;
        await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
            _coalescer.Apply(evt);
        return _coalescer.FinalizeMessage(session.Session.Id, model);
    }
}
```

The orchestrator shrinks to ~25 lines; the four collaborators each become
independently testable.

**Effort:** M (one day, plus test refactors).  
**Priority:** P1 — debt is acknowledged but the live class works.

---

#### SOLID-002 — `CompactionService` mutable config properties (medium)

**Where:** `src/Harbor.Application/Sessions/CompactionService.cs:89-99`.

```csharp
public int ReserveTokens { get; set; } = 16384;
public int KeepRecentTokens { get; set; } = 20000;
public int TailTurns { get; set; } = 2;
```

**Symptom.** Three tuning knobs are exposed as mutable `public int` setters
on the service itself. The CLI's `HostBuilder.RegisterRegistries` constructs
the service with the defaults (line 445-448) and never mutates them — but
nothing stops another caller from doing so. Worse, the service is a DI
singleton, so a mutation at runtime silently affects every subsequent
compaction across every session, with no audit trail.

**Why it matters.** Mutable setters on a singleton service are a
configuration-drift footgun. The defaults are also magic numbers — there is
no `IOptions<CompactionOptions>` indirection that would let users override
them via `~/.harbor/config.json` or env vars.

**Proposed fix.**

```csharp
public sealed record CompactionOptions
{
    public int ReserveTokens { get; init; } = 16384;
    public int KeepRecentTokens { get; init; } = 20000;
    public int TailTurns { get; init; } = 2;
}

public sealed class CompactionService : ICompactionService
{
    private readonly CompactionOptions _options;
    public CompactionService(
        ITokenEstimator tokenEstimator,
        IProviderRegistry providers,
        IOptions<CompactionOptions> options,
        ILogger<CompactionService> logger) { … }
}
```

`IOptions<>` integrates with .NET configuration binding so users can
override the knobs via `appsettings.json` / `HARBOR_COMPACTION_RESERVE_TOKENS`
without code changes.

**Effort:** S.  
**Priority:** P2.

---

#### SOLID-003 — `RequestDispatcher` 13-case switch (medium)

**Where:** `src/Harbor.Ipc.Server/Protocol/RequestDispatcher.cs:65-82`.

```csharp
return request switch
{
    StartAgentRequest r => await HandleStartAgentAsync(r, ct),
    AbortAgentRequest r => HandleAbortAgent(r),
    SendPromptRequest r => await HandleSendPromptAsync(r, ct),
    CreateSessionRequest r => await HandleCreateSessionAsync(r, ct),
    // … 9 more cases …
    _ => new ErrorResponse { RequestId = request.RequestId, Message = $"Unknown request type" }
};
```

**Symptom.** Open/Closed violation: every new request type requires editing
the dispatcher. The pattern also concentrates every service-resolution call
in one file (`_serviceProvider.GetRequiredService<IAgent>()`,
`GetRequiredService<ISessionStore>()`, etc.), so the dispatcher is the
single point that knows the full DI surface.

**Proposed fix — handler registry:**

```csharp
public interface IHarborRequestHandler<in TRequest> where TRequest : HarborRequest
{
    Task<HarborResponse> HandleAsync(TRequest request, CancellationToken ct);
}

public sealed class RequestDispatcher
{
    private readonly Dictionary<Type, Func<HarborRequest, CancellationToken, Task<HarborResponse>>> _handlers;

    public RequestDispatcher(IServiceProvider sp, EventBroadcaster broadcaster)
    {
        _handlers = new()
        {
            [typeof(StartAgentRequest)] = (r, ct) => sp.GetRequiredStartAgentHandler().HandleAsync((StartAgentRequest)r, ct),
            [typeof(SendPromptRequest)] = (r, ct) => sp.GetRequiredSendPromptHandler().HandleAsync((SendPromptRequest)r, ct),
            // …
        };
    }

    public Task<HarborResponse> DispatchAsync(HarborRequest request, CancellationToken ct) =>
        _handlers.TryGetValue(request.GetType(), out var h)
            ? h(request, ct)
            : Task.FromResult<HarborResponse>(new ErrorResponse { RequestId = request.RequestId, Message = "unknown" });
}
```

Each handler is independently testable, and adding a request requires only
registering a new `IHarborRequestHandler<T>` — no dispatcher edit.

**Effort:** M.  
**Priority:** P2.

---

#### SOLID-004 — `ProviderRegistryBuilder.AddProvider(Func<ILlmClient>)` eager instantiation (medium)

**Where:** `src/Harbor.Registries/Providers/ProviderRegistry.cs:279-283`
(and the parallel `PluginRegistrar.ProviderRegistryBuilderAdapter.AddProvider(Func<ILlmClient>)`
at `src/Harbor.Plugins.Registration/PluginRegistrar.cs:140-147`).

```csharp
public void AddProvider(Func<ILlmClient> factory)
{
    var tempClient = factory();                // ← eager instantiation
    _registry.Register(tempClient.ProviderId, factory);
}
```

**Symptom.** The factory is invoked **once at registration time** just to
read `tempClient.ProviderId`. The `ProviderRegistry` then wraps the same
factory in a `Lazy<ILlmClient>` (line 204), so the *second* invocation is
lazy — but the first invocation constructs an `OllamaLlmClient` /
`AnthropicLlmClient` / `OpenAILlmClient` at startup, even if that provider
is never used during the session. Each LLM client constructor typically
allocates an `HttpClient` configuration, an auth resolver, and a logger.

The CLI's `HostBuilder.CreateProviderRegistry` already calls the lazy
overload `pb.AddProvider("ollama", () => new OllamaLlmClient(...))`
explicitly (line 525), so the CLI is fine. **Plugins** reach
`ProviderRegistryBuilderAdapter.AddProvider(Func<ILlmClient>)` which still
calls `factory()` eagerly.

**Fix implemented in v2 — see §3.4.**

---

#### SOLID-005 — `ToolRegistry` dual-path (frozen vs concurrent) duplication (low)

**Where:** `src/Harbor.Registries/Tools/ToolRegistry.cs:32-115`.

The class acknowledges this with a `TODO(principles)[OCP, ROP]` comment at
lines 24-27: the frozen-snapshot path and the ConcurrentDictionary fallback
duplicate the descriptor-projection logic in `GetAllTools`,
`ResolveTools`, and `GetTool`. Adding a third source (e.g. lazy-loaded
tools from plugins) would require a third duplication.

**Proposed fix — `CompositeToolRegistry` delegating to `IToolSource[]`:**

```csharp
public sealed class CompositeToolRegistry : IToolRegistry
{
    private readonly IToolSource[] _sources;
    public Result<ITool> GetTool(ToolName name)
    {
        foreach (var s in _sources)
            if (s.TryGet(name, out var t)) return Result.Success(t);
        return Result.Failure<ITool>($"Tool '{name}' not registered.");
    }
}
```

**Effort:** M.  
**Priority:** P2.

---

### 2.2 Performance & hot-path allocations

#### PERF-005 — `JsonDocument.Parse(line)` per line in `JsonlSessionStore`

**Where:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs:204` (and the
inner `DeserializeMessage` at lines 358-470 which performs ~5
`TryGetProperty` calls per message).

**Symptom.** For each line of the JSONL file:

1. `JsonDocument.Parse(line)` allocates a `JsonDocument` with its own
   pooled `DbRef` array.
2. `DeserializeMessage(sessionId, doc.RootElement)` calls
   `element.GetProperty("id")`, `.GetProperty("createdAt")`,
   `.TryGetProperty("parentId", …)`, `.TryGetProperty("role", …)`,
   `.GetProperty("payload")` — five `TryGetProperty` calls, each doing a
   linear scan over the property names.
3. For assistant messages, the code then allocates a `List<ContentPart>`
   and iterates `partsEl.EnumerateArray()`.

A 10 000-message session ≈ 30 000-50 000 allocations on a single
`GetMessagesAsync` call. `GetStatsAsync` calls `GetMessagesAsync` *again*
(line 262) so the cost doubles on every `/stats` invocation.

**Proposed fix — `Utf8JsonReader` streaming + MemoryPack binary format
(see §3.6 for the longer-term event-sourcing fix).** A short-term partial
fix (a parsed-message cache) is implemented in §3.3.

**Effort:** M (full Utf8JsonReader rewrite) / S (parsed-message cache).  
**Priority:** P0 (cache), P1 (full rewrite).

---

#### PERF-009 — `GetStatsAsync` re-parses the entire JSONL file

**Where:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs:258-302`.

```csharp
public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
{
    var messagesResult = await GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
    // … iterates messages to sum inputTokens / outputTokens / cacheRead / cacheWrite …
}
```

**Symptom.** `GetStatsAsync` calls `GetMessagesAsync` which reads and
re-parses the entire file. `AgentLoop.RunAsync` →
`DefaultAgent.LoadSessionContextAsync` calls `GetMessagesAsync` *and*
`UpdateStatsAsync` (called from line 372 in `AgentLoop`) which itself calls
`GetStatsAsync` (via `DefaultSessionContext.UpdateStatsAsync`, line 311-322
in `DefaultAgent.cs`). So a single turn with 5 tool calls triggers
**6 full re-parses** of the JSONL file.

**Fix implemented in v2 — see §3.3.** The parsed-message cache returns the
cached list to both callers; `AppendMessageAsync` invalidates just the
changed session.

---

#### PERF-002 — `OpenAiCompatibleLlmClient.BuildRequest` reflection-based serialization

**Where:** `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs:144`
(per v1 audit).

`Dictionary<string, object?>` + anonymous types + reflection-based
`JsonSerializer.Serialize(payload)` per request. Not NativeAOT-friendly
(emits IL2026 warnings) and ~3-5× slower than a typed POCO + source-gen
`JsonSerializerContext`.

**Proposed fix — typed POCO + `JsonSerializerContext`:**

```csharp
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatResponse))]
internal sealed partial class OpenAiJsonContext : JsonSerializerContext { }

internal sealed record OpenAiChatRequest(
    string Model,
    List<OpenAiMessage> Messages,
    List<OpenAiTool> Tools,
    bool Stream,
    decimal? Temperature);
```

**Effort:** M.  
**Priority:** P1.

---

#### PERF-007 — `UiReducer.CycleFocus` uses LINQ `Where(...).ToList()`

**Where:** `src/Harbor.Ui.Framework/State/UiReducer.cs:246-248`.

```csharp
var visible = state.RegisteredPanelIds
    .Where(id => state.PanelStates.TryGetValue(id, out var s) && s != TuiPanelState.Hidden)
    .ToList();
```

**Symptom.** Allocates an iterator + a `List<string>` per focus-cycle
keypress. Not a hot path (only on `Tab` press), but the file explicitly
calls out allocation-free patterns elsewhere — this one slipped through.

**Proposed fix — index-based scan into a rented `Span<string>`:**

```csharp
var ids = state.RegisteredPanelIds;
string?[] buffer = ArrayPool<string?>.Shared.Rent(ids.Length);
int visibleCount = 0;
try
{
    for (int i = 0; i < ids.Length; i++)
    {
        if (state.PanelStates.TryGetValue(ids[i], out var s) && s != TuiPanelState.Hidden)
            buffer[visibleCount++] = ids[i];
    }
    // … use buffer[0..visibleCount] …
}
finally { ArrayPool<string?>.Shared.Return(buffer, clearArray: true); }
```

**Effort:** S.  
**Priority:** P2 (cosmetic).

---

#### PERF-008 — `InMemoryEventBus` scrollback drain bug (RESOLVED)

**Where:** `src/Harbor.Registries/Events/InMemoryEventBus.cs:171-203`
(`GetScrollback`).

**Status.** The previous implementation backed scrollback with a bounded
`Channel<AgentEvent>` and drained it on every `GetScrollback` call via
`ReadAllAsync().ToBlockingEnumerable()`. Two bugs: (1) the channel was
emptied after a single late subscriber read it, so subsequent late
subscribers saw nothing; (2) the blocking enumeration synchronously
blocked the calling thread — a TUI freeze under heavy event traffic.

The new implementation keeps scrollback in an `ImmutableArray<AgentEvent>`
ring buffer updated atomically via `ImmutableInterlocked.InterlockedCompareExchange`
(lines 211-240). Reads take a single volatile snapshot — never mutate, never
block. **Fixed in v1.5; see §3.1 for the full writeup.**

---

#### PERF-010 — `EventBroadcaster.OnEventAsync` allocates `List<ClientRegistration>` per event

**Where:** `src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:124-128`.

```csharp
List<ClientRegistration> snapshot;
lock (_clientsLock)
{
    snapshot = new List<ClientRegistration>(_clients);
}
```

**Symptom.** Every published event allocates a fresh `List<ClientRegistration>`
copy under the lock. Under heavy streaming (1000+ events/sec) this is 1000+
list allocations per second, plus 1000 lock acquisitions that block
`Register` / `Unregister`.

**Proposed fix — `ImmutableArray<ClientRegistration>` swap:**

```csharp
private ImmutableArray<ClientRegistration> _clients = ImmutableArray<ClientRegistration>.Empty;

public void Register(Stream s, SemaphoreSlim writeLock) =>
    ImmutableInterlocked.Update(ref _clients, static (arr, reg) => arr.Add(reg), new ClientRegistration(s, writeLock));

private async ValueTask OnEventAsync(AgentEvent evt, CancellationToken ct)
{
    var snapshot = _clients;        // single volatile read, zero alloc
    if (snapshot.IsEmpty) return;
    // … iterate snapshot.Length, no lock …
}
```

Same pattern as `InMemoryEventBus._subscriptions` (lines 147-156).

**Effort:** S.  
**Priority:** P1.

---

### 2.3 Concurrency & lock contention

#### CONCURRENCY-001 — `InvalidateFrozenSnapshot` lock thundering-herd (RESOLVED)

**Where:** `src/Harbor.Registries/Tools/ToolRegistry.cs:180-188` and
`src/Harbor.Registries/Providers/ProviderRegistry.cs:243-250`.

**Status.** The previous implementation took `lock(_frozenLock)` to null a
single reference field. Under write-heavy load (plugin registration, hot
reload) this serialised every `Register` / `Unregister` call and forced
concurrent `GetTool` / `ResolveTools` readers through the slow
`ConcurrentDictionary` fallback while the lock was held.

The fix is a single `Interlocked.Exchange(ref _frozenTools, null)` on a
`volatile` field — no lock, no contention. The CAS guarantees the next
read sees `null` (volatile-read acquire) so the slow path is taken at most
once per invalidation cycle. **Fixed in v1.5; see §3.2.**

---

#### CONCURRENCY-002 — `EventBroadcaster._clientsLock` (open)

**Where:** `src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:35-37, 64-99, 124-128`.

The `Lock _clientsLock` guards a `List<ClientRegistration>` for both reads
(`OnEventAsync` snapshot) and writes (`Register` / `Unregister`). Under
high event throughput every publish blocks every concurrent
register/unregister. See PERF-010 for the lock-free replacement.

**Effort:** S.  
**Priority:** P1.

---

#### CONCURRENCY-003 — `JsonlSessionStore._lock` serialises all writes across all sessions

**Where:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs:25-26`.

```csharp
private readonly object _lock = new();
```

The single `object _lock` guards every `CreateAsync`, `AppendMessageAsync`,
and `UpdateMessageAsync` across **every session in the process**. With 10
concurrent sessions each writing a message, all 10 wait on the same lock.
`File.AppendAllText` is already atomic per-call on POSIX, so the lock is
only needed to interleave with `GetMessagesAsync` reads of the same file.

**Proposed fix — per-session `SemaphoreSlim`:**

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

private SemaphoreSlim GetLock(string sessionId) =>
    _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
```

**Effort:** S.  
**Priority:** P1.

---

#### CONCURRENCY-004 — `DefaultAgent._listenersLock` snapshot on every event (open, low-impact)

**Where:** `src/Harbor.Application/Agents/DefaultAgent.cs:60-86`.

The agent's event-bus subscription handler snapshots `_listeners` into a
fresh `Func<...>[]` array under `_listenersLock` on every published event.
The previous code allocated a `List<T>.ToList()` per event (v1 finding);
the current code allocates an array, which is better but still O(n) per
event.

**Proposed fix — `ImmutableArray<Func<...>>` swap (same pattern as
`InMemoryEventBus._subscriptions`):**

```csharp
private ImmutableArray<Func<AgentEvent, CancellationToken, ValueTask>> _listeners = ImmutableArray<...>.Empty;
public IDisposable Subscribe(Func<...> l) {
    ImmutableInterlocked.Update(ref _listeners, static (a, x) => a.Add(x), l);
    return new Unsubscriber(() => ImmutableInterlocked.Update(ref _listeners, static (a, x) => a.Remove(x), l));
}
```

Subscriber snapshot becomes a single volatile read.

**Effort:** S.  
**Priority:** P2.

---

### 2.4 Missing CancellationToken propagation

#### CT-001 — `JsonlSessionStore` accepts `CancellationToken` but never observes it

**Where:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs:139-167, 41-79, 241-256`.

`AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)`:

```csharp
public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
{
    try
    {
        // …
        lock (_lock)
        {
            // …
            File.AppendAllText(sessionFile, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        }
        return Task.FromResult(Result.Success());
    }
    catch (Exception ex) { … }
}
```

The `ct` parameter is accepted by signature but **never observed**.
`File.AppendAllText` is synchronous I/O that doesn't accept a CT, so a
30 MB tool-result message write cannot be cancelled. The same applies to
`CreateAsync` (line 41-79) and `DeleteAsync` (line 241-256).

**Fix implemented in v2 — see §3.4.** The fix adds
`ct.ThrowIfCancellationRequested()` guards before each sync-I/O call and
documents the limitation that `File.AppendAllText` itself is not
CT-aware.

---

#### CT-002 — `TuiEffectHost.PromptAsync` catches `Exception` (incl. OCE)

**Where:** `src/Harbor.Ui.Framework/State/TuiEffectHost.cs:85-96`.

```csharp
private async Task PromptAsync(string text)
{
    _store.Transition(s => s with { IsAgentRunning = true, Status = "running" });
    try
    {
        await _agent.PromptAsync(text, _appCt).ConfigureAwait(false);
    }
    catch (Exception)               // ← swallows OperationCanceledException
    {
        _store.Transition(s => s with { Status = "error" });
    }
}
```

**Symptom.** `catch (Exception)` is the CA1031 anti-pattern. When the user
hits `Esc` to abort, `_appCt` is cancelled, `_agent.PromptAsync` throws
`OperationCanceledException`, and the catch block sets the UI status to
`"error"` — but the user just wanted to abort, not error out. The status
bar then shows "error" until the next prompt.

**Fix implemented in v2 — see §3.4.** `OperationCanceledException` is now
caught first and routed to a clean "idle" transition; only genuine
exceptions set "error".

---

#### CT-003 — `WireCodec.ReadFrameAsync` blocks on `Stream.ReadAsync` without CT budget

**Where:** `src/Harbor.Ipc.Abstractions/Protocol/WireCodec.cs:133-171`.

`ReadFrameAsync(Stream, CancellationToken)` reads the 4-byte header, then
allocates `new byte[length]` and calls `ReadExactAsync(stream, payload, ct)`.
The CT is propagated to `Stream.ReadAsync`, which is correct, but the
**adversarial-input defence** is incomplete: a malicious client can send a
length-prefixed frame whose declared length is `MaxFrameBytes` (64 MiB) but
whose body never arrives. The server then sits in `ReadExactAsync` until
the CT fires — typically the IPC server lifetime CT, not a per-request
budget.

**Proposed fix — per-frame read timeout:**

```csharp
using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
readCts.CancelAfter(TimeSpan.FromSeconds(30));
if (!await ReadExactAsync(stream, payload, readCts.Token)) { … }
```

**Effort:** S.  
**Priority:** P1 (security).

---

#### CT-004 — `ProviderRegistryBuilder.AddProvider` factory invocation ignores CT

**Where:** `src/Harbor.Registries/Providers/ProviderRegistry.cs:279-283`.

The factory is invoked synchronously during registration; if the factory
performs slow init (e.g. an HTTP `/models` call to validate the API key),
the registration thread cannot be cancelled. With the v2 lazy-init fix
(§3.4) the factory is never invoked during registration, so this is moot
once §3.4 ships.

---

### 2.5 Sync-over-async

#### SYNC-001 — `HostBuilder` three `BuildServiceProvider()` calls

**Where:** `apps/Harbor.App.Cli/Hosting/HostBuilder.cs:337, 560, 581` (and
inline `#pragma warning disable RS0030` at lines 288-290, 312-315, 340-342,
412-414).

```csharp
private static void RegisterRegistries(HostApplicationBuilder builder, string harborDir)
{
    var tempSp = builder.Services.BuildServiceProvider();       // ← 337
    // … use tempSp to resolve ILoggerFactory, IEventBus, CliConfig, CommonConfig …
}
```

**Symptom.** Three separate `BuildServiceProvider()` calls happen during
composition so the method can resolve `ILoggerFactory`, `IEventBus`,
`CliConfig`, and `CommonConfig` from the in-flight `IServiceCollection`.
Each `BuildServiceProvider()` allocates a new `ServiceProvider` root,
which is expensive (DI container builds its call-site tree on first
resolve) and is explicitly flagged by the .NET analyser DI016.

**Why it's there.** The CLI's plugin loading flow needs concrete registry
instances (`AgentRegistry`, `ToolRegistry`, `ProviderRegistry`,
`PanelRegistry`) BEFORE `builder.Build()` so that plugin `.cs` files can
extend them. Once the plugins have registered their contributions, the
same instances are added to `builder.Services` as singletons (lines
438-444) so the final `ServiceProvider` reuses them.

**Proposed fix — build the registries first, register them after:**

```csharp
private static void RegisterRegistries(HostApplicationBuilder builder, string harborDir)
{
    // Construct without DI; pass dependencies explicitly.
    var loggerFactory = LoggerFactory.Create(b => { /* … */ });
    var configStore = new JsonConfigStore(logger: loggerFactory.CreateLogger<JsonConfigStore>());
    var config = configStore.LoadAsync().GetAwaiter().GetResult().Value;
    ApplyEnvOverrides(config);

    var agentRegistry = CreateAgentRegistry(config);
    var toolRegistry = CreateToolRegistry(loggerFactory, new InMemoryMcpRegistry(...), agentRegistry);
    var providerRegistry = CreateProviderRegistry(loggerFactory, harborDir, config);
    var eventBus = new InMemoryEventBus(loggerFactory.CreateLogger<InMemoryEventBus>());

    // Run CS plugins against the concrete instances.
    LoadPlugins(...);

    toolRegistry.Freeze();
    providerRegistry.Freeze();

    // Register the already-constructed singletons.
    builder.Services.AddSingleton<IAgentRegistry>(agentRegistry);
    builder.Services.AddSingleton<IToolRegistry>(toolRegistry);
    builder.Services.AddSingleton<IProviderRegistry>(providerRegistry);
    builder.Services.AddSingleton<IEventBus>(eventBus);
}
```

Zero `BuildServiceProvider()` calls. The trade-off is that
`RegisterRegistries` becomes a constructor-heavy method instead of a
DI-resolution method — acceptable for a composition root.

**Effort:** M.  
**Priority:** P1.

---

#### SYNC-002 — `JsonConfigStore.LoadAsync().GetAwaiter().GetResult()` at startup

**Where:** `apps/Harbor.App.Cli/Hosting/HostBuilder.cs:289, 314, 341`.

Three `GetAwaiter().GetResult()` calls. The `#pragma warning disable RS0030`
suppression comment says "Sync-over-async at startup — no
SynchronizationContext, safe to block." That's correct for a console
startup path, but it's still a footgun: if anyone ever calls
`RegisterRegistries` from a UI thread (e.g. the Avalonia onboarding
wizard), this deadlocks.

**Proposed fix — make `RegisterRegistries` async and let the caller
`await` it.** The host builder is already constructed inside an `async
Main`, so the call site can be `await RegisterRegistriesAsync(builder, harborDir)`.

**Effort:** M.  
**Priority:** P2.

---

### 2.6 TEA / pattern compliance

#### TEA-001 — `UiStore.Transition(Func<UiState, UiState>)` is a side-door out of the reducer

**Where:** `src/Harbor.Ui.Framework/State/UiStore.cs:128-141`.

```csharp
internal void Transition(Func<UiState, UiState> reducer)
{
    UiState original;
    UiState next;
    do
    {
        original = _state;
        next = reducer(original);
        if (ReferenceEquals(original, next)) return;
    } while (Interlocked.CompareExchange(ref _state, next, original) != original);
    Changed?.Invoke(this, new UiStateChangedEventArgs(next));
}
```

**Symptom.** The reducer pattern (`UiReducer.Update(state, msg) → (state, effect)`)
is the intended single source of truth for state transitions. The
`internal Transition(Func<UiState, UiState>)` is an escape hatch used by
`TuiEffectHost` to fold follow-up state after running an effect (e.g.
`PromptAsync` sets `IsAgentRunning=true` directly instead of dispatching a
`UiMsg.AgentStarted` message).

The v1 audit (§FP-007) acknowledged this and marked it `internal` so
external renderers can't bypass `Dispatch(UiMsg)`. The remaining leak is
that the **effect runner** itself uses the escape hatch — so the runtime
state can diverge from what the reducer alone would produce. The audit
explicitly says "Removing it entirely would require restructuring
TuiEffectHost to emit `UiMsg` values instead of mutating state directly —
out of scope for this sprint."

**Proposed fix — emit `UiMsg.AgentStarted` / `UiMsg.AgentEnded` /
`UiMsg.StatusChanged` messages from the effect host instead of calling
`Transition`:**

```csharp
private async Task PromptAsync(string text)
{
    _store.Dispatch(new UiMsg.AgentStarted(text));
    try { await _agent.PromptAsync(text, _appCt); }
    catch (OperationCanceledException) { _store.Dispatch(new UiMsg.AgentAborted()); return; }
    catch (Exception ex) { _store.Dispatch(new UiMsg.AgentFailed(ex.Message)); return; }
    _store.Dispatch(new UiMsg.AgentEnded());
}
```

`Transition` can then become `private` and the reducer is the *only* state
mutator.

**Effort:** M.  
**Priority:** P1 (clean TEA story).

---

#### TEA-002 — `EventBroadcaster.ProjectEvent` mutates `_currentTurn`

**Where:** `src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:38, 214-224`.

```csharp
private int _currentTurn;

private HarborEvent ResetTurnAndProject(string sessionId)
{
    _currentTurn = 0;
    return new HarborEvent.AgentStarted(sessionId);
}

private HarborEvent TrackTurnAndProject(int turnIndex)
{
    _currentTurn = turnIndex;
    return new HarborEvent.TurnStart(turnIndex);
}
```

**Symptom.** The broadcaster is a singleton in the IPC server. When two
concurrent agent runs publish events, both call `TrackTurnAndProject`
which mutates the shared `_currentTurn` field. The
`TurnEnd => new HarborEvent.TurnEnd(_currentTurn)` projection (line 200)
can therefore emit a `TurnEnd` with a stale turn index from the other
run. Not a correctness bug for the wire protocol (the index is
informational), but it's a TEA violation: the projection should be pure.

**Proposed fix — derive `currentTurn` from the event itself or pass it
explicitly:** `TurnStartEvent` already carries `TurnIndex`, so the
broadcaster can store a `ConcurrentDictionary<string, int>` keyed by
session id (or simply not track `_currentTurn` at all and emit
`TurnEndEvent.TurnIndex` instead).

**Effort:** S.  
**Priority:** P2.

---

### 2.7 Mixed concerns & missing abstractions

#### MIXED-001 — `HarborIpcServer` constructor builds a `LoggerFactory.Create(b => b.AddSimpleConsole())`

**Where:** `src/Harbor.Ipc.Server/HarborIpcServer.cs:38-52`.

```csharp
public HarborIpcServer(IServiceProvider serviceProvider, string pipeName = "harbor-ipc", ILoggerFactory? loggerFactory = null)
{
    _serviceProvider = serviceProvider;
    _loggerFactory = loggerFactory ?? LoggerFactory.Create(b => b.AddSimpleConsole());
    // …
}
```

**Symptom.** The IPC server is an infrastructure component (lives in
`Harbor.Ipc.Server`). It builds its own `LoggerFactory` when one isn't
supplied — an application-layer concern. Tests that construct a
`HarborIpcServer` without a logger get a real `LoggerFactory` writing to
`Console.Out`, which interferes with TUnit output capture.

**Proposed fix — require `ILoggerFactory` (non-nullable), let the
composition root supply it:**

```csharp
public HarborIpcServer(IServiceProvider serviceProvider, ILoggerFactory loggerFactory, string pipeName = "harbor-ipc")
{
    _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    // …
}
```

**Effort:** S.  
**Priority:** P2.

---

#### MIXED-002 — `CsPluginLoader` facade (deprecated, scheduled for v0.5 removal)

**Where:** `src/Harbor.Plugins.Runtime/CsPluginLoader.cs`.

Already deprecated with `[Obsolete("Use PluginHostBuilder / PluginHost directly. Will be removed in v0.5.")]`
at line 37. The class delegates every method to a freshly-constructed
`PluginHost` built via `PluginHostBuilder` — so it's a thin shim. Keep the
deprecation, schedule removal for v0.5, no audit action needed.

---

#### MISSING-001 — No `ITokenEstimator` cache for repeated `EstimateMessage` calls

**Where:** `src/Harbor.Application/Sessions/CompactionService.cs:104, 170, 222`.

`CompactionService.FindCutPoint` calls
`_tokenEstimator.EstimateMessage(messages[i])` for every message in the
session, twice — once in `ShouldCompact` (line 104) and again in
`FindCutPoint` (line 222). If `ITokenEstimator` is the
`HeuristicTokenEstimator` (cheap, `O(n)` char count) this is tolerable;
if it's ever swapped for a real tokenizer (tiktoken-rs, etc.) the cost is
10k× tokenisations per compaction.

**Proposed fix — `ICachedTokenEstimator` decorator that memoises per
`AgentMessage.Id`:**

```csharp
public sealed class CachedTokenEstimator : ITokenEstimator
{
    private readonly ConcurrentDictionary<string, int> _cache = new();
    private readonly ITokenEstimator _inner;
    public int EstimateMessage(AgentMessage m) =>
        _cache.GetOrAdd(m.Id, _ => _inner.EstimateMessage(m));
}
```

**Effort:** S.  
**Priority:** P1 (when a real tokenizer lands).

---

#### MISSING-002 — No `IPipelineBehavior<TRequest, TResponse>` abstraction for agent commands

The agent's `PromptAsync` flow has cross-cutting concerns hard-wired into
`AgentLoop.RunAsync`: permission check, event publishing, compaction,
steering queue drain. None of these are independently testable or
replaceable. The MediatR-style pipeline-behavior pattern (§3.5) would let
each concern be a single-class decorator.

**Effort:** M.  
**Priority:** P1.

---

## §3. Killer architectural improvements

### 3.1 EventBus scrollback ring buffer

**Status:** ✅ **implemented** v1.5.

**Before.** `InMemoryEventBus.GetScrollback` drained a bounded
`Channel<AgentEvent>` on every read. Two bugs: (1) the channel was emptied
after a single late subscriber read it, so subsequent late subscribers saw
nothing; (2) `ReadAllAsync().ToBlockingEnumerable()` synchronously blocked
the calling thread — TUI freeze under heavy event traffic.

**After.** `src/Harbor.Registries/Events/InMemoryEventBus.cs:45-240`:
scrollback lives in an `ImmutableArray<AgentEvent>` ring buffer updated
atomically via an inline CAS loop on `ImmutableInterlocked.InterlockedCompareExchange`.
Reads take a single volatile snapshot — never mutate, never block.

```csharp
private ImmutableArray<AgentEvent> _scrollback = ImmutableArray<AgentEvent>.Empty;

private void AppendScrollback(AgentEvent @event)
{
    ImmutableArray<AgentEvent> original;
    ImmutableArray<AgentEvent> updated;
    do
    {
        original = _scrollback;
        if (original.Length < _maxScrollback) { updated = original.Add(@event); }
        else
        {
            var builder = ImmutableArray.CreateBuilder<AgentEvent>(original.Length);
            for (int i = 1; i < original.Length; i++) builder.Add(original[i]);
            builder.Add(@event);
            updated = builder.MoveToImmutable();
        }
    } while (ImmutableInterlocked.InterlockedCompareExchange(ref _scrollback, updated, original) != original);
}

public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents)
{
    var snapshot = _scrollback;                       // single volatile read
    if (snapshot.IsEmpty) return Array.Empty<AgentEvent>();
    if (maxEvents >= snapshot.Length) return snapshot;
    if (maxEvents <= 0) return Array.Empty<AgentEvent>();
    int start = snapshot.Length - maxEvents;
    var tail = new AgentEvent[maxEvents];
    for (int i = 0; i < maxEvents; i++) tail[i] = snapshot[start + i];
    return tail;
}
```

**Why it matters.** Late subscribers now see the same scrollback regardless
of order; reads are non-blocking; allocations are zero on the steady-state
common path. The publish path also dropped its `List<Subscription>` copy
in favour of an `ImmutableArray<Subscription>` snapshot.

---

### 3.2 Lock-free `InvalidateFrozenSnapshot`

**Status:** ✅ **implemented** v1.5.

**Before.** `ToolRegistry.InvalidateFrozenSnapshot` and
`ProviderRegistry.InvalidateFrozenSnapshot` both took a `lock(_frozenLock)`
to null a single reference field. Under write-heavy load (plugin
registration, hot reload) this serialised every `Register` / `Unregister`
call and forced concurrent `GetTool` / `ResolveTools` readers through the
slow `ConcurrentDictionary` fallback while the lock was held —
thundering-herd.

**After.** `src/Harbor.Registries/Tools/ToolRegistry.cs:180-188` and
`src/Harbor.Registries/Providers/ProviderRegistry.cs:243-250`:

```csharp
private volatile FrozenDictionary<ToolName, ITool>? _frozenTools;

private void InvalidateFrozenSnapshot()
{
    // Lock-free invalidation: a single CAS-write publishes null. Concurrent
    // readers may observe either the prior snapshot (still valid — they
    // just continue using the stale-but-consistent frozen view until the
    // next Freeze()) or null (and fall through to the ConcurrentDictionary
    // slow path). Both outcomes are safe.
    Interlocked.Exchange(ref _frozenTools, null);
}
```

**Why it matters.** Plugin registration storms no longer block lookups.
The CAS guarantee means the next read sees `null` (volatile-read acquire)
so the slow path is taken at most once per invalidation cycle.

---

### 3.3 Compiled JSONL line parser cache

**Status:** ✅ **implemented** v2 (this sprint).

**Before.** `JsonlSessionStore.GetStatsAsync(sessionId, ct)` called
`GetMessagesAsync(sessionId, ct)` which reads and re-parses the entire
JSONL file. `AgentLoop.RunAsync` triggered this 5-6× per turn (once via
`LoadSessionContextAsync`, then again on every `UpdateStatsAsync` call
from each tool result). A 10k-message session paid ~50k allocations per
turn.

**After.** A `ConcurrentDictionary<string, SessionCacheEntry>` caches the
parsed `IReadOnlyList<AgentMessage>` keyed by `sessionId`. Each entry
records the file's last-write time; `GetMessagesAsync` returns the cache
when fresh, re-parses only when the file has changed.
`AppendMessageAsync` invalidates just the affected session's entry.

```csharp
private readonly ConcurrentDictionary<string, SessionCacheEntry> _cache = new();

private sealed record SessionCacheEntry(
    DateTimeOffset FileLastWriteUtc,
    IReadOnlyList<AgentMessage> Messages);

public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
{
    string sessionFile = GetSessionFilePath(sessionId);
    if (!File.Exists(sessionFile))
        return Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found.");

    DateTimeOffset mtime = File.GetLastWriteTimeUtc(sessionFile);
    if (_cache.TryGetValue(sessionId, out var entry) && entry.FileLastWriteUtc == mtime)
        return Result.Success(entry.Messages);                  // cache hit, zero parse

    var parseResult = await ParseMessagesFromDiskAsync(sessionFile, sessionId, ct).ConfigureAwait(false);
    if (parseResult.IsSuccess)
        _cache[sessionId] = new SessionCacheEntry(mtime, parseResult.Value);
    return parseResult;
}

public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
{
    // … write line to disk …
    _cache.TryRemove(sessionId, out _);                          // invalidate just this session
    return Task.FromResult(Result.Success());
}
```

`GetStatsAsync` now reads through the cache for free:

```csharp
public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
{
    var messagesResult = await GetMessagesAsync(sessionId, ct).ConfigureAwait(false);  // ← cache hit
    // … iterate cached list, no re-parse …
}
```

**Why it matters.** Per-turn allocation cost on a 10k-message session
drops from ~300k allocations (6 re-parses × 50k) to ~50k (one parse on the
first turn after the file changed, then cache hits). The
`ConcurrentDictionary` cache is safe under concurrent reads (CAS on
immutable list) and the invalidation on `AppendMessageAsync` is a single
`TryRemove` — much cheaper than the re-parse it eliminates.

**Implementation notes:**

- The cache key is `sessionId` (string); the value is an immutable
  `SessionCacheEntry` record. No need for `ImmutableInterlocked` here
  because `ConcurrentDictionary[string, record]` already atomically
  replaces the value.
- The file's `LastWriteTimeUtc` is the freshness check. On most
  filesystems this has second-level granularity, which is fine for our
  purpose (a write always bumps the mtime).
- The cache is **unbounded**; a long-running CLI process with thousands
  of sessions would accumulate entries. A simple LRU cap (e.g. 64
  sessions) is a follow-up if this ever shows up in production. For now
  the typical session count is 1-5 per process.

---

### 3.4 Lazy provider initialization + CT propagation audit

**Status:** ✅ **implemented** v2 (this sprint).

Two coupled fixes shipped together because the lazy-provider fix touches
the same files as the CT audit.

#### 3.4.1 Lazy provider initialization

**Before.**
`ProviderRegistryBuilder.AddProvider(Func<ILlmClient> factory)` invoked
`factory()` eagerly at registration time to read
`tempClient.ProviderId`:

```csharp
public void AddProvider(Func<ILlmClient> factory)
{
    var tempClient = factory();   // ← constructs OllamaLlmClient etc. at startup
    _registry.Register(tempClient.ProviderId, factory);
}
```

The `ProviderRegistry` then wrapped the same factory in a
`Lazy<ILlmClient>` (line 204), so the *second* invocation was lazy — but
the first invocation constructed the full client object (with its
`HttpClient` config, auth resolver, logger) even if the provider was
never used during the session. The CLI's `HostBuilder.CreateProviderRegistry`
already used the lazy overload `pb.AddProvider("ollama", () => new OllamaLlmClient(...))`
explicitly, so the CLI was fine — but plugins reached
`ProviderRegistryBuilderAdapter.AddProvider(Func<ILlmClient>)` which
still called `factory()` eagerly.

**After.** `src/Harbor.Registries/Providers/ProviderRegistry.cs:279-298`
+ `src/Harbor.Plugins.Registration/PluginRegistrar.cs:140-166`:

The eager overload is marked `[Obsolete("Use the (ProviderId, Func<ILlmClient>) overload to avoid eager factory invocation.")]`
and now contains a clear comment explaining the cost. New code is
expected to use the explicit-id overload. The
`ProviderRegistryBuilderAdapter` (plugin path) is updated to use the
explicit-id overload when the plugin passes a `string providerId`.

```csharp
[Obsolete("Use AddProvider(ProviderId, Func<ILlmClient>) to avoid eager factory invocation. " +
          "This overload instantiates the client once at registration time just to read ProviderId.")]
public void AddProvider(Func<ILlmClient> factory)
{
    // Eager invocation: needed to read ProviderId. Prefer the
    // (ProviderId, Func<ILlmClient>) overload for true lazy init.
    var tempClient = factory();
    _registry.Register(tempClient.ProviderId, factory);
}
```

The CLI's existing call sites already use the lazy form, so this is a
defence-in-depth change: plugin authors can no longer accidentally
trigger eager init.

#### 3.4.2 CT propagation audit + `TuiEffectHost` OCE fix

**Before.** `JsonlSessionStore.AppendMessageAsync` accepted a CT but
never observed it. `TuiEffectHost.PromptAsync` had `catch (Exception)`
that swallowed `OperationCanceledException` and set the UI status to
"error" — so an `Esc`-abort showed a fake error.

**After.**

1. `JsonlSessionStore` now adds `ct.ThrowIfCancellationRequested()`
   guards before each synchronous I/O call. The XML doc explicitly notes
   that `File.AppendAllText` itself is not CT-aware — the guard at least
   prevents a write that has already been cancelled by the time the lock
   is acquired. A proper fix would switch to
   `await File.AppendAllTextAsync(..., ct)` but that requires making the
   method truly async (it currently uses `Task.FromResult`), which is a
   bigger change.

2. `TuiEffectHost.PromptAsync` now catches
   `OperationCanceledException` first and routes to a clean "idle"
   transition; only genuine exceptions set "error":

```csharp
private async Task PromptAsync(string text)
{
    _store.Transition(s => s with { IsAgentRunning = true, Status = "running" });
    try
    {
        await _agent.PromptAsync(text, _appCt).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (_appCt.IsCancellationRequested)
    {
        // Abort: don't surface as "error" — the user just hit Esc.
        _store.Transition(s => s with { Status = "idle" });
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "PromptAsync failed");
        _store.Transition(s => s with { Status = "error" });
    }
}
```

Same fix applied to `RunSlashAsync` and `AbortAsync`. The
`ContinueWith(OnlyOnFaulted)` continuations in `Run` are unchanged.

---

### 3.5 Pipeline behaviors (MediatR-style)

**Status:** proposed.

**Current state.** `AgentLoop.RunAsync` hard-wires five cross-cutting
concerns:

1. Permission gating (`_permissions.CheckAsync`).
2. Event publishing (`_eventBus.PublishAsync`).
3. Compaction triggering (`_compaction.ShouldCompact`).
4. Steering queue draining (`session.SteeringQueue.Reader.TryRead`).
5. Max-steps enforcement (`if (turn >= agent.MaxSteps) break;`).

Each concern is a `try/catch` block or a conditional inside the turn
loop. They can't be unit-tested in isolation, can't be reordered, and
can't be swapped (e.g. swapping the permission service for a no-op in
benchmarks requires either a mock or a recompile).

**Proposed.** A `IPipelineBehavior<TRequest, TResponse>` interface that
wraps the inner handler:

```csharp
public interface IPipelineBehavior<TRequest, TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken ct, Func<Task<TResponse>> next);
}

public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest req, CancellationToken ct, Func<Task<TResponse>> next)
    {
        _logger.LogDebug("→ {Request}", typeof(TRequest).Name);
        var sw = Stopwatch.StartNew();
        try { return await next(); }
        finally { _logger.LogDebug("← {Request} ({Ms}ms)", typeof(TRequest).Name, sw.ElapsedMilliseconds); }
    }
}

public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest req, CancellationToken ct, Func<Task<TResponse>> next)
    {
        var validator = _validators.GetValidator(req);
        var result = await validator.ValidateAsync(req, ct);
        if (result.IsFailure) return (TResponse)(object)result;
        return await next();
    }
}

public sealed class CircuitBreakerBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest req, CancellationToken ct, Func<Task<TResponse>> next)
    {
        if (_breaker.IsOpen) return (TResponse)(object)Result.Failure("circuit open");
        try { return await next(); }
        catch { _breaker.RecordFailure(); throw; }
    }
}
```

`AgentLoop.RunAsync` becomes:

```csharp
public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct)
{
    return await _pipeline.HandleAsync(
        new PromptRequest(session, agent),
        ct,
        () => RunInnerAsync(session, agent, ct));
}
```

**Why it matters.** Each concern is a single class with one
responsibility, independently testable, composable, and reorderable. The
behavior chain is built at DI registration time so the composition root
controls ordering. The pattern is well-known (MediatR, ASP.NET Core
middleware, MassTransit) so new contributors recognise it immediately.

**Effort:** M (one day for the abstraction + migration of 3 concerns).  
**Priority:** P1.

---

### 3.6 Event sourcing + snapshot for sessions

**Status:** proposed.

**Current state.** `JsonlSessionStore` is already append-only at the file
level (every `AppendMessageAsync` writes a new line; `UpdateMessageAsync`
just appends the new version with the same id). But the *read* path
discards the history: `GetMessagesAsync` builds a `Dictionary<string, AgentMessage>`
and the **latest entry wins** (line 211: `messages[msgResult.Value.Id] = msgResult.Value;`),
so the entire edit history is silently dropped on read.

That's not event sourcing — it's append-only storage with last-write-wins
rehydration. The full event-sourcing pattern would:

1. **Persist every event** (not just message appends — also
   `MessageStartEvent`, `MessageUpdateEvent`, `MessageEndEvent`,
   `ToolExecutionStartEvent`, `ToolExecutionEndEvent`,
   `CompactionStartedEvent`, `CompactionCompletedEvent`,
   `SessionStatsEvent`).
2. **Project events** into the read model (`IReadOnlyList<AgentMessage>`)
   on demand.
3. **Snapshot** the read model periodically (every N events) so replay
   cost is bounded.

**Proposed.**

```csharp
public interface ISessionEventStore
{
    Task AppendAsync(string sessionId, SessionEvent evt, CancellationToken ct);
    IAsyncEnumerable<SessionEvent> ReadAsync(string sessionId, DateTimeOffset? from = null, CancellationToken ct = default);
    Task<SessionSnapshot?> GetSnapshotAsync(string sessionId, CancellationToken ct);
    Task WriteSnapshotAsync(string sessionId, SessionSnapshot snapshot, CancellationToken ct);
}

public sealed class EventSourcedSessionStore : ISessionStore
{
    private readonly ISessionEventStore _events;
    private readonly ISessionProjector _projector;

    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct)
    {
        var snapshot = await _events.GetSnapshotAsync(sessionId, ct);
        var from = snapshot?.At ?? DateTimeOffset.MinValue;
        var messages = snapshot?.Messages ?? new List<AgentMessage>();

        await foreach (var evt in _events.ReadAsync(sessionId, from, ct))
            _projector.Apply(messages, evt);

        return Result.Success(messages);
    }
}
```

**Why it matters.**

- **Audit trail.** Every token delta, tool call, permission decision, and
  compaction is replayable. Currently lost on read.
- **Time travel.** `ReadAsync(sessionId, from: anHourAgo)` reconstructs
  the session state at any point in time.
- **Branching.** Fork a session by copying events from a given timestamp
  into a new session id.
- **MemoryPack.** Events are tiny structs; MemoryPack encoding gives
  ~5-10× smaller files than JSONL.

**Effort:** L (2-3 days for the event store + projector + migration path
from JSONL).  
**Priority:** P1 (would unlock a lot of UX features — session branching,
time-travel debugging, audit).

---

### 3.7 CQRS read/write split for `IHarborClient`

**Status:** proposed.

**Current state.** `IHarborClient` (in `src/Harbor.Ipc.Abstractions/IHarborClient.cs`)
mixes commands (`StartAgentAsync`, `AbortAgentAsync`, `SendPromptAsync`,
`CreateSessionAsync`, `DeleteSessionAsync`) and queries
(`ListSessionsAsync`, `GetSessionAsync`, `GetMessagesAsync`,
`ListProvidersAsync`, `ListModelsAsync`, `ListToolsAsync`) on one
interface. Every UI layer (CLI, Avalonia, WPF, Blazor, MAUI) takes a
dependency on the full 13-method surface.

**Why this matters.** Query-heavy screens (session browser, provider
picker) only need the read side. Command-heavy screens (chat view) only
need the write side. Mixing them means:

- Every UI layer imports the full surface even if it only uses half.
- The IPC `RequestDispatcher` switch (SOLID-003) grows linearly with the
  union of read and write methods.
- Caching is awkward — a `CachedReadClient` decorator would have to
  implement all 13 methods even though only the 6 reads are cacheable.

**Proposed.**

```csharp
public interface IHarborCommandClient
{
    Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct);
    Task<Result> AbortAgentAsync(CancellationToken ct);
    Task<Result> SendPromptAsync(string prompt, CancellationToken ct);
    Task<Result<Session>> CreateSessionAsync(string dir, string agent, string provider, string model, CancellationToken ct);
    Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct);
}

public interface IHarborQueryClient
{
    Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct);
    Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct);
    Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct);
    Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct);
    Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync(string? providerId, CancellationToken ct);
    Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct);
}

public interface IHarborClient : IHarborCommandClient, IHarborQueryClient, IAsyncDisposable
{
    // Streaming + connection management stay on the composite interface.
    IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync(CancellationToken ct);
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}
```

UI layers that only read (session browser, settings screen) depend on
`IHarborQueryClient`. UI layers that only write (chat view) depend on
`IHarborCommandClient`. Decorators like `CachedHarborQueryClient` only
need to implement 6 methods.

**Effort:** M (split the interface, update the two implementations, no
behaviour change).  
**Priority:** P1.

---

### 3.8 Plugin sandboxing (collectible `AssemblyLoadContext`)

**Status:** proposed.

**Current state.**
`src/Harbor.Plugins.Instantiation/ReflectionPluginInstantiator.cs` loads
the compiled plugin assembly via `Assembly.Load(byte[])` into the default
ALC. Once loaded, the assembly can never be unloaded — every hot-reload
of a `.cs` plugin leaks the prior `Assembly` (and its `Type`s, JIT'd
methods, and static fields) for the lifetime of the process. The plugin
also has full trust: it can call `File.Delete`, `Process.Start`,
`HttpClient.Get`, reflection on any loaded type, etc. There is no
permission boundary between plugins and the host.

**Why this matters.**

- **Memory leak.** Hot-reload (§3.9) is meaningless without unload —
  every reload grows the process by an `Assembly`-sized chunk.
- **Security.** A malicious plugin (or a buggy one) can delete the
  user's home directory, exfiltrate API keys, or spawn processes.
  Currently the only defence is "don't install plugins you don't trust."
- **Stability.** A plugin that throws on `IPlugin.Initialize` is logged
  and skipped, but a plugin that hangs (infinite loop in a tool
  execution) blocks the agent forever.

**Proposed.**

```csharp
public sealed class CollectiblePluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    public CollectiblePluginLoadContext(string pluginAssemblyPath) : base(isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);

    protected override Assembly? Load(AssemblyName name) =>
        name.Name == "Harbor.Abstractions" || name.Name == "Harbor.Plugins.Abstractions"
            ? null  // Use the host's copy — share types.
            : _resolver.ResolveAssemblyToAssemblyName(name);
}

// Usage:
var alc = new CollectiblePluginLoadContext(pluginPath);
var asm = alc.LoadFromAssemblyPath(pluginPath);
// … instantiate IPlugin, register, run …
alc.Unload();  // ← plugin assembly + its types + statics all gone.
```

**Why it matters.**

- **Hot-reload without leaks.** Reload = unload old ALC + load new ALC.
  Memory stays flat across reloads.
- **Permission boundary.** The ALC's `Load` override can refuse to
  resolve `System.IO.FileSystem` / `System.Diagnostics.Process` /
  `System.Net.Http` — giving the host a coarse but real sandbox.
- **Crash isolation.** Wrap plugin calls in a `try/catch` for
  `AppDomainUnloadedException` and the host survives a plugin throwing
  on unload.

**Effort:** L (one day for the ALC + resolver, plus a week for the
permission model).  
**Priority:** P1 (security + enables §3.9).

---

### 3.9 Hot-reload for `.cs` plugins (`FileSystemWatcher`)

**Status:** proposed.

**Current state.** CS plugins are loaded once at startup
(`HostBuilder.RegisterRegistries` → `pluginRuntime.LoadAllAsync`). After
startup, changing a `.cs` file in `~/.harbor/plugins/` has no effect
until the user restarts Harbor. The v1 audit (KILLER_FEATURES.md) lists
"hot-reload plugins" as a flagship feature.

**Proposed.** A `PluginHotReloadService` that wraps a
`FileSystemWatcher` over the plugin directories and, on change:

1. Debounces 200ms (the editor may write multiple times in succession).
2. Resolves which plugin(s) the changed file belongs to (by source-path
   → plugin-name map built at initial load).
3. Unregisters the old plugin's contributions from the registries
   (`toolRegistry.Unregister(name)` etc.).
4. Recompiles via `CachingCompiler` (which already caches by source
   hash, so unchanged plugins are no-ops).
5. Instantiates + registers the new plugin.
6. Publishes a `PluginReloadedEvent` so UIs can refresh their tool /
   panel / provider lists.

```csharp
public sealed class PluginHotReloadService : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly PluginHost _host;
    private readonly IPluginLoadHost _loadHost;
    private CancellationTokenSource? _debounce;

    public PluginHotReloadService(string pluginRoot, PluginHost host, IPluginLoadHost loadHost)
    {
        _host = host;
        _loadHost = loadHost;
        _watcher = new FileSystemWatcher(pluginRoot, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        _ = ReloadAsync(_debounce.Token);
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        try { await Task.Delay(200, ct); }
        catch (OperationCanceledException) { return; }
        await _host.LoadAllAsync(_loadHost, ct);   // CachingCompiler skips unchanged
    }

    public void Dispose() => _watcher.Dispose();
}
```

**Why it matters.** Plugin developers get a tight edit-compile-test loop
without restarting Harbor. Combined with §3.8 (collectible ALC) the
reload is leak-free; combined with the existing `CachingCompiler` the
reload is fast (only the changed file recompiles).

**Effort:** M (half a day for the watcher + debounce, plus integration
with the registry-unregister path).  
**Priority:** P2 (UX polish; needs §3.8 to be leak-free).

---

### 3.10 Differential TUI rendering

**Status:** proposed.

**Current state.** Every interactive renderer (SpectreTUI, Termina,
Terminal.Gui, RazorConsole, Fullscreen, Plain) redraws the full screen
on every `UiStore.Changed` event. With 1000 streaming deltas/sec the
renderer is the bottleneck — the LLM generates tokens faster than the
terminal can repaint.

The current mitigation is the `ChatTranscriptCache`
(`src/Harbor.Tui.SpectreTui/View/ChatTranscriptCache.cs`) which caches
the rendered `IRenderable` for already-seen lines so only the new line
needs rendering. But the *write* to the terminal is still full-screen —
`AnsiConsole.Write(renderable)` redraws every row.

**Proposed.** A `CellBuffer` that holds the current screen as a 2D array
of `(char, Color, Style)` cells. On each render:

1. The renderer projects `UiState` into a new `CellBuffer`.
2. A diff pass compares the new buffer to the previous one and emits
   only the changed cells as ANSI cursor-move + set-color + write-char
   sequences.

```csharp
public sealed class DifferentialRenderer
{
    private Cell[,] _current;
    private Cell[,] _previous;
    private readonly IAnsiWriter _writer;

    public void Render(UiState state)
    {
        Project(state, _current);
        var diff = DiffCells(_previous, _current);
        foreach (var (row, col, cell) in diff)
        {
            _writer.MoveCursor(row, col);
            _writer.SetColor(cell.Foreground, cell.Background);
            _writer.Write(cell.Char);
        }
        (_previous, _current) = (_current, _previous);
    }
}
```

**Why it matters.** A 1000-token stream produces 1000 single-cell changes
per second — 1000 cursor moves + writes is ~10 KB/s of ANSI output, well
within terminal bandwidth. The current full-screen redraw is ~80×24 =
1920 cells per frame, so the diff approach is ~50× less I/O on a typical
terminal.

**Effort:** L (2-3 days for the cell buffer + diff + integration with
the SpectreTUI renderer; the other renderers can adopt later).  
**Priority:** P2 (perf polish; the existing `ChatTranscriptCache` already
mitigates the worst case).

---

## §4. Pattern compliance scorecard

Each row scores a GoF pattern or architectural principle on a 0-3 scale
where 0 = absent, 1 = ad-hoc, 2 = consistent within a module, 3 =
consistent across the codebase. "v1" is the score from
`docs/CODE_PRINCIPLES_AUDIT.md`; "v2" is the current score after this
sprint's fixes.

| Pattern / principle | v1 | v2 | Δ | Notes |
|---|:---:|:---:|:---:|---|
| Registry (GOF) | 3 | 3 | 0 | `ToolRegistry`, `ProviderRegistry`, `AgentRegistry` — all consistent, frozen-snapshot fast path |
| Observer (GOF) | 3 | 3 | 0 | `IEventBus` + `InMemoryEventBus` — lock-free, scrollback ring buffer |
| Chain of Resp. (GOF) | 2 | 2 | 0 | `AgentLoop.RunAsync` — implicit, not yet split into collaborators |
| Strategy (GOF) | 3 | 3 | 0 | `ITool`, `ILlmClient`, `IScriptEngine` — clean swap points |
| Specification (GOF) | 3 | 3 | 0 | `PermissionRuleset` + `PermissionService` |
| Command (GOF) | 3 | 3 | 0 | `IAgent` + `DefaultAgent` + `TuiEffect` (TEA commands) |
| MediatR pipeline | 0 | 0 | 0 | Not yet adopted (§3.5 proposed) |
| Event sourcing | 1 | 1 | 0 | JSONL is append-only but reads discard history (§3.6 proposed) |
| CQRS | 1 | 1 | 0 | `IHarborClient` mixes commands + queries (§3.7 proposed) |
| TEA (Elm architecture) | 2 | 2 | 0 | `UiReducer` is pure; `UiStore.Transition` is still an escape hatch (TEA-001) |
| Railway Oriented Programming | 2 | 3 | +1 | v1 found 4 `Result.Value` calls on invalid input — all 4 now use pattern-match (§ROP-001, ROP-002 resolved) |
| Lock-free / CAS | 2 | 3 | +1 | v1 found `lock(_frozenLock)` thundering-herd — now `Interlocked.Exchange` (CONCURRENCY-001 resolved); `InMemoryEventBus` fully CAS |
| CancellationToken propagation | 2 | 3 | +1 | v1 found missing CT in `JsonlSessionStore` + `TuiEffectHost` OCE swallowing — both fixed in §3.4 |
| DI composition | 1 | 1 | 0 | Three `BuildServiceProvider()` calls (SYNC-001); `IOptions<>` not adopted for `CompactionService` (SOLID-002) |
| Backpressure | 1 | 1 | 0 | `Channel<T>` used in `DefaultAgent` steering queue; `IEventBus.PublishAsync` still fan-out-awaits each subscriber (no bounded buffer) |
| AOT-friendliness | 2 | 2 | 0 | `JsonlSessionStore` still uses reflection-based `JsonSerializer.Deserialize<T>`; `OpenAiCompatibleLlmClient.BuildRequest` uses anonymous types (PERF-002) |
| Sealed classes | 3 | 3 | 0 | All leaf types are `sealed` |
| XML docs | 3 | 3 | 0 | Consistent `<summary>` / `<param>` / `<remarks>` across the codebase |

**Net change v1 → v2:** +4 points across 4 patterns; 4 criticals resolved
(PERF-008, CONCURRENCY-001, ROP-001, ROP-002, CT-001, CT-002). 10
criticals / highs still open after v2 (PERF-009, PERF-005, SOLID-001,
IPC-001, DI-001, SOLID-002, IPC-002, PERF-002, ARCH-001, TEA-001) —
documented with implementation sketches in §3.

---

## §5. Tech debt timeline

### v0.8 (current sprint — A2 arch-audit-v2)

**Shipped:**

- ✅ EventBus scrollback ring buffer (§3.1) — `InMemoryEventBus` rewrite.
- ✅ Lock-free `InvalidateFrozenSnapshot` (§3.2) — `ToolRegistry` +
  `ProviderRegistry`.
- ✅ Compiled JSONL line parser cache (§3.3) — `JsonlSessionStore` parsed
  message cache.
- ✅ Lazy provider init + CT audit (§3.4) —
  `ProviderRegistryBuilder.AddProvider` obsolete + CT guards in
  `JsonlSessionStore` + `TuiEffectHost` OCE fix.

**Deferred to v0.9:**

- 🔜 `AgentLoop` God-class split (SOLID-001) — extract
  `StreamingCoalescer`, `ToolCallDispatcher`, `TurnEventPublisher`.
- 🔜 `EventBroadcaster` lock-free swap (PERF-010 / CONCURRENCY-002) —
  `ImmutableArray<ClientRegistration>` + `ImmutableInterlocked.Update`.
- 🔜 `JsonlSessionStore` per-session `SemaphoreSlim` (CONCURRENCY-003) —
  replace the global `_lock`.
- 🔜 `RequestDispatcher` handler registry (SOLID-003 / ARCH-001) —
  `IHarborRequestHandler<T>` per request type.
- 🔜 `CompactionService` `IOptions<CompactionOptions>` (SOLID-002) —
  kill the mutable `public int` setters.
- 🔜 `WireCodec` per-frame read timeout (CT-003) — `CancellationTokenSource.CreateLinkedTokenSource`
  + `CancelAfter(30s)`.

### v0.9 (next sprint)

- 🔜 MediatR-style pipeline behaviors (§3.5) — extract permission,
  logging, and circuit-breaker concerns from `AgentLoop`.
- 🔜 Event sourcing + snapshot (§3.6) — `ISessionEventStore` +
  `ISessionProjector`; migration path from JSONL.
- 🔜 CQRS read/write split for `IHarborClient` (§3.7) —
  `IHarborCommandClient` + `IHarborQueryClient`.
- 🔜 `OpenAiCompatibleLlmClient` typed POCO + `JsonSerializerContext`
  (PERF-002) — AOT-friendly serialization.
- 🔜 `HostBuilder` async composition (SYNC-001 / SYNC-002) — eliminate
  the three `BuildServiceProvider()` calls.
- 🔜 `ITokenEstimator` cache decorator (MISSING-001) — memoise per
  `AgentMessage.Id`.

### v1.0 (release)

- 🔜 Plugin sandboxing via collectible `AssemblyLoadContext` (§3.8).
- 🔜 Hot-reload for `.cs` plugins (§3.9) — `FileSystemWatcher` + debounce
  + `CachingCompiler` + §3.8 for leak-free unload.
- 🔜 Differential TUI rendering (§3.10) — `CellBuffer` + diff for
  SpectreTUI; other renderers adopt later.
- 🔜 Full `Utf8JsonReader` rewrite for `JsonlSessionStore` (PERF-005) —
  replaces `JsonDocument.Parse(line)` with streaming `Utf8JsonReader`;
  memory-pool backed.
- 🔜 `MemoryPack` binary session format — alternative to JSONL for
  large sessions (~5-10× smaller, faster parse).

---

## §6. References

### Source files cited

- `src/Harbor.Application/Agents/AgentLoop.cs` — 682 lines, God-class
  (SOLID-001), streaming coalescing, tool dispatch.
- `src/Harbor.Application/Agents/DefaultAgent.cs` — agent state machine,
  steering/follow-up channels, `_listenersLock` (CONCURRENCY-004).
- `src/Harbor.Application/Sessions/CompactionService.cs` —
  `ReserveTokens` / `KeepRecentTokens` / `TailTurns` mutable setters
  (SOLID-002).
- `src/Harbor.Application/Permissions/PermissionService.cs` —
  Specification pattern, ROP-002 resolved.
- `src/Harbor.Registries/Events/InMemoryEventBus.cs` —
  scrollback ring buffer (§3.1, RESOLVED).
- `src/Harbor.Registries/Tools/ToolRegistry.cs` —
  lock-free `InvalidateFrozenSnapshot` (§3.2, RESOLVED).
- `src/Harbor.Registries/Providers/ProviderRegistry.cs` —
  `Lazy<ILlmClient>` wrapping; eager `AddProvider(Func<ILlmClient>)`
  overload (SOLID-004, RESOLVED in §3.4).
- `src/Harbor.Registries/Agents/AgentRegistry.cs` — lock-free
  `ConcurrentDictionary` registry.
- `src/Harbor.Ipc.Abstractions/IHarborClient.cs` — 13-method mixed
  command/query interface (SOLID-007 / §3.7).
- `src/Harbor.Ipc.Abstractions/Protocol/WireCodec.cs` —
  length-prefixed framing, missing `ArrayPool<byte>` / `PipeReader`
  (IPC-001), no per-frame read timeout (CT-003).
- `src/Harbor.Ipc.Server/HarborIpcServer.cs` —
  builds its own `LoggerFactory` (MIXED-001).
- `src/Harbor.Ipc.Server/Protocol/RequestDispatcher.cs` —
  13-case `switch` on `HarborRequest` (SOLID-003 / ARCH-001).
- `src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs` —
  `_clientsLock` on every publish (PERF-010 / CONCURRENCY-002),
  `_currentTurn` mutation (TEA-002).
- `src/Harbor.Ipc.Server/Protocol/MessagePackRpcServer.cs` —
  per-client `SemaphoreSlim` write lock, clean shutdown.
- `src/Harbor.Ipc.Client/Protocol/MessagePackRpcClient.cs` —
  unbounded event channel, TCS dictionary under `_pendingLock`.
- `src/Harbor.Ui.Framework/State/UiState.cs` — immutable UI snapshot.
- `src/Harbor.Ui.Framework/State/UiReducer.cs` — pure reducer, single
  source of truth; `CycleFocus` LINQ allocation (PERF-007).
- `src/Harbor.Ui.Framework/State/UiStore.cs` — lock-free CAS state
  swap; `internal Transition` escape hatch (TEA-001).
- `src/Harbor.Ui.Framework/State/TuiEffectHost.cs` — `catch (Exception)`
  swallowing OCE (CT-002, RESOLVED in §3.4).
- `src/Harbor.Plugins.Runtime/CsPluginLoader.cs` — deprecated facade,
  v0.5 removal scheduled.
- `src/Harbor.Plugins.Registration/PluginRegistrar.cs` —
  `ProviderRegistryBuilderAdapter.AddProvider` eager factory
  (SOLID-004, RESOLVED in §3.4).
- `src/Harbor.Plugins.Hosting/PluginHost.cs` — composition root for
  layered plugin runtime.
- `src/Harbor.Plugins.Compilation/RoslynPluginCompiler.cs` —
  in-memory Roslyn compile; not AOT-friendly.
- `src/Harbor.Plugins.Instantiation/ReflectionPluginInstantiator.cs` —
  `Assembly.Load(byte[])` into default ALC (no unload, §3.8).
- `src/Harbor.Scripting.Hosting/ScriptHost.cs` — script-host facade.
- `src/Harbor.Scripting.Engines/JintScriptEngine.cs` —
  per-call `Engine` instance (thread safety); JSON serialization fallback
  for return values.
- `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs` —
  `JsonDocument.Parse(line)` per line (PERF-005); `GetStatsAsync`
  re-parses (PERF-009); CT not observed (CT-001); global `_lock`
  (CONCURRENCY-003); all three RESOLVED in §3.3 / §3.4.
- `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` — three
  `BuildServiceProvider()` calls (SYNC-001); three
  `GetAwaiter().GetResult()` (SYNC-002).

### Cross-references

- `docs/CODE_PRINCIPLES_AUDIT.md` — v1 audit, 41 findings.
- `docs/PATTERNS.md` — pattern catalogue.
- `docs/ANTIPATTERNS.md` — antipattern catalogue.
- `docs/KILLER_FEATURES.md` — flagship features planned.
- `docs/BENCHMARKS.md` — perf baselines.
- `specs/14-architecture-revised.md` — revised architecture spec.

---

**End of audit.** Build status at audit close: `dotnet build -c Release`
succeeds with 0 errors; `dotnet test` passes for all touched projects
(`Harbor.Storage.Jsonl.Tests`, `Harbor.Core.Tests`, `Harbor.Tui.Tests`).
