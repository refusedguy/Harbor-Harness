# Harbor.Plugins.Host

Out-of-process MCP stdio server that exposes Harbor's C# (Roslyn) `ITool` plugins to the AOT core. Spawned by the core via `mcp.json` (`harbor-csharp-plugins`), it never pulls Roslyn into the AOT graph.

## Layer

**Infrastructure (plugin host boundary).** Runs as a normal JIT process (`PublishAot=false`). The AOT core references it only as an external stdio server, not as an assembly.

## What's in it

| File | Purpose |
|------|---------|
| `Program.cs` | Stdio MCP server entry point (`Main`). |
| `McpStdioServer.cs` | JSON-RPC 2.0 NDJSON loop: `initialize`, `tools/list`, `tools/call`, `ping`, `notifications/initialized`. |
| `McpPluginLoadHost.cs` | In-process plugin registry that collects `ITool` registrations and exposes them to the stdio server. Every other `IPluginLoadHost` door (`RegisterProvider`, `RegisterAgent`, `RegisterTuiPlugin`, `RegisterPanelProvider`, `RegisterSessionStore`, `RegisterTuiBackend`) is accepted so plugin `Initialize` never throws, then logged as not exposed over MCP and discarded — see the parity table below. |
| `NullEventBus.cs` | No-op event bus for plugin-host runs that don't need event streaming. |

## Capability parity: in-process JIT vs split host

The split host speaks MCP stdio, which carries tools only. Everything else a
CS-source plugin can contribute in-process is accepted (so `Initialize` never
throws) but logged and discarded here. Each row names the reason, not just the
verdict (#419).

| Capability | In-process JIT (`CsPluginLoader`) | Split host (this exe) | Reason |
|------------|-----------------------------------|-----------------------|--------|
| Tool (`IToolPlugin`) | supported | supported | MCP `tools/list` + `tools/call` carry name, description, JSON schema and result — the full tool contract fits the wire. |
| Provider (`IProviderPlugin`) | supported | unsupported (logged, discarded) | An `ILlmClient` factory cannot cross stdio: token streaming is per-turn and latency-sensitive, and auth material must not leave the core process. |
| Agent (`IAgentPlugin`) | supported | unsupported (logged, discarded) | `AgentDefinition` carries a `PermissionRuleset` the core evaluates per call; rehydrating agents from a description string would silently drop the policy. |
| TUI view (`ITuiPlugin`) | closed seam (#564 — collected, never rendered) | unsupported (logged, discarded) | Views must live in the renderer process; #555 freezes new TUI axes, so the split does not reopen this one. |
| TUI panel (`ITuiPanelPlugin`) | supported | unsupported (logged, discarded) | The panel registry lives in the TUI renderer process; panels are UI-process state, not serializable registrations. |
| Session store (`ISessionStorePlugin`, #581) | supported | unsupported (logged, discarded) | The store is resolved in-core at startup via `HARBOR_STORAGE`; a store behind a pipe would put every session read on IPC. |
| TUI backend (`ITuiBackendPlugin`, #581/#584) | supported | unsupported (logged, discarded) | The renderer is constructed in the UI process; a backend factory cannot be expressed as an MCP tool. |
| Skill / MCP server | n/a — not plugin axes | n/a | Skills ship as the builtin `skill` tool and MCP servers as `IMcpRegistry` entries; there is no plugin interface to implement. |

## Process boundary is not a sandbox

This host runs CS-source plugins with **full trust**, exactly like the
in-process loader — only reviewed source files belong in the plugin
directories (see [docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)). The
split exists so Roslyn stays out of the AOT binary, not to isolate untrusted
code: a separate stdio process gives crash containment (below), not a security
boundary. No sandbox claim without an actual sandbox; the untrusted-plugin
policy is a separate statement and does not live here.

## Failure paths

- **Tool throws** → `tools/call` returns an `isError` result with the message;
  the server keeps serving (`McpStdioServer.HandleToolCallAsync` catches per
  call — one bad tool cannot wedge the loop).
- **Unknown tool / method** → JSON-RPC `-32602` / `-32601`, never a silent
  empty result.
- **Host process dies** → the parent sees stdio EOF on the pipe. That is a
  transport failure the core surfaces, not a silent no-plugins state: the
  `mcp.json` entry is opt-in, so absence of the entry means no tools listed
  (explicit config), while death of a configured entry must error loudly
  parent-side.
- **Protocol mismatch** → the server announces `protocolVersion 2024-11-05`
  on `initialize` and negotiates nothing further. A client requiring a newer
  version must fail with an actionable error on its side; the server will not
  guess.

## Public API summary

- **`McpStdioServer.RunAsync(CancellationToken)`**: reads JSON-RPC lines from stdin, writes results to stdout.
- **`McpStdioServer.HandleLineAsync(string, CancellationToken)`**: one frame in, one response frame (or null) out — the seam the test suite drives.
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

`tests/Harbor.Plugins.Host.Tests` drives the JSON-RPC surface through
`McpStdioServer.HandleLineAsync` (no subprocess needed): `initialize`
(protocol version `2024-11-05`, server name `harbor-csharp-plugins`),
`tools/list`, `tools/call` (success, unknown tool `-32602`, missing name
`-32602`, throwing tool → `isError` with the loop still alive), unknown
method (`-32601`), `ping`, and the response-less frames
(`notifications/initialized`, method-less lines → no frame).

The parity suite pins the table above as an executable spec: every non-tool
door accepts and discards, and `tools/list` stays empty for them. The
declared-contract test pins `PluginContractAssemblies` (#1012), including
`Harbor.Ui.Framework.State`.

Latency (acceptance data for #1055): `ToolsCall_DispatchLatency_StaysInteractive`
measures loopback `tools/call` dispatch (60 calls, warmed up) and fails above
a 100 ms mean. Each test's stdout — the measured mean — is filed under the
test in TRX and rendered into the CI summary by
`tools/test-measurements.py`, so the number is re-measured on every run
rather than quoted once here.

## Build

```bash
dotnet build src/Harbor.Plugins.Host/Harbor.Plugins.Host.csproj
```

## Known limitations

- Must run as a JIT process; NativeAOT is not supported because Roslyn emits IL at runtime.
- Single-threaded NDJSON loop — one request at a time. Long-running tool executions block the server.
- `NullEventBus` is a stub; real plugin hosts should wire an actual event bus if event streaming is needed.
