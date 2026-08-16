# Harbor.Ipc.Client

Out-of-process `IHarborClient` implementation. Talks to a remote `HarborIpcServer` via MessagePack-over-pipe (Windows) or MessagePack-over-Unix-domain-socket (Linux/Mac). Used when `HARBOR_MODE=ipc-client`.

## When to use

- **Client mode.** UI process wants to talk to a server running elsewhere.
- When you want the UI process to be small (~5 MB) and not pull in `Harbor.Application` / `Harbor.Registries` / `Harbor.Tools.Builtin` / `Harbor.Providers.*` (which together total ~150 MB).
- When the UI is written in a non-.NET language (use the MessagePack protocol directly — see `docs/IPC.md` §Custom-clients).

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
| `Protocol/EventSubscription.cs`    | Adapter from RPC client's event channel to `IAsyncEnumerable<HarborEvent>`.                                             |
| `Transport/ClientPipeTransport.cs` | Named Pipe (Windows) / Unix Domain Socket (Linux/Mac) outbound connect.                                                 |

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

- `docs/IPC.md` — full architecture, transport, security, performance notes.
- `Harbor.Ipc.Abstractions/README.md` — protocol contract.
- `Harbor.Ipc.Server/README.md` — server.
