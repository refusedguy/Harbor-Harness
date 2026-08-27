# Harbor.Ipc.Client

Out-of-process `IHarborClient` implementation. Talks to a remote `HarborIpcServer` via MessagePack-over-pipe (Windows), MessagePack-over-Unix-domain-socket (Linux/Mac), or MessagePack-over-TCP (cross-host). Used when `HARBOR_MODE=ipc-client`.

## When to use

- **Client mode.** UI process wants to talk to a server running elsewhere.
- When you want the UI process to be small (~5 MB) and not pull in `Harbor.Application` / `Harbor.Registries` / `Harbor.Tools.Builtin` / `Harbor.Providers.*`.
- When the UI is written in a non-.NET language (use the MessagePack protocol directly).

## When NOT to use

- Single-process apps — use `Harbor.Ipc.InProcess`.
- Tests that want to exercise the full client surface without spinning up a pipe server.

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│  IpcHarborClient                                           │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  ClientPipeTransport                                 │  │
│  │  (open Named Pipe / Unix Domain Socket)              │  │
│  └──────────────┬───────────────────────────────────────┘  │
│                 │                                          │
│                 ▼                                          │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  MessagePackRpcClient                                │  │
│  │  ┌────────────────────────────────────────────────┐  │  │
│  │  │  Send loop: request → TCS pending              │  │  │
│  │  │  Read loop: response → complete TCS            │  │  │
│  │  │             EventEnvelope → event channel       │  │  │
│  │  └────────────────────────────────────────────────┘  │  │
│  └──────────────┬───────────────────────────────────────┘  │
│                 │                                          │
│                 ▼                                          │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  EventSubscription                                   │  │
│  │  (IAsyncEnumerable<HarborEvent> via channel reader)  │  │
│  └──────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────┘
```

## Files

| File                               | Purpose                                                                                                                 |
|------------------------------------|-------------------------------------------------------------------------------------------------------------------------|
| `IpcHarborClient.cs`               | `IHarborClient` impl. Maps each call to a `HarborRequest`, sends via RPC, awaits response, deserializes domain payload. |
| `IpcHarborClientExtensions.cs`     | `UseIpcHarborClient()` DI helper.                                                                                       |
| `Protocol/MessagePackRpcClient.cs` | Request/response multiplexer with demultiplexed event stream.                                                           |
| `Protocol/ReconnectableRpcClient.cs` | Auto-reconnect decorator that re-establishes the pipe after a drop and re-subscribes event listeners.                 |
| `Protocol/EventSubscription.cs`    | Adapter from RPC client's event channel to `IAsyncEnumerable<HarborEvent>`.                                             |
| `Transport/ClientPipeTransport.cs` | Named Pipe (Windows) / Unix Domain Socket (Linux/Mac) outbound connect.                                                 |
| `Transport/TcpClientTransport.cs`  | Outbound TCP connect (Tailscale / cross-host scenarios).                                                                |

## Registration

```csharp
// In your composition root (Program.cs):
services.UseIpcHarborClient(pipeName: "harbor-ipc");

// Then at app startup:
var client = host.Services.GetRequiredService<IHarborClient>();
await client.ConnectAsync();
```

## Concurrency

- A single read loop drains the stream. `OkResponse` / `ErrorResponse` complete pending request TCSs by `RequestId`; `EventEnvelope` frames are pushed to an unbounded event channel.
- Writes are serialized through a `SemaphoreSlim` so request frames never interleave.
- Cancellation: registering on the per-call `CancellationToken` removes the pending TCS and cancels the awaiter.

## See also

- `Harbor.Ipc.Abstractions/README.md` — protocol contract.
- `Harbor.Ipc.Server/README.md` — server.
