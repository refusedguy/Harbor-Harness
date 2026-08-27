# Harbor.Telemetry.Otlp

OpenTelemetry OTLP telemetry exporter for Harbor. Depends on `Harbor.Telemetry.Core` and the OpenTelemetry SDK. Must **never** be referenced by CLI/TUI binaries — daemon/server publish profiles only.

## Layer

**Infrastructure (telemetry export).** Outer ring. Physically separated from the CLI/TUI dependency graph by sprint3-C rule.

## What's in it

| File | Purpose |
|------|---------|
| `HarborOtlpExporter.cs` | `HarborOtlpExporter.Attach(...)` — configures and starts an OTLP exporter using the Core `ActivitySource` and `Meter`. |

## Public API summary

- **`HarborOtlpExporter.Attach(string? endpoint = null)`**: configures OpenTelemetry SDK to export traces and metrics via OTLP (default endpoint `http://localhost:4317`). Returns an `IDisposable` that flushes and shuts down the exporter on dispose.

## Dependencies

| Package | Purpose |
|---------|---------|
| `OpenTelemetry` | SDK core |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | OTLP gRPC exporter |

| Project | Purpose |
|---------|---------|
| `Harbor.Telemetry.Core` | `ActivityTracer`, `MeterMetrics`, `HarborTelemetrySources` |

## Tests

No dedicated test project. Integration-validated by daemon/server profiles and `tests/Harbor.Telemetry.Tests/` (which runs against Core, not Otlp).

## Build

```bash
dotnet build src/Harbor.Telemetry.Otlp/Harbor.Telemetry.Otlp.csproj
```

## Known limitations

- OTLP exporter only — no Jaeger, Zipkin, or Console exporter configured here.
- CLI/TUI binaries must not reference this project; use a separate publish profile or container image that includes the exporter.
