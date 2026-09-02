# Harbor.Plugins.Host

Out-of-process MCP stdio server that exposes Harbor's C# (Roslyn) `ITool` plugins to the AOT core. Spawned by the core via `mcp.json` (`harbor-csharp-plugins`), it never pulls Roslyn into the AOT graph.

## Layer

**Infrastructure (plugin host boundary).** Runs as a normal JIT process (`PublishAot=false`). The AOT core references it only as an external stdio server, not as an assembly.

## What's in it

| File | Purpose |
|------|---------|
| `Program.cs` | Stdio MCP server entry point (`Main`). |
| `McpStdioServer.cs` | JSON-RPC 2.0 NDJSON loop: `initialize`, `tools/list`, `tools/call`, `ping`, `notifications/initialized`. |
| `McpPluginLoadHost.cs` | In-process plugin registry that collects `ITool`, `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin` registrations and exposes them to the stdio server. |
| `NullEventBus.cs` | No-op event bus for plugin-host runs that don't need event streaming. |

## Public API summary

- **`McpStdioServer.RunAsync(CancellationToken)`**: reads JSON-RPC lines from stdin, writes results to stdout.
- **`McpPluginLoadHost`**: `Tools` (read-only tool dictionary), `RegisterTool`, `RegisterProvider`, `RegisterAgent`, `RegisterTuiPlugin`, plus access to `Services`, `Configuration`, `LoggerFactory`, `EventBus`, `Panels`.
- **MCP methods**: `initialize` (returns `2024-11-05` protocol version), `tools/list`, `tools/call`, `ping`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging` | Logging |
| `Microsoft.Extensions.Logging.Console` | Console sink for host logs |
| `Microsoft.Extensions.DependencyInjection` | Service scope for plugin execution |
| `Microsoft.Extensions.Configuration` | Plugin config binding |

| Project | Purpose |
|---------|---------|
| `Harbor.Plugins.Hosting` | Plugin host abstractions |
| `Harbor.Plugins.Storage` | Plugin storage/resolution |
| `Harbor.Plugins.Compilation` | Roslyn compilation pipeline |
| `Harbor.Plugins.Registration` | Plugin registration model |
| `Harbor.Plugins.Instantiation` | Plugin instantiation |
| `Harbor.Plugins.Abstractions` | Plugin contracts |
| `Harbor.Terminal.Abstractions` | Terminal UI plugin contract |
| `Harbor.Abstractions` | Domain types (`AgentEvent`, etc.) |
| `Harbor.Abstractions.Contracts` | Value objects |

## Tests

No dedicated test project. Validated by `Harbor.Plugins.Runtime.Tests` and E2E tests that exercise the MCP boundary.

## Build

```bash
dotnet build src/Harbor.Plugins.Host/Harbor.Plugins.Host.csproj
```

## Known limitations

- Must run as a JIT process; NativeAOT is not supported because Roslyn emits IL at runtime.
- Single-threaded NDJSON loop — one request at a time. Long-running tool executions block the server.
- `NullEventBus` is a stub; real plugin hosts should wire an actual event bus if event streaming is needed.
