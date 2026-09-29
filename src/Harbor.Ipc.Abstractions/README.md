# Harbor.Ipc.Abstractions

Shared contract for Harbor's IPC layer. Referenced by **both** the in-process client and the MessagePack-over-pipe IPC client/server.

## What's here

| File                          | Purpose                                                                                               |
|-------------------------------|-------------------------------------------------------------------------------------------------------|
| `IHarborClient.cs`            | Client interface every UI talks to (agent, sessions, providers, tools, events).                       |
| `IHarborServer.cs`            | Host-side interface (`StartAsync` / `StopAsync` / `IsRunning`).                                       |
| `IPipeTransport.cs`           | Transport abstraction (Named Pipe on Windows, Unix Domain Socket on Linux/Mac).                       |
| `HarborEvent.cs`              | 11-case discriminated union of streaming events (simplified, wire-stable projection of `AgentEvent`). |
| `HarborEventMapping.cs`       | Bidirectional mapping `HarborEvent ↔ HarborEventData` (wire DTO).                                     |
| `AgentEventProjector.cs`      | **The one** `AgentEvent → HarborEvent` projection, shared by every host (was duplicated in two, #495). |
| `EventProjection.cs`          | `EventProjectionOutcome` + `EventProjection` — the typed result: emitted / no-wire-case / unmapped.     |
| `ProjectionState.cs`          | Per-host state the projection needs: active session + per-session turn index.                          |
| `Protocol/HarborRequest.cs`   | MessagePack `[Union]` of all request types (StartAgent, SendPrompt, CreateSession, ListTools, ...).   |
| `Protocol/HarborResponse.cs`  | MessagePack `[Union]` of three response shapes: `OkResponse`, `ErrorResponse`, `EventEnvelope`.       |
| `Protocol/HarborEventData.cs` | MessagePack `[Union]` of event wire DTOs (mirror of `HarborEvent`).                                   |
| `Protocol/WireCodec.cs`       | Length-prefixed MessagePack framing + `SerializeDomain<T>` / `DeserializeDomain<T>` helpers.          |

## Wire format

Every `AgentEvent` subtype is classified exactly once in `AgentEventProjector`:
either a handler that emits a `HarborEvent`, or an entry in `NoWireCaseReasons`
with the reason it has no wire case. There is no `_ =>` fallback — a type in
neither table comes back as `Unmapped`, which hosts log and count instead of
dropping silently. `tests/Harbor.Ipc.Tests/AgentEventProjectionCoverageTests`
reflects over the `AgentEvent` and `LlmEvent` unions and fails when a subtype is
unclassified, so adding an event type without deciding its projection is a test
failure, not a silent behaviour change on one host.

Each frame:

```
┌─────────────────┬──────────────────────────────────────┐
│ uint32 BE len   │ MessagePack payload (len bytes)       │
└─────────────────┴──────────────────────────────────────┘
```

Payload is one of:

- `HarborRequest` (client → server)
- `HarborResponse` (server → client), which is one of:
    - `OkResponse` — with optional MessagePack `Payload` bytes (domain object)
    - `ErrorResponse` — with `Message` string
    - `EventEnvelope` — with `EventBytes` (a serialized `HarborEventData` union member)

## Why parallel DTOs?

The rich domain types (`Session`, `AgentMessage`, `ToolDescriptor`, `ModelInfo`, `ProviderId`, `ToolResult`) are `[MemoryPackable]` — not `[MessagePackObject]`. We carry them through the wire as **typed MessagePack `byte[]`** payloads (`OkResponse.Payload`, `EventEnvelope.EventBytes`) — serialized via `WireCodec.SerializeDomain<T>` with the caller supplying the concrete type, so no type metadata travels on the wire. This:

- keeps the wire contract small and explicit (only ~25 DTO records, not 100+);
- decouples the wire from any future MemoryPack/MemoryPackable changes;
- makes non-.NET clients (Python/JS/Rust/Go) trivial — they `msgpack.unpackb(payload)` and get a plain dict.

## See also

- `Harbor.Ipc.InProcess/README.md` — default in-process client.
- `Harbor.Ipc.Server/README.md` — MessagePack RPC server.
- `Harbor.Ipc.Client/README.md` — remote IPC client.
