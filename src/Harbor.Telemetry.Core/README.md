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

`EventBusQueueAgeReporter` also writes one Debug line per report, so
`harbor logs --last` shows the queue-age numbers with no debugger and no OTLP
endpoint attached. Cadence is per-app via
`HarborComposeOptions.EventBusQueueAgeReportInterval` (`null` = register the
reporter without a background cadence, so hosts/tests start no timer).

## Public API summary

- **`ActivityTracer`**: singleton `Instance`; `StartSpan(name, tags)` → `ITelemetrySpan` with `SetTag`, `SetError`.
- **`MeterMetrics`**: singleton `Instance`; `Counter` and `Histogram`.
- **`InstrumentedProviderRegistry` / `InstrumentedLlmClient`**: transparent decorators preserving inner behavior while recording provider/turn metrics.
- **`InstrumentedToolRegistry` / `TelemetryToolDecorator`**: transparent tool decorators recording execution duration and outcome.
- **`TracingAgentProxy`**: wraps `IAgent` in a root span per `PromptAsync`/`Steer` cycle.

## Dependencies

| Package | Purpose |
|---------|---------|
| `System.Diagnostics.DiagnosticSource` (transitive) | ActivitySource / Activity APIs |

| Project | Purpose |
|---------|---------|
| `Harbor.Diagnostics.Abstractions` | `ITracer`, `IMetrics`, `ITelemetrySpan` |
| `Harbor.Abstractions` | Domain types (`AgentEvent`, `ProviderId`, etc.) |

## Tests

`tests/Harbor.Telemetry.Tests/` — covers tracer, metrics, and decorator behavior.

## Build

```bash
dotnet build src/Harbor.Telemetry.Core/Harbor.Telemetry.Core.csproj
```

## Known limitations

- No automatic OpenTelemetry protocol export — that's `Harbor.Telemetry.Otlp`.
- AOT-safe by contract: no runtime code generation, no reflection emit, no OTEL SDK types.
