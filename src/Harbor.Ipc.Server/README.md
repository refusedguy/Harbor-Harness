# Harbor.Ipc.Server

Hosts the AgentLoop + registries and exposes them via MessagePack RPC over Named Pipe (Windows) or Unix Domain Socket (Linux/Mac). Used when `HARBOR_MODE=ipc-server`.

## When to use

- **Server mode.** Run as a long-lived background process; UI processes (CLI / Avalonia / WPF / Blazor / MAUI / mobile / web / Python / JS / Rust / Go) connect as clients.
- When the UI is in a different process than the agent loop (e.g. remote TUI over SSH, mobile client talking to a desktop server, web UI talking to a backend).

## When NOT to use

- Single-process apps (use `Harbor.Ipc.InProcess`).
- Apps that need zero-latency agent calls — IPC adds ~1 ms per call.

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│  HarborIpcServer                                          │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  ServerPipeTransport                                 │  │
│  │  (Named Pipe / Unix Domain Socket accept loop)       │  │
│  └──────────────┬───────────────────────────────────────┘  │
│                 │                                          │
│                 ▼                                          │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  MessagePackRpcServer                                │  │
│  │  (per-client task: read frame → dispatch → write)    │  │
│  └──────────────┬───────────────────────────────────────┘  │
│                 │                                          │
│                 ▼                                          │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  RequestDispatcher                                   │  │
│  │  (HarborRequest → HarborResponse via IAgent/Store)   │  │
│  └──────────────┬───────────────────────────────────────┘  │
│                 │                                          │
│                 ▼                                          │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  EventBroadcaster                                    │  │
│  │  (IEventBus subscription → all client streams)       │  │
│  └──────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────┘
```

## Files

| File | Purpose |
| --- | --- |
| `HarborIpcServer.cs` | `IHarborServer` impl. Composes transport + dispatcher + broadcaster. |
| `HarborIpcServerExtensions.cs` | `UseHarborIpcServer()` DI helper. |
| `Protocol/RequestDispatcher.cs` | Dispatches `HarborRequest` → `HarborResponse` via the host's DI services. |
| `Protocol/MessagePackRpcServer.cs` | Per-client request loop; concurrent multi-client. |
| `Protocol/EventBroadcaster.cs` | Subscribes to `IEventBus`, pushes `HarborEvent`s to all connected client streams. |
| `Transport/ServerPipeTransport.cs` | Named Pipe (Windows) / Unix Domain Socket (Linux/Mac) accept loop. |

## Registration

```csharp
// In your composition root (Program.cs):
services.UseHarborIpcServer(pipeName: "harbor-ipc");

// Then in your app startup:
var server = host.Services.GetRequiredService<IHarborServer>();
await server.StartAsync();
// ... run until shutdown ...
await server.StopAsync();
```

## Thread safety

- Multiple clients can connect concurrently — each gets its own dedicated `Task`.
- Writes to each client stream are serialized through a per-stream `SemaphoreSlim`.
- The `EventBroadcaster` snapshots the client list under a `Lock` before each broadcast.
- One dead client (broken pipe) never blocks the others — failures are isolated and the dead client is removed.

## See also

- `docs/IPC.md` — full architecture, transport, security, performance notes.
- `Harbor.Ipc.Abstractions/README.md` — protocol contract.
- `Harbor.Ipc.Client/README.md` — remote client.
