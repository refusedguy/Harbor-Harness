# Event topology — live in-process contract

> Status: normative for the current implementation. Every guarantee below is
> cited `file:line` against `origin/dev`. If code and doc disagree, code wins
> and the doc must be updated in the same PR.
>
> Related: #27 (migration must preserve these), #33 (engine boundaries).

## 1. Actual topology

There is **no `Channel<AgentEvent>` in the core bus and no single
consumer/dispatcher thread**. The issue's "producers → channel → single
consumer/dispatcher → fan-out" sketch does not match the implementation:
`InMemoryEventBus.PublishAsync` fans out **synchronously and sequentially to a
snapshot of subscribers in the publisher's own call stack**.

```
producers (await PublishAsync directly)
  AgentLoop ..................... src/Harbor.Application/Agents/AgentLoop.cs:157,173,282,316,345,352,363,455,526,549,575
  ToolDispatcher ................ src/Harbor.Application/Agents/ToolDispatcher.cs:212,241,273,317,347,374,390,404,419
  CompactionBehavior ............ src/Harbor.Application/Agents/Pipeline/CompactionBehavior.cs:55,83,118
  SandboxedPluginTool ........... src/Harbor.Plugins.Registration/SandboxedPluginTool.cs:185
        │ await _eventBus.PublishAsync(evt, ct)
        ▼
InMemoryEventBus.PublishAsync ... src/Harbor.Registries/Events/InMemoryEventBus.cs:170
  1. middleware pipeline (registration order; drop/throw ⇒ event dies here)
     ............................ src/Harbor.Registries/Events/InMemoryEventBus.cs:184-204
  2. scrollback ring append ...... src/Harbor.Registries/Events/InMemoryEventBus.cs:207 (+402-424)
  3. lock-free snapshot .......... src/Harbor.Registries/Events/InMemoryEventBus.cs:210
  4. sequential awaited fan-out .. src/Harbor.Registries/Events/InMemoryEventBus.cs:270-318
        │ same event reference to every subscriber in the snapshot, in subscription order
        ▼
subscribers (all in-process, each owns its IDisposable — see §6)
  DefaultAgent listener fan-out ............. src/Harbor.Application/Agents/DefaultAgent.cs:93
  EventBusAppStoreDispatcher → AppStore ..... src/Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs:28
  EventBroadcaster → IPC clients ............ src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:124
  InProcessHarborClient ..................... src/Harbor.Ipc.InProcess/InProcessHarborClient.cs:104
  TUI renderers (ReplRunner ×2, DemoCommand)  apps/Harbor.App.Cli/Repl/ReplRunner.cs:156,236 + apps/Harbor.App.Cli/Commands/DemoCommand.cs:159
  CellForge bridges ......................... src/Harbor.Tui.CellForge/Chat/Streaming/ChatScreenBridge.cs:82 + InlineAgentStreamBridge.cs:31
  Avalonia UiEventRouter .................... apps/Harbor.App.Avalonia/Hosting/UiEventRouter.cs:61
```

`Channel<T>` exists **only at the edges**, never in the core bus:

| Channel | Location | Role |
|---|---|---|
| Per-client outbound queue, bounded 4096, `SingleReader = true` | `src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:490-492` | replay-before-live per client; writer never silently drops |
| In-process client queue, bounded 1024, `DropOldest` | `src/Harbor.Ipc.InProcess/InProcessHarborClient.cs:97-100` | latest-events-visible TUI semantics |
| Steering queues (agent control, **not** events) | `src/Harbor.Application/Agents/DefaultAgent.cs:87-90`, `SubAgentRunner.cs:113-115` | agent steering messages, unrelated to the bus |
| Provider LLM stream channels | `src/Harbor.Providers.*/…LlmClient.cs` | raw LLM deltas before they become `MessageUpdateEvent`s |

## 2. Who updates the store

The bus **never writes any store**. Durability and UI state are subscriber
concerns:

- UI state: `EventBusAppStoreDispatcher.OnAgentEvent` posts into `AppStore`
  marshalled onto the UI thread
  (`src/Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs:28-33`).
  The dispatch is fire-and-post: the bus does not wait for the store to apply.
- Session persistence: `ISessionStore` implementations (Jsonl/Memory/Sqlite)
  are written on the agent path, **not** via bus subscription — there is no
  atomicity between "event published" and "message persisted" (dual-write gap
  from the issue is real: a crash between the two leaves them inconsistent).
- Mandatory vs optional projections: subscribers are **not** classified by the
  fan-out loop (§4) — but the *sink* side now is. #47/S3 added
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
  order per event (`InMemoryEventBus.cs:270-318`), and sequential awaits
  serialize publishes. Tested: `EventTopologySemanticsTests.SingleProducer_OrderPreserved`.
- **G2 — single observed sequence per publish.** All subscribers in one
  publish's snapshot receive the **same event reference in the same position**
  of their stream (snapshot taken once at `InMemoryEventBus.cs:210`). Tested:
  `EventTopologySemanticsTests.SingleObservedSequence_AllSubscribersSeeSameOrder`.
- **G3 — NO wall-clock / cross-producer total order.** Concurrent publishers
  interleave arbitrarily; "channel-acceptance order" is just whichever
  `PublishAsync` body runs first — there is no sequence number on `AgentEvent`
  (contrast `EventEnvelope.Sequence` at the IPC edge,
  `EventBroadcaster.cs:252`). Subscribers attached mid-flight may miss earlier
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
  `InMemoryEventBus.cs:257-268`.)
- **No durability in the bus.** Crash loses everything not yet in
  `ISessionStore`: the scrollback ring is memory-only, and there is no
  graceful-shutdown drain API — `InMemoryEventBus` has no `Dispose`/`Complete`.
  Shutdown hygiene lives in subscribers: `EventBroadcaster.DisposeAsync`
  unsubscribes and stops per-client writers (`EventBroadcaster.cs:105-122`),
  `EventBusAppStoreDispatcher.DisposeAsync` unsubscribes
  (`EventBusAppStoreDispatcher.cs:36-40`), `InProcessHarborClient` completes
  its channel (`InProcessHarborClient.cs:249-250`). In-flight `PublishAsync`
  calls honour the caller's `CancellationToken` — cancellation abandons the
  remaining fan-out (tail loss by design).
- **Late subscribers get future events only.** `Subscribe` never replays
  (`InMemoryEventBus.cs:340-349`); history is available only as an explicit
  pull via `GetScrollback` (repeatable point-in-time copy,
  `InMemoryEventBus.cs:364-394`). Tested:
  `EventTopologySemanticsTests.LateSubscriber_SeesFutureOnly_ScrollbackIsSnapshot`.
  Cross-process reconnect replay exists **only** at the IPC edge via
  `lastSequence` cursor against the 1000-envelope server ring
  (`EventBroadcaster.cs:32-35,49-50,141-189`); a gap older than the ring
  requires full resync (no in-bus cursor/dedup primitive).
- **The scrollback ring is armed by its first reader (#518).** The topology
  answer to "is retention part of the bus contract?" is *yes* — the pull above is
  a declared in-process guarantee, and `IEventBus` is public in the
  zero-dependency `Harbor.Abstractions` assembly that every plugin references
  (`IPluginLoadHost.EventBus` hands a live bus to out-of-tree plugin code, and
  the desktop app declares `[Exposes(typeof(IEventBus))]` as a validated DI
  capability). "No production callers" is therefore **not a provable claim**
  here, and the ring was not deleted. What *was* wrong is the cost: arming
  retention by capacity made a ring nobody read cost a slot write under a lock
  on every publish **and** silently disqualified the fast path for every shipped
  preset (0/200 qualifying publishes on CLI and desktop, 200/200 only headless).
  Retention is now armed by the first `GetScrollback` call, so:
  - an unread ring costs nothing, and the fast path is reachable for the
    shipped presets;
  - history is complete **from the first read onward**. Events published
    strictly before the first read were not retained, and a publish racing the
    arming may be missed — an accepted gap for a diagnostic pull;
  - arming is one-way and permanent, and a disabled ring (`maxScrollback <= 0`)
    can never be armed.
  Tested: `EventBusRetentionArmingGuardTests` (both halves — the ring stays free
  while unread and is genuinely maintained once read, so the fast path can never
  be won by silently dropping history), and the shipped-preset regression row
  `EventBusSinkCompositionTests.DesktopPreset_ZeroSubscribers_Qualifies`.

## 5. Subscriber-error and backpressure policy

- **Throwing subscriber is evicted, others continue** — an exception in one
  handler marks it dead and removes it after the publish
  (`InMemoryEventBus.cs:313-317,320-325`), never halting the renderer or other
  subscribers. Pre-existing test: `EventBusTests.FailingSubscriber_DoesNotBlockOthers`.
- **Middleware failure drops the event for everyone** — a `false` return or a
  throw anywhere in the pipeline returns before scrollback and fan-out
  (`InMemoryEventBus.cs:190-202`). Middleware is therefore a mandatory filter,
  not an optional listener.
- **Slow-subscriber budget (A4):** each handler gets `DefaultHandlerBudget`
  (250 ms, `InMemoryEventBus.cs:62`); an over-budget handler is left behind
  (orphaned but observed), accrues a strike, and is evicted after
  `MaxSlowStrikes` (3, `InMemoryEventBus.cs:65`) consecutive strikes
  (`InMemoryEventBus.cs:246-268,282-311`). `TimeSpan.Zero` disables the budget
  (legacy blocking semantics). Pre-existing tests:
  `EventBusBackpressureTests` (all three cases).
- **Scrollback eviction (drop-oldest):** at capacity the oldest slot is
  overwritten in place, head advances (`InMemoryEventBus.cs:417-422`).
  Tested: `EventTopologySemanticsTests.Scrollback_EvictsOldest_NoLossBelowCapacity`.

## 6. Subscription lifecycle

- Every `Subscribe` returns an `IDisposable`; disposal CAS-removes that exact
  `Subscription` record (`InMemoryEventBus.cs:340-349,426-441`). Disposal is
  idempotent (`_action = null` after first invoke,
  `InMemoryEventBus.cs:472-476`).
- Holders responsible for disposal: `DefaultAgent` (`_eventBusSubscription`),
  `EventBusAppStoreDispatcher.DisposeAsync`, `EventBroadcaster.DisposeAsync`,
  `InProcessHarborClient`, CellForge bridges (`Subscription` property),
  REPL runners / `DemoCommand` (process-lifetime, never disposed — acceptable,
  bus is process-lifetime too).
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
