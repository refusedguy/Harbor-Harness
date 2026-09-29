# Event topology — live in-process contract

> Status: normative for the current implementation. Every guarantee below is
> cited `file:line` against `origin/dev`. If code and doc disagree, code wins
> and the doc must be updated in the same PR.
>
> Citation rule: a `file:line` here is a **fence**, not a hint.
> `tools/check-doc-cites.py` fails the build when the file is gone, when the
> line is past EOF, or when a backticked type is declared but constructed by
> nothing. Before it existed, this document cited nine line numbers past the end
> of a 249-line `AgentLoop.cs` and named a dispatcher no composition root
> builds — and both existing docs gates passed it, because they check form, not
> content.
>
> Related: #27 (CellForge TUI epic — the renderer that consumes most of this),
> #33 (`Ui.Framework` into reusable layers — the reducer/TEA refactor §2
> describes), #44 (dispatcher topology), #47 (mandatory/optional sinks).

## 1. Actual topology

There is **no `Channel<AgentEvent>` in the core bus and no single
consumer/dispatcher thread**. The issue's "producers → channel → single
consumer/dispatcher → fan-out" sketch does not match the implementation:
`InMemoryEventBus.PublishAsync` fans out **synchronously and sequentially to a
snapshot of subscribers in the publisher's own call stack**.

```
producers (await PublishAsync directly)
  AgentLoop ..................... src/Harbor.Application/Agents/AgentLoop.cs:174,214,221,232
  ToolDispatcher ................ src/Harbor.Application/Agents/ToolDispatcher.cs:215,269,283,298,329,375,418,480,505,516
  CompactionBehavior ............ src/Harbor.Application/Agents/Pipeline/CompactionBehavior.cs:55,83,118
  SandboxedPluginTool ........... src/Harbor.Plugins.Registration/SandboxedPluginTool.cs:188
        │ await _eventBus.PublishAsync(evt, ct)
        ▼
InMemoryEventBus.PublishAsync ... src/Harbor.Registries/Events/InMemoryEventBus.cs:398
  0. fast path .................. src/Harbor.Registries/Events/InMemoryEventBus.cs:395-450
     (zero subscribers + no mandatory sink + ring unarmed ⇒ returns before
      touching a collection; falls through to the slow path otherwise)
  1. middleware pipeline (registration order; drop/throw ⇒ event dies here)
     ............................ src/Harbor.Registries/Events/InMemoryEventBus.cs:528 (+926-959)
  2. scrollback ring append ...... src/Harbor.Registries/Events/InMemoryEventBus.cs:537 (+654-676)
  3. lock-free snapshot .......... src/Harbor.Registries/Events/InMemoryEventBus.cs:540
  4. sequential awaited fan-out .. src/Harbor.Registries/Events/InMemoryEventBus.cs:569 (+973-1075)
        │ same event reference to every subscriber in the snapshot, in subscription order
        ▼
subscribers (all in-process, each owns its IDisposable — see §6)
  DefaultAgent listener fan-out ............. src/Harbor.Application/Agents/DefaultAgent.cs:110
  DiagnosticsAggregator (tool/agent errors) . src/Harbor.Application/Diagnostics/DiagnosticsAggregator.cs:102,103
  EventBroadcaster → IPC clients ............ src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:129
  InProcessHarborClient ..................... src/Harbor.Ipc.InProcess/InProcessHarborClient.cs:104
  CellForge REPL event pump ................. apps/Harbor.App.Cli/Repl/ReplLifecycle.cs:98-103
  TUI renderers (ReplRunner ×2, DemoCommand) apps/Harbor.App.Cli/Repl/ReplRunner.cs:270,389 + apps/Harbor.App.Cli/Commands/DemoCommand.cs:236
  CellForge bridges ......................... src/Harbor.Tui.CellForge/Chat/Streaming/EventSubscription.cs:18 + InlineAgentStreamBridge.cs:31
  Avalonia UiEventRouter → UiStore .......... apps/Harbor.App.Avalonia/Hosting/UiEventRouter.cs:61
```

Every entry in that subscriber list is a live `IEventBus.Subscribe` on a shipped
code path, and the list is exhaustive for `src/` + `apps/`. It is exhaustive
without exception: until #664 it was not — `EventBusAppStoreDispatcher` was
listed here while no composition root constructed it, and a type nothing builds
is not topology. #594 then deleted that branch (with the `AppStore` /
`Harbor.Ui.Framework.Reducers` it fed), so the list is now complete because
there is nothing left off it.

`Channel<T>` exists **only at the edges**, never in the core bus:

| Channel | Location | Role |
|---|---|---|
| Per-client outbound queue, bounded 4096, `SingleReader = true` | `src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:448-453` | replay-before-live per client; writer never silently drops |
| RPC client frame queue, bounded 4096, `DropOldest` | `src/Harbor.Ipc.Client/Protocol/MessagePackRpcClient.cs:36-41` | wire frames arriving from the server; not a bus subscriber |
| In-process client queue, bounded 1024, `DropOldest` | `src/Harbor.Ipc.InProcess/InProcessHarborClient.cs:97-102` | latest-events-visible TUI semantics |
| CellForge REPL event pump, unbounded, `SingleReader = true` | declared `apps/Harbor.App.Cli/Repl/CellForgeReplRunner.cs:103`, written `apps/Harbor.App.Cli/Repl/ReplLifecycle.cs:98-103`, drained `apps/Harbor.App.Cli/Repl/ReplLifecycle.cs:205` | marshals bus events onto the frame-loop thread. The only `Channel<AgentEvent>` in the repo, and it is **downstream of** `Subscribe`, not inside the bus |
| Steering queues (agent control, **not** events) | `src/Harbor.Application/Agents/AgentSteeringQueue.cs:13-15` (held by `DefaultAgent.cs:26`), `src/Harbor.Application/Agents/SubAgentRunner.cs:139` | agent steering messages, unrelated to the bus |
| Provider LLM stream channels | `src/Harbor.Providers.*/…LlmClient.cs` | raw LLM deltas before they become `MessageUpdateEvent`s |

## 2. Who updates the store

The bus **never writes a store itself**. What a subscriber does with the event
is the subscriber's business, and the live branches are:

- **TUI / CLI** — the renderer is the subscriber. `ReplRunner.cs:270,389` and
  `DemoCommand.cs:236` hand every event to `ITuiRenderer.RenderAsync`, and the
  renderer dispatches into a `UiStore`
  (`src/Harbor.Tui.AnsiPlain/AnsiPlainTuiRenderer.cs:74`,
  `src/Harbor.Tui.CellForge/Chat/CellForgeTuiRenderer.cs:314`), which reduces it
  through `ChatAppReducer` (`src/Harbor.Ui.Framework.State/State/UiStore.cs:175-183`).
  The CellForge REPL takes the same route via its `_events` pump
  (`ReplLifecycle.cs:205-209`).
- **Avalonia desktop** — `UiEventRouter` resolves the session-scoped store and
  dispatches there (`apps/Harbor.App.Avalonia/Hosting/UiEventRouter.cs:61-80`).
<!-- check-doc-cites: allow-unwired AppState — named once here as the shape the deleted branch used to fold AgentEvent into. #594 removed that branch, so AppState now has readers (two benchmarks) but no writer under src/ or apps/. It is deliberately left in place rather than deleted with the branch: it lives in the live Harbor.Ui.Framework.State project, and docs/ROADMAP.md records the decision as an open item. Deleting the sentence would hide a true and useful fact — that the legacy shape is producer-less. -->
- **The flat-`AppState` branch no longer exists.** This document used to carry a
  "dead branch, do not follow it" entry here describing
  `EventBusAppStoreDispatcher` → `AppStore` → `Harbor.Ui.Framework.Reducers.AppReducer`
  as the second UI-state projection, with a note that no composition root
  constructed any of it. That note is gone because the code is: #594 deleted
  all three, and with them the whole `src/Harbor.Ui.Framework.Reducers`
  project. The caveat is not lost, it is enforced —
  `tests/Harbor.Architecture.Tests/LegacyFlatTeaBranchRule.cs` fails the build
  if any of it returns, and fails it in the same assertion block that requires
  `State/ChatAppReducer.cs` + `State/UiStore.cs` to still be present, so the
  rule cannot be satisfied by deleting the *live* fold instead.
- **Session persistence** is a separate claim: `ISessionStore` implementations
  (Jsonl/Memory/Sqlite) are written on the agent path, **not** via bus
  subscription — there is no atomicity between "event published" and "message
  persisted" (the dual-write gap from the issue is real: a crash between the two
  leaves them inconsistent).
- **Mandatory vs optional projections:** subscribers are **not** classified by the
  fan-out loop (§4) — but the *sink* side is. #47/S3 added
  `IEventBusMiddleware.SinkKind` (mandatory/optional, declared per sink, computed
  once at composition time) and the exhaustive verdict table lives in
  [`docs/EVENT_BUS_SINKS.md`](./EVENT_BUS_SINKS.md). "Mandatory projection never
  silently skipped" is now enforced for sinks — a bus with a mandatory sink
  always runs the full path, and optional sinks on the fast path are drained and
  counted rather than dropped. Subscriber-side mandatoryity stays implicit: the
  fast path requires **zero** subscribers, so a subscriber's presence is already
  a disqualifier (§7.5).

## 3. Ordering guarantees

- **G1 — per-producer sequential order.** A single producer that `await`s each
  `PublishAsync` before the next publish is observed in publish order by every
  subscriber that stays attached: the fan-out loop awaits handlers in snapshot
  order per event (`InMemoryEventBus.cs:569,973-1058`), and sequential awaits
  serialize publishes. Tested: `EventTopologySemanticsTests.SingleProducer_OrderPreserved`.
- **G2 — single observed sequence per publish.** All subscribers in one
  publish's snapshot receive the **same event reference in the same position**
  of their stream (snapshot taken once at `InMemoryEventBus.cs:540`). Tested:
  `EventTopologySemanticsTests.SingleObservedSequence_AllSubscribersSeeSameOrder`.
- **G3 — NO wall-clock / cross-producer total order.** Concurrent publishers
  interleave arbitrarily; "channel-acceptance order" is just whichever
  `PublishAsync` body runs first — there is no sequence number on `AgentEvent`
  (contrast `EventEnvelope.Sequence` at the IPC edge,
  `EventBroadcaster.cs:263`). Subscribers attached mid-flight may miss earlier
  events of a concurrent burst.
- **G4 — no order across parallel callbacks, by construction.** Callbacks run
  sequentially in subscription order, but a slow subscriber is orphaned after
  its budget (§5) and continues concurrently — its completion order relative
  to later events is undefined.

## 4. Accepted vs processed, crash/shutdown losses

- **G5 — awaited publish ⇒ delivered to attached fast subscribers.**
  `PublishAsync` returns only after every subscriber in the snapshot has been
  awaited (or orphaned/evicted, §5). There is no queue behind the call: when
  the returned `Task` completes, fast subscribers have processed the event.
  Tested: `EventTopologySemanticsTests.AwaitedPublish_MeansDelivered`.
  (This is stronger than "accepted ≠ processed": for the common case they
  coincide; they diverge only for over-budget slow subscribers, whose orphaned
  task stays observed via a fault-logging continuation,
  `InMemoryEventBus.cs:1014-1027`.)
- **No durability in the bus.** Crash loses everything not yet in
  `ISessionStore`: the scrollback ring is memory-only, and there is no
  graceful-shutdown drain API — `IEventBus` declares no `Dispose`/`Complete`
  (`src/Harbor.Abstractions/Events/IEventBus.cs:21-`) and `InMemoryEventBus`
  implements neither. Shutdown hygiene lives in subscribers:
  `EventBroadcaster.DisposeAsync` unsubscribes and stops per-client writers
  (`EventBroadcaster.cs:110-126`), `InProcessHarborClient` unsubscribes and
  completes its channel (`InProcessHarborClient.cs:246-255`). In-flight
  `PublishAsync` calls honour the caller's `CancellationToken` — cancellation
  abandons the remaining fan-out (tail loss by design).
- **Late subscribers get future events only.** `Subscribe` never replays
  (`InMemoryEventBus.cs:578-587`); history is available only as an explicit
  pull via `GetScrollback` (repeatable point-in-time copy,
  `InMemoryEventBus.cs:602-646`). Tested:
  `EventTopologySemanticsTests.LateSubscriber_SeesFutureOnly_ScrollbackIsSnapshot`.
  Cross-process reconnect replay exists **only** at the IPC edge via
  `lastSequence` cursor against the 1000-envelope server ring
  (`EventBroadcaster.cs:51,159-206`); a gap older than the ring
  requires full resync (no in-bus cursor/dedup primitive).
- **The scrollback ring is armed by its first reader (#518).** The topology
  answer to "is retention part of the bus contract?" is *yes* — the pull above is
  a declared in-process guarantee, and `IEventBus` is public in the
  zero-dependency `Harbor.Abstractions` assembly that every plugin references
  (`IPluginLoadHost.EventBus` hands a live bus to out-of-tree plugin code,
  `src/Harbor.Plugins.Abstractions/IPluginLoadHost.cs:60`, and the desktop app
  declares `[Exposes(typeof(IEventBus))]`
  (`apps/Harbor.App.Avalonia/AppHost.cs:38`) as a validated DI capability).
  "No production callers" is therefore **not a provable claim**
  here, and the ring was not deleted. What *was* wrong is the cost: arming
  retention by capacity made a ring nobody read cost a slot write under a lock
  on every publish **and** silently disqualified the fast path for every shipped
  preset (0/200 qualifying publishes on CLI and desktop, 200/200 only headless;
  the preset is `EventBusScrollback = 1000`,
  `src/Harbor.Hosting/HarborComposeOptions.cs:153`).
  Retention is now armed by the first `GetScrollback` call
  (`InMemoryEventBus.cs:616`), so:
  - an unread ring costs nothing, and the fast path is reachable for the
    shipped presets;
  - history is complete **from the first read onward**. Events published
    strictly before the first read were not retained, and a publish racing the
    arming may be missed — an accepted gap for a diagnostic pull;
  - arming is one-way and permanent, and a disabled ring (`maxScrollback <= 0`)
    can never be armed (`InMemoryEventBus.cs:656-658`).
  Tested: `EventBusRetentionArmingGuardTests` (both halves — the ring stays free
  while unread and is genuinely maintained once read, so the fast path can never
  be won by silently dropping history), and the shipped-preset regression row
  `EventBusSinkCompositionTests.DesktopPreset_ZeroSubscribers_Qualifies`.

## 5. Subscriber-error and backpressure policy

- **Throwing subscriber is evicted, others continue** — an exception in one
  handler marks it dead and removes it after the publish
  (`InMemoryEventBus.cs:1046-1058`), never halting the renderer or other
  subscribers. Pre-existing test: `EventBusTests.FailingSubscriber_DoesNotBlockOthers`.
- **Middleware failure drops the event for everyone** — a `false` return or a
  throw anywhere in the pipeline returns before scrollback and fan-out
  (`InMemoryEventBus.cs:528-534,926-959`). Middleware is therefore a mandatory
  filter, not an optional listener.
- **Slow-subscriber budget (A4):** each handler gets `DefaultHandlerBudget`
  (250 ms, `InMemoryEventBus.cs:118`); an over-budget handler is left behind
  (orphaned but observed), accrues a strike, and is evicted after
  `MaxSlowStrikes` (3, `InMemoryEventBus.cs:134`) consecutive strikes
  (`InMemoryEventBus.cs:1014-1027,1030-1051`). `TimeSpan.Zero` disables the budget
  (legacy blocking semantics). Pre-existing tests:
  `EventBusBackpressureTests` (all three cases).
- **Scrollback eviction (drop-oldest):** at capacity the oldest slot is
  overwritten in place, head advances (`InMemoryEventBus.cs:671-674`).
  Tested: `EventTopologySemanticsTests.Scrollback_EvictsOldest_NoLossBelowCapacity`.

## 6. Subscription lifecycle

- Every `Subscribe` returns an `IDisposable`; disposal CAS-removes that exact
  `Subscription` record (`InMemoryEventBus.cs:578-587,678-693`). Disposal is
  idempotent (`_action = null` after first invoke,
  `InMemoryEventBus.cs:1252-1266`).
- Holders responsible for disposal: `DefaultAgent` (`_eventBusSubscription`,
  released in `Dispose`, `DefaultAgent.cs:492-498`),
  `EventBroadcaster.DisposeAsync`, `InProcessHarborClient`, the CellForge REPL
  pump (`using var busPump`, `ReplLifecycle.cs:98-104`), CellForge bridges
  (`Subscription` property, `ChatScreenBridge.cs:105,108`),
  `DiagnosticsAggregator`, and the REPL runners / `DemoCommand`
  (process-lifetime, never disposed — acceptable, bus is process-lifetime too).
- Unsubscribing mid-publish does not affect the in-flight snapshot (the
  publisher iterates its own `ImmutableArray` copy). Tested:
  `EventTopologySemanticsTests.Unsubscribe_StopsFutureEvents_IdempotentDispose`.
- References are strong: handlers are rooted by the bus until disposed. There
  is no weak-subscription mode; forgetting to dispose a short-lived subscriber
  on a process-lifetime bus leaks it (documented, not tested — static by
  inspection).

## 7. Explicitly NOT guaranteed (open questions for reviewer)

1. Session separation / stale-generation filtering / "accounting may still
   update usage" live above the bus (agent/store layers) — no bus primitive
   covers them; semantic tests deferred until those layers declare contracts.
2. IPC reconnect snapshot+tail with cursor dedup and lost-window resync is an
   `EventBroadcaster` behavior, covered by `tests/Harbor.Ipc.Tests` — not
   duplicated here.
3. "Projections never execute domain commands as a replay side effect" is a
   design rule with no enforcement point; needs a harness-level (not bus-level)
   test.
4. Whether the dual-write gap (§2) needs an outbox/atomic-commit mechanism is
   a #27 migration decision, not a bus guarantee.
5. Whether subscribers need mandatory/optional kinds (§2) is **decided for
   sinks** in #47/S3 — see `docs/EVENT_BUS_SINKS.md`. A *subscriber*-side kind
   is still redundant while the fast path requires zero subscribers, and becomes
   necessary only if a future topology ever fans out to a partial set.
