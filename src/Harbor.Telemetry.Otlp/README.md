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

## Wiring

**There is no wiring in the product binaries, and that is the decision, not an
oversight.** This assembly is referenced by nothing under `src/` or `apps/` —
only by `tests/Harbor.Architecture.Tests/`, which pins that boundary
(`TelemetryOtlpBoundaryRules`).

The reason is the AOT publish. `OpenTelemetry.Exporter.OpenTelemetryProtocol`
implements the OTLP protobuf/gRPC framing itself and references the
reflection-based configuration binder directly; with the SDK core that is four
assemblies the published CLI does not carry today — `OpenTelemetry` (338,944
bytes of net10.0 managed IL), `OpenTelemetry.Exporter.OpenTelemetryProtocol`
(195,072), `OpenTelemetry.Api` (83,968) and
`OpenTelemetry.Api.ProviderBuilderExtensions` (24,576). The `aot-publish`
job publishes `apps/Harbor.App.Cli` with NativeAOT — its pass criterion is
that the publish SUCCEEDS. So the OTLP path is deliberately kept out of the
shipped CLI, and the shipping answer to "where does the telemetry go?" is the
visible sink in
[`Harbor.Telemetry.Core`](../Harbor.Telemetry.Core/README.md), which is built on
`ActivityListener`/`MeterListener` — framework types, no SDK, AOT-safe.

One measured nuance, because the obvious story is the wrong one: the
`Microsoft.Extensions.*` packages the SDK pulls in are **already** in the
CLI's resolved package graph (`Microsoft.Extensions.Hosting` brings
`Microsoft.Extensions.Configuration.Binder` and friends with it). The delta is
exactly the four OpenTelemetry assemblies above. The binder is not what makes
this a boundary; the four assemblies are.

A JIT host that wants OTLP carries this assembly itself and calls
`HarborOtlpExporter.Attach(endpoint)`; the exporter subscribes to the canonical
`Harbor.Telemetry` source and meter **by name**, so it observes exactly the
same spans and measurements the visible sink does.

## Dependencies

| Package | Purpose |
|---------|---------|
| `OpenTelemetry` | SDK core |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | OTLP gRPC exporter |

| Project | Purpose |
|---------|---------|
| `Harbor.Telemetry.Core` | `HarborTelemetrySources` (the `ActivitySource`/`Meter` names the exporter subscribes to) |

Both packages are **Apache-2.0**; Harbor is MIT.

**This project is the only one allowed to declare them.** No product project under
`src/` or `apps/` may declare an `OpenTelemetry*` package, and none may reference
this project — the rule, its measurements and the allow-list are in
[docs/ARCHITECTURE_LAYERS.md §5.8b](../../docs/ARCHITECTURE_LAYERS.md#58b-the-telemetry-export-boundary--the-opentelemetry-sdk-stays-out-of-the-product-graph),
and it is enforced by `TelemetryOtlpBoundaryRules`.

## Tests

No dedicated test project. Integration-validated by daemon/server profiles and `tests/Harbor.Telemetry.Tests/` (which runs against Core, not Otlp). The boundary itself — that no product assembly references this project — is pinned by `tests/Harbor.Architecture.Tests/TelemetryOtlpBoundaryRules.cs`.

## Build

```bash
dotnet build src/Harbor.Telemetry.Otlp/Harbor.Telemetry.Otlp.csproj
```

## Known limitations

- OTLP exporter only — no Jaeger, Zipkin, or Console exporter configured here.
- CLI/TUI binaries must not reference this project, and no product project may declare
  an `OpenTelemetry*` package; use a separate publish profile or container image that
  includes the exporter, or an external collector. Both halves are enforced by
  `TelemetryOtlpBoundaryRules`, whose allow-list names this project as the only owner
  of the SDK — and the allow-list is a ratchet in both directions, so an entry that
  stops carrying the SDK is red too.
- **Nobody in the product attaches this exporter.** That is the decision, not an
  oversight: the shipping answer is the visible sink in
  [`Harbor.Telemetry.Core`](../Harbor.Telemetry.Core/README.md). The consumer is an
  embedding JIT host that carries this assembly itself.
- The declared `Harbor.Telemetry.Core` reference binds no type (the source/meter names are compile-time constants, so Roslyn emits no assembly reference) — it is tracked in `EnforcerIntegrityTests.DeclaredButUnboundProjectReferences` rather than silently left in place.
