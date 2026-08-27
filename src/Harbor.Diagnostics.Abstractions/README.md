# Harbor.Diagnostics.Abstractions

Harbor's **innermost telemetry contracts** — `ITracer`, `IMetrics`, and `CorrelationContext`. Zero dependencies on any Harbor project, making it safe for the Domain layer and any future layer to reference.

## Layer

**Domain (contracts).** The telemetry onion's center. `Harbor.Telemetry.Core` depends on this; the OTEL SDK (`Harbor.Telemetry.Otlp`) depends on Core. No other Harbor project may reference this assembly directly except Core.

## What's in it

| File | Purpose |
|------|---------|
| `TelemetryContracts.cs` | `ITracer`, `IMetrics`, `ITelemetrySpan` interfaces |
| `Correlation.cs` | `CorrelationContext` record and ambient `Correlation.Current` / `Push` scope |
| `NullTelemetry.cs` | `NullTracer` and `NullMetrics` no-op implementations |

## Public API summary

- **`ITracer`**: `StartSpan(name, tags)` → `ITelemetrySpan`; spans support `SetTag`, `SetError`, `Dispose`.
- **`IMetrics`**: `Counter(name, value, tags)` and `Histogram(name, value, tags)`.
- **`CorrelationContext`**: immutable record holding correlation identifiers; ambient access via `Correlation.Current` and `Correlation.Push(...)`.
- **`NullTracer.Instance` / `NullMetrics.Instance`**: singleton no-ops for telemetry-free runs.

## Dependencies

None. Zero NuGet packages, zero Harbor project references.

## Tests

Referenced transitively by `tests/Harbor.Telemetry.Tests/`.

## Build

```bash
dotnet build src/Harbor.Diagnostics.Abstractions/Harbor.Diagnostics.Abstractions.csproj
```

## Known limitations

- No ActivitySource or Meter implementation here — those live in `Harbor.Telemetry.Core`.
- `CorrelationContext` is ambient (static), not scoped to async flows by default.
