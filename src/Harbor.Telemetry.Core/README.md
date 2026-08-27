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
