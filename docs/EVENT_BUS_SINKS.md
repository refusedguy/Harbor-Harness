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
| 1 | `apps/Harbor.App.Cli/Hosting/HostBuilder.cs:123-125` | `TypeFilterMiddleware` (CLI preset) | **mandatory** | it is the event-type contract every CLI projection is built on; unfiltered events reaching a renderer/bridge is a state break, not a lost log line |
| 2 | `apps/Harbor.App.Cli/Hosting/HostBuilder.cs:123` | `EventBusScrollback = 1000` (retention) | **mandatory retention** (for `GetScrollback`) | a reader asking for history would get an empty answer; the guard honours retention regardless of who reads it (§5) |
| 3 | `src/Harbor.Hosting/HarborComposeOptions.cs:124-128` | `CliDefault()` — scrollback 1000, no sinks | **mandatory retention** | same as #2; the bus is therefore not fast-path eligible |
| 4 | `src/Harbor.Hosting/HarborComposeOptions.cs:131-134` | `DesktopDefault()` — no sink, default capacity | **mandatory retention** | same as #2; no sink does not mean eligible |
| 5 | `src/Harbor.Hosting/HarborComposeOptions.cs:73` | `EventBusMiddlewares` factory | **n/a (wiring)** | the composition-time channel through which #1/#4 reach the bus; `null` means "no sinks", never "sniff later" |
| 6 | `src/Harbor.Hosting/Modules/ConfigurationModule.cs:70-74` | the one place a bus is constructed in production | **n/a (wiring)** | computes the mandatory set once and hands the bus to DI; the verdict never depends on DI order |
| 7 | `src/Harbor.Registries/Events/TypeFilterMiddleware.cs:39` | type allowlist filter | **mandatory** | same reason as #1 — it is declared, not inferred, and the no-allowlist construction is a configuration, not a licence to be skipped |
| 8 | `src/Harbor.Registries/Events/SamplingMiddleware.cs:34` | rate limiter for `MessageUpdateEvent` | **optional** | its own job is to drop events for cheaper rendering; nothing downstream becomes *wrong* without it, only less sampled. It is drained on the fast path anyway |
| 9 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:300` | the publish guard itself | **n/a (decision)** | the only place a publish may be short-circuited; §3 |
| 10 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:401-410`, counters at `:582-605` | queue-age envelope (`PublishedCount`, `InflightPublishCount`, `OldestPendingAge`, `MaxDispatchDuration`) | **accounting/telemetry — skipped, but counted** | the envelope is deliberately not entered on the fast path (#47/S2 computes percentiles over completed slow-path publishes only). The skip is not silent: `FastPathCount` makes the total publish count exact (`FastPathCount + PublishedCount`) |
| 11 | `src/Harbor.Registries/Events/InMemoryEventBus.cs:418` | `LogDebug("Publishing event: …")` | **optional (diagnostic)** | a log line; not a record anyone reconciles against |
| 12 | `src/Harbor.Plugins.Host/NullEventBus.cs:11` | no-op bus for the standalone MCP plugin host | **optional (all sinks absent by construction)** | there is no subscriber, no retention and no sink, so every publish is unobservable *by design of the host*, not by accident of a guard |
| 13 | `contrib/apps/Harbor.App.Wpf/App.xaml.cs:176` | `AddSingleton<IEventBus, InMemoryEventBus>()` | **mandatory retention** | no scrollback override → library default capacity → not eligible |
| 14 | `contrib/apps/Harbor.App.Maui/MauiProgram.cs:76` | `AddSingleton<IEventBus, InMemoryEventBus>()` | **mandatory retention** | same as #13 |
| 15 | `apps/Harbor.App.Avalonia/AppHost.cs:75` + `src/Harbor.Hosting/Modules/ConfigurationModule.cs:70-74` | desktop preset through `AddHarbor` | **mandatory retention** | no sink, default capacity → not eligible |
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
fast path  ⇔  _fastPathEligible (composition-time constant)
              && _subscriptions.IsEmpty (lock-free snapshot read)

_fastPathEligible = _maxScrollback == 0 && !_hasMandatorySink
_hasMandatorySink = any(sink.SinkKind == Mandatory)   // read once, ctor
```

| Term | What it rules out | What it would cost to drop it |
|---|---|---|
| zero subscribers | a live projection, renderer or IPC client | the user watches a session that never advances |
| `maxScrollback == 0` | a history read | `GetScrollback` silently returns nothing |
| no mandatory sink | state / audit / accounting / telemetry | the exact silent-loss class #47 forbids |

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
| CLI preset (`HostBuilder.CliOptions`) | `TypeFilterMiddleware` (mandatory) | 1000 | **0 / 200 = 0 %** | both the mandatory sink and the retention capacity disqualify |
| Desktop preset (`DesktopDefault`, Avalonia host) | none | default (1000) | **0 / 200 = 0 %** | retention capacity alone |
| Headless (scrollback off, no sinks) | none | 0 | **200 / 200 = 100 %** | the qualifying case |
| Headless + sampler | `SamplingMiddleware` (optional) | 0 | **200 / 200 = 100 %**, drained 200× | optional sinks do not disqualify |
| Headless + type filter | `TypeFilterMiddleware` (mandatory) | 0 | **0 / 200 = 0 %** | a mandatory sink alone is enough |

So: **with today's presets, 0 % of production publishes qualify.** That is the
honest number, and it is the reason the guard was never the bottleneck to
optimise. The value of the change is that the number is now (a) measurable at
runtime from two counters, (b) reachable for any host that turns retention off,
and (c) safe to rely on, because the two terms that gate it are enumerated in
§2 rather than guessed.

### Follow-up (not in this slice)

`GetScrollback` has **no production caller** on this branch (only the interface,
`NullEventBus`, the bus itself and tests). The 1000-slot retention that
disqualifies every shipped preset is therefore written on every publish and read
by nobody. Whether the shipped presets should retain at all is a topology
decision, not a local optimisation — filed against #44, deliberately **not**
changed here: doing it silently would have been the "blind bypass" this slice
exists to prevent.

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
