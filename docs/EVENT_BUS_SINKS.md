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
(`src/Harbor.Registries/Events/InMemoryEventBus.cs:346-360`). Nothing on the
publish path re-derives it, and no sink type is special-cased by name.

**Optional does not mean "may be skipped in silence."** On the fast path the bus
*drains* the optional sinks and counts the drain
(`OptionalSinkDrainCount`); if a sink drops the event, that drop is counted too
(`OptionalSinkDropCount`) and logged. See §4.

## 2. Middleware / sink registration sites (exhaustive)

| # | Site | Sink | Verdict | Reason (what breaks if the event is silently dropped) |
|---|---|---|---|---|
| 1 | *removed in #478* — was `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` | ~~`TypeFilterMiddleware` (CLI preset)~~ | **n/a — deleted** | it was registered with NO allowed types, so it admitted every event while still declaring **mandatory**: the CLI paid the mandatory-sink cost (fast path off, queue-age envelope built) for a filter that dropped nothing. Deleting it is delivery-neutral — the filter passed everything — and it was the one term in this table that was not true |
| 2 | `apps/Harbor.App.Cli/Hosting/HostBuilder.cs:143` | `EventBusScrollback = 1000` (retention) | **retention, armed by its reader** (#518) | capacity alone no longer gates the fast path: a reader asking for history arms the ring on the spot, so an unread one is neither written nor paid for. With #1 gone, nothing is left in this preset to observe a publish — see §5 |
| 3 | `src/Harbor.Hosting/HarborComposeOptions.cs:151-155` | `CliDefault()` — scrollback 1000, no sinks | **retention, armed by its reader** (#518) | same as #2, and with #1 removed this preset is now structurally identical to #4 |
| 4 | `src/Harbor.Hosting/HarborComposeOptions.cs:157-160` | `DesktopDefault()` — no sink, default capacity | **retention, armed by its reader** (#518) | same as #2 — no sink, and nobody reads the ring |
| 5 | `src/Harbor.Hosting/HarborComposeOptions.cs:79` | `EventBusMiddlewares` factory | **n/a (wiring)** | the composition-time channel through which #1/#4 reach the bus; `null` means "no sinks", never "sniff later" |
| 6 | `src/Harbor.Hosting/Modules/ConfigurationModule.cs:70-74` | the one place a bus is constructed in production | **n/a (wiring)** | computes the mandatory set once and hands the bus to DI; the verdict never depends on DI order |
| 7 | `src/Harbor.Registries/Events/TypeFilterMiddleware.cs` | type allowlist filter | **mandatory** | same reason as a filter should have: it is a contract on what the projections downstream may see, so bypassing it would push unapproved event types into rendered state. Since #478 the verdict is earned rather than assumed — the constructor rejects an empty allowlist, so a mandatory sink cannot be one that admits everything (`tests/Harbor.Architecture.Tests/TypeFilterRegistrationTests.cs` fails the build on a typeless product call site) |
| 8 | `src/Harbor.Registries/Events/SamplingMiddleware.cs:34` | rate limiter for `MessageUpdateEvent` | **optional** | its own job is to drop events for cheaper rendering; nothing downstream becomes *wrong* without it, only less sampled. It is drained on the fast path anyway |
| 9 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:398-450` | the publish guard itself | **n/a (decision)** | the only place a publish may be short-circuited; §3 |
| 10 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:505-518`, counters at `:701-748` | queue-age envelope (`PublishedCount`, `InflightPublishCount`, `OldestPendingAge`, `MaxDispatchDuration`) | **accounting/telemetry — skipped, but counted** | the envelope is deliberately not entered on the fast path (#47/S2 computes percentiles over completed slow-path publishes only). The skip is not silent: `FastPathCount` makes the total publish count exact (`FastPathCount + PublishedCount`) |
| 11 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:519` | `LogDebug("Publishing event: …")` | **optional (diagnostic)** | a log line; not a record anyone reconciles against |
| 12 | `src/Harbor.Plugins.Host/NullEventBus.cs:11` | no-op bus for the standalone MCP plugin host | **optional (all sinks absent by construction)** | there is no subscriber, no retention and no sink, so every publish is unobservable *by design of the host*, not by accident of a guard |
| 13 | `contrib/apps/Harbor.App.Wpf/App.xaml.cs:176` | `AddSingleton<IEventBus, InMemoryEventBus>()` | **retention, armed by its reader** (#518) | no scrollback override → library default capacity. Listed for completeness only: `contrib/` is unmaintained and outside CI, so its composition is not exercised by anything in this repo |
| 14 | `contrib/apps/Harbor.App.Maui/MauiProgram.cs:76` | `AddSingleton<IEventBus, InMemoryEventBus>()` | **retention, armed by its reader** (#518) | same as #13 |
| 15 | `apps/Harbor.App.Avalonia/AppHost.cs:75` + `src/Harbor.Hosting/Modules/ConfigurationModule.cs:70-74` | desktop preset through `AddHarbor` | **retention, armed by its reader** (#518) | no sink, default capacity → **eligible**, 200/200 (§5) |
| 16 | `src/Harbor.Plugins.Abstractions/IPluginLoadHost.cs:60`, `src/Harbor.Abstractions/Plugins/IPlugin.cs:123`, `src/Harbor.Hosting/Modules/PluginLoadHostAdapter.cs:104` | the bus handed to plugins | **optional** | third-party code: a plugin subscription is an extension point, and it must not be able to defeat the fast path for the host. The host's own state never depends on a plugin observing an event |
| 17 | `tests/Harbor.Core.Tests/EventBusMiddlewareTests.cs:14,29,44,58,71,93,107` + stubs at `:277-310` | pass-through / drop / transform / throwing / recording fakes | **mandatory (interface default)** | they are the pipeline's own test surface; declaring nothing must keep them on the full path, and the default does exactly that |
| 18 | `tests/Harbor.Ipc.Tests/TestHost.cs:30`, `tests/Harbor.Plugins.Runtime.Tests/TestSupport/FakePluginLoadHost.cs:76`, `tests/Harbor.LoadTests/MultiSessionLoadHarness.cs:101`, `tests/Harbor.App.Cli.Tests/CellForgeReplSmokeTests.cs:49` | test buses | **mandatory retention / n/a** | constructed with the default capacity; they are not the case under test |
| 19 | `tests/Harbor.TestKit/Fakes.cs:239`, `tests/Harbor.Plugins.Runtime.Tests/TestSupport/RecordingEventBus.cs:9` | `FakeEventBus`, `RecordingEventBus` | **n/a (test doubles)** | they replace the bus rather than sit behind it |
| 20 | `tests/Harbor.Benchmarks/EventBusBenchmark.cs:23`, `EventBusScrollbackBenchmark.cs:27,33,83`, `EventBroadcasterThroughputBenchmark.cs:33`, `EventBusFastPathBenchmark.cs` | benchmark buses | **n/a (measurement)** | the fast-path file is the only one that constructs a 0-scrollback bus on purpose, because it is the row that must report 0 B/op |
| 21 | `tests/Harbor.Benchmarks/AgentLoopBenchmark.cs:354` | `BenchEventBus` (drops everything) | **n/a (measurement)** | isolates AgentLoop cost from bus cost |

<!-- #594 deleted the allow-unwired EventBusAppStoreDispatcher escape hatch
     above, because the type it protected no longer exists. An allowance for a
     deleted type is a hole with no reader. -->
<!-- check-doc-cites: allow-unwired TypeFilterMiddleware — #478 removed the CLI registration; the type survives as the reference implementation its own tests and the architecture guard exercise, and row 7 documents that verdict rather than a shipped composition -->
<!-- check-doc-cites: allow-unwired SamplingMiddleware — no shipped preset registers a sampler; it is the reference optional sink that the composition tests and benchmarks construct to measure the drained-optional-sink path -->

Both remaining allowances are deliberate and are stated here because the type
rule (`tools/check-doc-cites.py` `DOC-TYPE-UNWIRED`) flags any type that no file
under `src/` or `apps/` references. Neither of these two is wired by a product
preset any more, and §5 below is the honest description of that: the only
composition that still exercises a filter is a **test** composition.

## 3. Subscriber sites (exhaustive)

Subscribers are sinks too, but the guard does **not** consult their verdict: the
fast path requires *zero* subscribers, so a subscriber's presence is already a
disqualifier and a "mandatory" flag would be dead weight. The verdicts are still
recorded, because they answer #44's topology question.

| Site | Verdict | Reason |
|---|---|---|
| `src/Harbor.Application/Agents/DefaultAgent.cs:110` | mandatory | listener fan-out drives sub-agent/turn accounting |
| `src/Harbor.Application/Diagnostics/DiagnosticsAggregator.cs:102,103` | mandatory | tool-error and agent-error records must exist for `/diagnostics` |
| `src/Harbor.Ipc.Server/Protocol/EventBroadcaster.cs:129` | mandatory | remote/daemon clients and reconnect replay (own 1000-envelope ring) |
| `src/Harbor.Ipc.InProcess/InProcessHarborClient.cs:104` | mandatory | in-process client stream |
| `src/Harbor.Tui.CellForge/Chat/Streaming/EventSubscription.cs:18` | mandatory | CellForge renderer |
| `src/Harbor.Tui.CellForge/Chat/Streaming/InlineAgentStreamBridge.cs:31` | mandatory | inline agent stream renderer |
| `apps/Harbor.App.Cli/Repl/ReplRunner.cs:270,389` | mandatory | REPL renderer, live for the session |
| `apps/Harbor.App.Cli/Repl/ReplLifecycle.cs:98-103` | mandatory | CellForge REPL event pump (`_events` channel → render loop) |
| `apps/Harbor.App.Avalonia/Hosting/UiEventRouter.cs:61` | mandatory | Avalonia UI state router |
| `apps/Harbor.App.Cli/Commands/DemoCommand.cs:236` | optional | demo renderer; no state depends on it |
| plugin subscriptions (#16 above) | optional | third-party extension point |
| `src/Harbor.Telemetry.Core/TracingAgentProxy.cs:30` | n/a | subscribes to an *agent*, not to the bus |

`EventBusAppStoreDispatcher` used to sit in this table as "the UI state
projection — the state the user reads". It was not: no composition root
constructed it, `AppStore` was not on the TUI read path, and the live UI
projection goes renderer → `UiStore` → `ChatAppReducer`. #664 corrected the row
here and in
[`docs/EVENT_TOPOLOGY.md`](./EVENT_TOPOLOGY.md) §2 while leaving the code.
Then #594 deleted the branch, so the row is now absent because the subscriber
is, not because the table quietly stopped claiming it.

## 4. The guard, term by term

```
fast path  ⇔  FastPathEligible
              = !HasMandatorySink && !RetentionArmed && _subscriptions.IsEmpty
```

- `HasMandatorySink` is computed **once in the constructor** from the sinks'
  own `SinkKind` verdicts (`InMemoryEventBus.cs:346-360`). A sink that declares
  nothing counts as mandatory, so an unconsidered sink keeps the full path.
- `RetentionArmed` flips on the first `GetScrollback` call
  (`InMemoryEventBus.cs:616`) and never flips back (#518).
- `_subscriptions` is the live count; a single subscriber is enough to
  disqualify.

A publish that reaches the guard and qualifies is a publish that **no shipped
preset has any consumer for**. That is the point: it is the state in which
skipping is observably lossless, because there is provably nothing to observe it.

## 5. Measured effect on the shipped presets

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

The last two rows are **test compositions**, not shipped presets. Read them as
the price of each verdict, not as a description of a product build.

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
a budget for the mandatory path** — they are the reason the shipped presets were
moved off it, and the reason a future mandatory sink has to justify itself
against a measured 85× gap rather than a hunch.
