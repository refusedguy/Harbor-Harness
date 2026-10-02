# Harbor.Plugins.Host

Out-of-process MCP stdio server that exposes Harbor's C# (Roslyn) `ITool` plugins to the AOT core. Spawned by the core via `mcp.json` (`harbor-csharp-plugins`), it never pulls Roslyn into the AOT graph.

## Layer

**Infrastructure (plugin host boundary).** Runs as a normal JIT process (`PublishAot=false`). The AOT core references it only as an external stdio server, not as an assembly.

## What's in it

| File | Purpose |
|------|---------|
| `Program.cs` | Stdio MCP server entry point (`Main`). |
| `McpStdioServer.cs` | JSON-RPC 2.0 NDJSON loop: `initialize`, `tools/list`, `tools/call`, `ping`, `notifications/initialized`. |
| `McpPluginLoadHost.cs` | In-process plugin registry that collects `ITool` registrations and exposes them to the stdio server. `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin` and `ITuiPanelPlugin` registrations are accepted and logged as not exposed over MCP, then discarded. |
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

**No test project covers this host, and the build is what stands behind it.**

`Harbor.slnx` compiles this project, so a change that does not compile fails
`build` — but nothing here has an assertion of its own. Two things that look
like coverage are not:

- `Harbor.Plugins.Runtime.Tests` does **not** reference this project. It
  references `Harbor.Plugins.Hosting`, a different assembly; the name
  similarity is the whole of the resemblance. It exercises the in-process
  `PluginHost` pipeline, never this stdio server.
- No E2E project references it either. Grepping `McpStdioServer` finds this
  project, and comments in other files using the same words about a different
  subject.

So the JSON-RPC surface below — `initialize` / `tools/list` / `tools/call` /
`ping`, the protocol version, the NDJSON framing — is unverified. Until a
subprocess test drives the binary over a pipe, treat that list as a description
of the code, not as a contract anything holds it to. Wiring one is a new axis:
it needs a process fixture, and the architecture gate deliberately does not
reference this project, so it would not be a rule addition.

## Build

```bash
dotnet build src/Harbor.Plugins.Host/Harbor.Plugins.Host.csproj
```

## Known limitations

- Must run as a JIT process; NativeAOT is not supported because Roslyn emits IL at runtime.
- Single-threaded NDJSON loop — one request at a time. Long-running tool executions block the server.
- `NullEventBus` is a stub; real plugin hosts should wire an actual event bus if event streaming is needed.
