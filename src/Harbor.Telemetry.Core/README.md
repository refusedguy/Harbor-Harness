# Harbor.Telemetry.Core

Harbor's **core telemetry** implementation — `ActivitySource`/`Meter` wrappers, boundary decorators for tools/LLM/agent, and AOT-safe instrumentation. The OTEL SDK lives in `Harbor.Telemetry.Otlp` (daemon/server publish profiles only) and is never referenced by CLI/TUI binaries.

## Layer

**Infrastructure (telemetry core).** Depends only on `Harbor.Diagnostics.Abstractions` (contracts) and `Harbor.Abstractions`. No reflection emit, no OTEL SDK.

## What's in it

| File | Purpose |
|------|---------|
| `HarborTelemetrySources.cs` | Singleton `ActivitySource` (`Harbor.Telemetry`) and `Meter` with version `0.4.0`. |
| `ActivityTracer.cs` | `ITracer` implementation backed by `System.Diagnostics.Activity`. |
| `MeterMetrics.cs` | `IMetrics` implementation backed by `System.Diagnostics.Metrics.Meter`. |
| `InstrumentedLlmClient.cs` | `IProviderRegistry` + `ILlmClient` decorators that emit LLM streaming/turn metrics and spans. |
| `InstrumentedToolRegistry.cs` | `IToolRegistry` + `ITool` decorators that emit tool execution metrics and spans. |
| `TracingAgentProxy.cs` | `IAgent` decorator that wraps agent turns in a single parent span. |
| `EventBusQueueAgeReporter.cs` | Polls the event-bus publish-envelope view (`IEventBusQueueMetrics`) and exports the queue-age distribution (#47/S2). |
| `TelemetryTextExporter.cs` | The **visible** sink (#1741/#1742): an `ActivityListener`/`MeterListener` that writes one line per record to the console or a file, bounded and honest about drops. |
| `TelemetryRedaction.cs` | The **single gate** every exported value passes through (#1741): sensitive keys, secret-shaped values and unknown types never leave the process. |
| `TelemetryExportOptions.cs` | The sink's configuration and its `TelemetryExportStats` snapshot (exported / dropped / truncated counts). |

## Metric names (stable)

All instruments live on the canonical `Harbor.Telemetry` Meter, so any
`MeterListener` — and `HarborOtlpExporter.Attach()` (OTLP), which subscribes to
that Meter by name — observes them with no extra wiring. Names are part of the
contract; rename them only with a deliberate migration.

| Instrument | Kind | Meaning |
|-----------|------|---------|
| `tool.calls` / `tool.duration.ms` | counter / histogram | tool executions and their duration |
| `llm.ttfb.ms` / `llm.tokens` | histogram / counter | time to first token; prompt/completion tokens (`token.type`) |
| `turn.count` / `turn.duration.ms` | counter / histogram | agent turns and their duration |
| `eventbus.dispatch.duration.ms` | histogram | **submission → fan-out-complete duration of one publish**, reported as the p50/p95/p99/max of the bus's bounded window (`dispatch.quantile` tag). Not a "queue latency" of anything else (#47/S2) |
| `eventbus.queue.oldest.pending.age.ms` | histogram | age of the oldest still-pending publish (monotonic stamp; 0 once inflight drains) |
| `eventbus.publish.inflight` | histogram | publishes submitted but not yet fanned out |
| `eventbus.publish.count` | counter | queued publishes, advanced by the delta since the previous report |

The visible sink publishes its own health on the same meter, so a collector
sees the export next to the telemetry it exported:

| Instrument | Kind | Meaning |
|-----------|------|---------|
| `telemetry.export.records` | counter | records written to the sink |
| `telemetry.export.bytes` | counter | bytes written to the sink |
| `telemetry.export.dropped` | counter | records dropped because the export buffer was full |
| `telemetry.export.dropped.bytes` | counter | bytes dropped with them |
| `telemetry.export.truncated` | counter | records shortened to fit the per-record cap |
| `telemetry.export.truncated.bytes` | counter | bytes lost to that shortening |

`EventBusQueueAgeReporter` also writes one Debug line per report, so
`harbor logs --last` shows the queue-age numbers with no debugger and no OTLP
endpoint attached. Cadence is per-app via
`HarborComposeOptions.EventBusQueueAgeReportInterval` (`null` = register the
reporter without a background cadence, so hosts/tests start no timer).

## Wiring

**Nothing is activated by default.** The instrumentation is always on; the
OUTPUT is opt-in, and until a host asks for it the sink does not exist — no
listener is attached, `ActivitySource.StartActivity` keeps returning `null`,
and a normal run (or a test run) writes nothing and allocates nothing.

Two ways to turn it on:

| Knob | Where | Values |
|------|-------|--------|
| `HARBOR_TELEMETRY_EXPORT` | process environment | `none` (default), `console`, `file` |
| `HarborComposeOptions.TelemetryExport` | composition options | `TelemetryExportTarget.None` (default), `.Console`, `.File` |

The environment wins over the option, so an operator can turn the sink on for
one run without touching the host. An unknown value turns the sink OFF and
logs a warning — a typo must not silently produce (or silently hide)
telemetry.

| Knob | Default | Meaning |
|------|---------|---------|
| `HARBOR_TELEMETRY_EXPORT_PATH` | `<HarborDir>/logs/telemetry.txt` | file the `file` target appends to |
| `HARBOR_TELEMETRY_MAX_RECORD_BYTES` | `4096` | cap on one exported record; a longer record is truncated and counted |
| `HARBOR_TELEMETRY_MAX_BUFFER_BYTES` | `262144` | cap on the buffered bytes; a record that does not fit is dropped, counted and logged |

The composition root wires it in
[`Harbor.Hosting/Modules/TelemetryModule.cs`](../../src/Harbor.Hosting/Modules/TelemetryModule.cs)
(`AddHarborTelemetry`), which creates the sink eagerly — the listeners have to
be live before the first span of the run exists.

```bash
# one line per span and per measurement, on stdout
HARBOR_TELEMETRY_EXPORT=console harbor ask "hello"

# the same, into a file (the target for an interactive TUI session)
HARBOR_TELEMETRY_EXPORT=file harbor
```

```
activity tool.execute "duration_ms"="12.4" "status"="Ok" "tool.name"="read"
metric tool.calls "value"="1" "tool.name"="read" "tool.status"="ok"
```

## Public API summary

- **`ActivityTracer`**: singleton `Instance`; `StartSpan(name, tags)` → `ITelemetrySpan` with `SetTag`, `SetError`.
- **`MeterMetrics`**: singleton `Instance`; `Counter` and `Histogram`.
- **`InstrumentedProviderRegistry` / `InstrumentedLlmClient`**: transparent decorators preserving inner behavior while recording provider/turn metrics.
- **`InstrumentedToolRegistry` / `TelemetryToolDecorator`**: transparent tool decorators recording execution duration and outcome.
- **`TracingAgentProxy`**: wraps `IAgent` in a root span per `PromptAsync`/`Steer` cycle.
- **`TelemetryTextExporter`**: the visible sink. `new TelemetryTextExporter(options, logger, output?)` attaches the listeners; `TryExport(kind, name, attributes)` writes one record; `Flush()` writes the buffer out; `Stats` is the exported/dropped/truncated snapshot.
- **`TelemetryRedaction`**: `Apply(key, value)` → the text that may leave, `IsSensitiveKey(key)`, `LooksLikeSecret(value)`, `SanitizeName(name)`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `System.Diagnostics.DiagnosticSource` (transitive) | ActivitySource / Activity / MeterListener APIs |
| `Microsoft.Extensions.Logging.Abstractions` | `ILogger` for the drop and write diagnostics (the LoggerMessage source generator) |

| Project | Purpose |
|---------|---------|
| `Harbor.Diagnostics.Abstractions` | `ITracer`, `IMetrics`, `ITelemetrySpan` |
| `Harbor.Abstractions` | Domain types (`AgentEvent`, `ProviderId`, etc.) |

Referenced by: `Harbor.Hosting` (the composition root wires the sink) and
`Harbor.Telemetry.Otlp`.

## Tests

`tests/Harbor.Telemetry.Tests/` — covers tracer, metrics, decorator behavior, and
the visible sink (activity visible, drop counted and logged, secret withheld,
default off).

## Build

```bash
dotnet build src/Harbor.Telemetry.Core/Harbor.Telemetry.Core.csproj
```

## Known limitations

- No automatic OpenTelemetry protocol export — that's `Harbor.Telemetry.Otlp`, which
  the product binaries deliberately do not reference (see its README).
- The redaction gate is a **first step**, not the whole #1741 waterfall: it keys on
  attribute names and on known credential shapes. A credential with no known prefix,
  under a key that does not name it, is not detected — the key-name rule is the
  primary defence.
- AOT-safe by contract: no runtime code generation, no reflection emit, no OTEL SDK types.
