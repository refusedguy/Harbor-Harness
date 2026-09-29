# Event-bus sinks — mandatory vs optional (the #47/S3 enumeration)

> Status: normative for the current implementation. Every `file:line` is cited
> against this branch. If code and doc disagree, code wins and the doc must be
> updated in the same PR.
>
> This file exists because of a specific failure mode: `InMemoryEventBus` used to
> gate its zero-subscriber fast path on "no middleware registered at all"
> (`_middlewares.Count == 0 && _maxScrollback == 0 && _subscriptions.IsEmpty`).
> That guard is conservative to the point of being unreachable — the CLI
> registers a filter and a 1000-slot scrollback — but tightening it *without*
> knowing which sinks are mandatory is blind bypass, and blind bypass is how
> audit, accounting and state silently stop happening.
>
> So: the table first, the guard second. Related: #47 (epic), #44 (dispatcher
> topology), `docs/EVENT_TOPOLOGY.md` (ordering/delivery contracts).

## 1. The verdict rule

A sink is **mandatory** when silently losing an event to it breaks:

| Domain | Example | What breaks |
|---|---|---|
| state | UI projection, renderer, session store projection | the user sees a session that never advanced |
| audit | "this tool ran", "this message was compacted" | a record that must exist does not |
| accounting | token/cost counters, usage totals | the bill is wrong |
| telemetry | queue age, publish rate, drop counters | a measurement that must be taken is not taken |

A sink is **optional** when it is a diagnostic, a sampler, a secondary
projection or a third-party extension — when nothing downstream of it becomes
*wrong*, only less informed.

The verdict is declared by the sink (`IEventBusMiddleware.SinkKind`,
`src/Harbor.Abstractions/Events/IEventBusMiddleware.cs:51`), **defaults to
mandatory** so an unconsidered sink can never be bypassed by accident, and is
read **once in the bus constructor**
(`src/Harbor.Registries/Events/InMemoryEventBus.cs:263-278`). Nothing on the
publish path re-derives it, and no sink type is special-cased by name.

**Optional does not mean "may be skipped in silence."** On the fast path the bus
*drains* the optional sinks and counts the drain
(`OptionalSinkDrainCount`); if a sink drops the event, that drop is counted too
(`OptionalSinkDropCount`) and logged. See §4.

## 2. Middleware / sink registration sites (exhaustive)

| # | Site | Sink | Verdict | Reason (what breaks if the event is silently dropped) |
|---|---|---|---|---|
| 1 | *removed in #478* — was `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` | ~~`TypeFilterMiddleware` (CLI preset)~~ | **n/a — deleted** | it was registered with NO allowed types, so it admitted every event while still declaring **mandatory**: the CLI paid the mandatory-sink cost (fast path off, queue-age envelope built) for a filter that dropped nothing. Deleting it is delivery-neutral — the filter passed everything — and it was the one term in this table that was not true |
| 2 | `apps/Harbor.App.Cli/Hosting/HostBuilder.cs:123` | `EventBusScrollback = 1000` (retention) | **retention, armed by its reader** (#518) | capacity alone no longer gates the fast path: a reader asking for history arms the ring on the spot, so an unread one is neither written nor paid for. With #1 gone, nothing is left in this preset to observe a publish — see §5 |
| 3 | `src/Harbor.Hosting/HarborComposeOptions.cs:124-128` | `CliDefault()` — scrollback 1000, no sinks | **retention, armed by its reader** (#518) | same as #2, and with #1 removed this preset is now structurally identical to #4 |
| 4 | `src/Harbor.Hosting/HarborComposeOptions.cs:131-134` | `DesktopDefault()` — no sink, default capacity | **retention, armed by its reader** (#518) | same as #2 — no sink, and nobody reads the ring |
| 5 | `src/Harbor.Hosting/HarborComposeOptions.cs:73` | `EventBusMiddlewares` factory | **n/a (wiring)** | the composition-time channel through which #1/#4 reach the bus; `null` means "no sinks", never "sniff later" |
| 6 | `src/Harbor.Hosting/Modules/ConfigurationModule.cs:70-74` | the one place a bus is constructed in production | **n/a (wiring)** | computes the mandatory set once and hands the bus to DI; the verdict never depends on DI order |
| 7 | `src/Harbor.Registries/Events/TypeFilterMiddleware.cs` | type allowlist filter | **mandatory** | same reason as a filter should have: it is a contract on what the projections downstream may see, so bypassing it would push unapproved event types into rendered state. Since #478 the verdict is earned rather than assumed — the constructor rejects an empty allowlist, so a mandatory sink cannot be one that admits everything (`tests/Harbor.Architecture.Tests/TypeFilterRegistrationTests.cs` fails the build on a typeless product call site) |
| 8 | `src/Harbor.Registries/Events/SamplingMiddleware.cs:34` | rate limiter for `MessageUpdateEvent` | **optional** | its own job is to drop events for cheaper rendering; nothing downstream becomes *wrong* without it, only less sampled. It is drained on the fast path anyway |
| 9 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:300` | the publish guard itself | **n/a (decision)** | the only place a publish may be short-circuited; §3 |
| 10 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:401-410`, counters at `:582-605` | queue-age envelope (`PublishedCount`, `InflightPublishCount`, `OldestPendingAge`, `MaxDispatchDuration`) | **accounting/telemetry — skipped, but counted** | the envelope is deliberately not entered on the fast path (#47/S2 computes percentiles over completed slow-path publishes only). The skip is not silent: `FastPathCount` makes the total publish count exact (`FastPathCount + PublishedCount`) |
| 11 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:418` | `LogDebug("Publishing event: …")` | **optional (diagnostic)** | a log line; not a record anyone reconciles against |
| 12 | `src/Harbor.Plugins.Host/NullEventBus.cs:11` | no-op bus for the standalone MCP plugin host | **optional (all sinks absent by construction)** | there is no subscriber, no retention and no sink, so every publish is unobservable *by design of the host*, not by accident of a guard |
| 13 | `contrib/apps/Harbor.App.Wpf/App.xaml.cs:176` | `AddSingleton<IEventBus, InMemoryEventBus>()` | **retention, armed by its reader** (#518) | no scrollback override → library default capacity. Listed for completeness only: `contrib/` is unmaintained and outside CI, so its composition is not exercised by anything in this repo |
| 14 | `contrib/apps/Harbor.App.Maui/MauiProgram.cs:76` | `AddSingleton<IEventBus, InMemoryEventBus>()` | **retention, armed by its reader** (#518) | same as #13 |
| 15 | `apps/Harbor.App.Avalonia/AppHost.cs:75` + `src/Harbor.Hosting/Modules/ConfigurationModule.cs:70-74` | desktop preset through `AddHarbor` | **retention, armed by its reader** (#518) | no sink, default capacity → **eligible**, 200/200 (§5) |
| 16 | `src/Harbor.Plugins.Abstractions/IPluginLoadHost.cs:58`, `src/Harbor.Abstractions/Plugins/IPlugin.cs:123`, `src/Harbor.Hosting/Modules/PluginLoadHostAdapter.cs:104` | the bus handed to plugins | **optional** | third-party code: a plugin subscription is an extension point, and it must not be able to defeat the fast path for the host. The host's own state never depends on a plugin observing an event |
| 17 | `tests/Harbor.Core.Tests/EventBusMiddlewareTests.cs:14,29,44,58,71,93,107` + stubs at `:277-310` | pass-through / drop / transform / throwing / recording fakes | **mandatory (interface default)** | they are the pipeline's own test surface; declaring nothing must keep them on the full path, and the default does exactly that |
| 18 | `tests/Harbor.Ipc.Tests/TestHost.cs:30`, `tests/Harbor.Plugins.Runtime.Tests/TestSupport/FakePluginLoadHost.cs:76`, `tests/Harbor.LoadTests/MultiSessionLoadHarness.cs:101`, `tests/Harbor.App.Cli.Tests/CellForgeReplSmokeTests.cs:49` | test buses | **mandatory retention / n/a** | constructed with the default capacity; they are not the case under test |
| 19 | `tests/Harbor.TestKit/Fakes.cs:239`, `tests/Harbor.Plugins.Runtime.Tests/TestSupport/RecordingEventBus.cs:9` | `FakeEventBus`, `RecordingEventBus` | **n/a (test doubles)** | they replace the bus rather than sit behind it |
| 20 | `tests/Harbor.Benchmarks/EventBusBenchmark.cs:23`, `EventBusScrollbackBenchmark.cs:27,33,83`, `EventBroadcasterThroughputBenchmark.cs:33`, `EventBusFastPathBenchmark.cs` | benchmark buses | **n/a (measurement)** | the fast-path file is the only one that constructs a 0-scrollback bus on purpose, because it is the row that must report 0 B/op |
| 21 | `tests/Harbor.Benchmarks/AgentLoopBenchmark.cs:354` | `BenchEventBus` (drops everything) | **n/a (measurement)** | isolates AgentLoop cost from bus cost |

## 3. Subscriber sites (exhaustive)

Subscribers are sinks too, but the guard does **not** consult their verdict: the
fast path requires *zero* subscribers, so a subscriber's presence is already a
disqualifier and a "mandatory" flag would be dead weight. The verdicts are still
recorded, because they answer #44's topology question.

| Site | Verdict | Reason |
|---|---|---|
| `src/Harbor.Application/Agents/DefaultAgent.cs:110` | mandatory | listener fan-out drives sub-agent/turn accounting |
| `src/Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs:28` | mandatory | the UI state projection — this is the state the user reads |
| `src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:124` | mandatory | remote/daemon clients and reconnect replay (own 1000-envelope ring) |
| `src/Harbor.Ipc.InProcess/InProcessHarborClient.cs:104` | mandatory | in-process client stream |
| `src/Harbor.Tui.CellForge/Chat/Streaming/EventSubscription.cs:18` | mandatory | CellForge renderer |
| `src/Harbor.Tui.CellForge/Chat/Streaming/InlineAgentStreamBridge.cs:31` | mandatory | inline agent stream renderer |
| `apps/Harbor.App.Cli/Repl/ReplRunner.cs:209,298` | mandatory | REPL renderer, live for the session |
| `apps/Harbor.App.Cli/Repl/ReplLifecycle.cs:79` | mandatory | CellForge REPL event pump (`_events` channel → render loop) |
| `apps/Harbor.App.Avalonia/Hosting/UiEventRouter.cs:61` | mandatory | Avalonia UI state router |
| `apps/Harbor.App.Cli/Commands/DemoCommand.cs:159` | optional | demo renderer; no state depends on it |
| plugin subscriptions (#16 above) | optional | third-party extension point |
| `src/Harbor.Telemetry.Core/TracingAgentProxy.cs:32` | n/a | subscribes to an *agent*, not to the bus |

## 4. The guard, term by term

```
fast path  ⇔  !_hasMandatorySink && !_retentionArmed
              && _subscriptions.IsEmpty (lock-free snapshot read)

_hasMandatorySink = any(sink.SinkKind == Mandatory)   // read once, ctor
_retentionArmed   = a GetScrollback call has happened // latches, #518
```

| Term | What it rules out | What it would cost to drop it |
|---|---|---|
| zero subscribers | a live projection, renderer or IPC client | the user watches a session that never advances |
| `!_retentionArmed` (#518) | a **future** history read | a ring nobody has read costs a slot write under a lock on every publish, and — because its mere capacity used to fail this term — it kept every shipped preset off the fast path |
| no mandatory sink | state / audit / accounting / telemetry | the exact silent-loss class #47 forbids |

The scrollback term changed in #518. It used to be `maxScrollback == 0`, a
*configured capacity*: a bus holding 1000 slots for a reader that never came was
indistinguishable, to the guard, from a bus holding 1000 slots for a reader that
might. It is now the *arming flag*, set by the first `GetScrollback` call — so the
term is true exactly when there is a ring being maintained, which is the only
state in which a history read can observe anything. A `maxScrollback <= 0` bus has
no ring and can never be armed, so it keeps the pre-#518 behaviour.

When the fast path qualifies but **optional** sinks are attached, the bus drains
them inline (`DrainOptionalSinksAsync`,
`src/Harbor.Registries/Events/InMemoryEventBus.cs:357`) and increments
`OptionalSinkDrainCount`. An optional sink that returns `false` or throws
increments `OptionalSinkDropCount` and is logged. An optional sink whose
`ValueTask` is not yet complete is **awaited**, not abandoned — a dropped
`Task` would be silent loss with extra steps.

This is the case the old guard could not express: "0 subscribers, 0 scrollback,
one sampler attached" used to take the full path even though nothing mandatory
could observe the publish.

## 5. Measured qualification fraction (not an estimate)

Fraction = `FastPathCount / (FastPathCount + PublishedCount)`, measured through
the real `AddHarbor` composition root by
`tests/Harbor.Hosting.Tests/EventBusSinkCompositionTests.cs` (200 publishes per
row; each row prints its own fraction to stdout):

| Composition | Sinks | Scrollback | Measured qualifying fraction | Why |
|---|---|---|---|---|
| CLI preset (`HostBuilder.CliOptions`) | none (the typeless filter was removed in #478) | 1000, unread | **200 / 200 = 100 %** | no sink (#478), and an unread ring is not maintained (#518) |
| Desktop preset (`DesktopDefault`, Avalonia host) | none | default (1000), unread | **200 / 200 = 100 %** | no sink, and an unread ring is not maintained — so the publish is unobservable |
| Headless (scrollback off, no sinks) | none | 0 | **200 / 200 = 100 %** | the qualifying case, unchanged |
| Headless + sampler | `SamplingMiddleware` (optional) | 0 | **200 / 200 = 100 %**, drained 200× | optional sinks do not disqualify |
| Headless + type filter | `TypeFilterMiddleware` (mandatory) | 0 | **0 / 200 = 0 %** | a mandatory sink alone is enough |

So: **every shipped preset now qualifies in full — 200/200.** Two independent
changes got there, and neither would have got there alone:

- **#478** removed the CLI preset's typeless `TypeFilterMiddleware`. It declared
  itself **mandatory** while admitting every event it was supposed to police, so
  the CLI was paying the mandatory-sink cost for a filter that dropped nothing.
- **#518** stopped configured capacity from gating the fast path. Retention is
  armed by the first `GetScrollback` call, so a ring nobody reads is not
  maintained and not paid for.

The order matters for reading the history: the CLI preset was originally kept off
the fast path by **both** terms, and this file's first version of this sentence
credited only the mandatory verdict. #478 then deleted the sink term and left
capacity as the sole remaining reason — and #518 deleted that. The desktop preset
had **no sinks at all** from the start, so it was never entitled to that
explanation: it was being held out by the scrollback term alone.

What survives as a genuine guarantee is the **last** row, not a shipped preset: a
`TypeFilterMiddleware` carrying a real allowlist. That one earns its mandatory
verdict (the constructor rejects an empty one since #478), and it correctly keeps
the bus off the fast path — a filter is a contract on what projections may see,
not a listener.

The cost side of the same question (what a qualifying publish actually costs)
is measured in `docs/BENCHMARKS.md` §5.4: **1.46 ns / 0 B** with no sink,
**14.5 ns / 0 B** with an optional sink drained, against 83.5 ns / 112 B
(mandatory sink) and 159.2 ns / 200 B (one subscriber). Those timings are **not
re-measured for #518** and still describe the pre-#518 path; the qualification
fractions above are exact because CI asserts them, not because they were eyeballed.

### Follow-up: resolved by #518

This section used to read:

> `GetScrollback` has **no production caller** on this branch (only the interface,
> `NullEventBus`, the bus itself and tests). The 1000-slot retention that
> disqualifies every shipped preset is therefore written on every publish and read
> by nobody. Whether the shipped presets should retain at all is a topology
> decision, not a local optimisation — filed against #44, deliberately **not**
> changed here: doing it silently would have been the "blind bypass" this slice
> exists to prevent.

The topology decision was taken in #44 and is recorded in
`docs/EVENT_TOPOLOGY.md` §4: **retention is part of the bus contract** — history
is an explicit pull via `GetScrollback`, and it is the only replay primitive below
the IPC edge. #518 therefore did *not* delete the ring, and the reason is worth
recording, because "no production callers" was never a safe deletion criterion
here:

- `IEventBus` is public in the **zero-dependency** `Harbor.Abstractions` assembly
  that every plugin references. Removing a member from it is a source and binary
  break for out-of-tree plugins, not a dead-code delete.
- `IPluginLoadHost.EventBus` hands a **live bus to plugin code**
  (`PluginRegistrar` → `SandboxedPluginTool`), and the desktop app declares
  `[Exposes(typeof(IEventBus))]` as a validated DI capability. A plugin can call
  `GetScrollback` at any moment.
- There is no caller census that can prove absence across that boundary.

What *was* provable — and wrong — is the cost, so that is what changed: the ring
is now maintained only once a reader exists. A guard was written **before** the
change (`EventBusRetentionArmingGuardTests`, plus the two flipped preset rows in
`EventBusSinkCompositionTests`) and pins both halves, so the fast path cannot be
won by silently dropping history: unread ring is free, armed ring retains every
later publish and leaves the fast path.

The second flipped row is the CLI preset. It is the one place where two
independent corrections had to be reconciled rather than applied one after the
other. #478 left it with no sinks at all, so its 1000-slot capacity became the
only remaining reason it could not take the fast path; #518 then removed that
too. Both changes are kept, neither is softened — the CLI preset is 200/200 for
the sum of two independent corrections, and the two rows are asserted separately
so a future re-divergence between the CLI and the desktop composition is visible
instead of averaged away.

## 6. What this slice deliberately does not do

- **No `IValueTaskSource`.** The publish return shape is #47/S4's job; this
  slice only moved the state machine off the fast path by splitting
  `PublishAsync` into a synchronous guard + `PublishSlowAsync`, so S4 changes one
  method body instead of the whole file.
- **No change to any shipped composition.** Scrollback capacities, sink sets and
  subscriber wiring are untouched — only their *declared* verdicts were added.
- **No telemetry wiring.** The counters are plain properties on the bus, the
  same way #47/S2's percentile export consumes `PublishedCount`; the telemetry
  layer polls them. No new package, no new project reference.
