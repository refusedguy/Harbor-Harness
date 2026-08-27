# Harbor.Logging

Serilog-based logging bootstrap for Harbor. Configures sinks (file, console, async), enriches with correlation context, and exposes a diagnostics sink that forwards structured logs into Harbor's in-memory diagnostics panel.

## Layer

**Infrastructure (cross-cutting).** Used by all app hosts and modules; depends only on `Microsoft.Extensions.Logging.Abstractions`.

## What's in it

| File | Purpose |
|------|---------|
| `LoggerSetup.cs` | `Create(...)` and `CreateWithDiagnostics(...)` factory methods that build Serilog loggers from Harbor config. Also `CleanupOldLogs(...)` for log rotation. |

## Public API summary

- **`LoggerSetup.Create(ILoggerFactory, HarborConfig, IEventBus?)`**: builds a Serilog logger with file/console/async sinks, correlation enrichment, and optional diagnostics panel sink.
- **`LoggerSetup.CreateWithDiagnostics(...)`**: same as `Create` but also attaches a `DiagnosticsSink` that pushes `LogEventLevel` + category + message into `IDiagnosticsPanel`.
- **`LoggerSetup.CleanupOldLogs(string logDir, int maxFiles = 50)`**: deletes oldest log files when the directory exceeds the limit.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Serilog` | Core logging API |
| `Serilog.Extensions.Hosting` | `ILoggerFactory` integration |
| `Serilog.Sinks.File` | Rolling file sink |
| `Serilog.Sinks.Console` | Console sink |
| `Serilog.Sinks.Async` | Async wrapper for other sinks |
| `Microsoft.Extensions.Logging.Abstractions` | `ILoggerFactory` / `ILogger` contracts |

## Tests

No dedicated test project. Logging behavior is exercised indirectly through `Harbor.Hosting.Tests` and app-level integration tests.

## Build

```bash
dotnet build src/Harbor.Logging/Harbor.Logging.csproj
```

## Known limitations

- Log rotation is manual (`CleanupOldLogs`); Serilog's built-in rolling file sink handles retention, but Harbor also sweeps orphaned files.
- Diagnostics panel sink buffers only recent entries (default capacity managed by `InMemoryDiagnosticsPanel`).
