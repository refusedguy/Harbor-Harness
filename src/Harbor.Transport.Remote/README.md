# Harbor.Transport.Remote

Remote UI transport layer for Harbor — PSK-authenticated gateway and client that serialize `AgentEvent`s over HTTP/SSE so a desktop GUI can control a headless CLI/TUI session.

## Layer

**Infrastructure (remote transport).** Bridges the gap between a desktop GUI process and a headless Harbor session. Depends on `Harbor.Abstractions` and ASP.NET Core.

## What's in it

| File | Purpose |
|------|---------|
| `RemoteGateway.cs` | `RemoteGateway` — ASP.NET Core minimal API that hosts the event stream endpoint and accepts PSK-authenticated connections. |
| `RemoteClient.cs` | `RemoteClient` — connects to a gateway, sends `UiTransportPacket`s, receives the event stream. |
| `UiTransportPacket.cs` | `UiTransportPacket` record wrapping event type, payload, and timestamp; `FromEvent(AgentEvent)` factory. |
| `PsAuthHandler.cs` | `PsAuthHandler` — pre-shared key validation middleware/handler for gateway authentication. |

## Public API summary

- **`RemoteGateway`**: `StartAsync(port, ct)` / `StopAsync(ct)` — hosts the remote transport server.
- **`RemoteClient`**: `ConnectAsync(uri, ct)`, `SendAsync(packet, ct)`, implements `IAsyncDisposable`.
- **`UiTransportPacket`**: `Type`, `Event`, `Timestamp`; `FromEvent` factory.
- **`PsAuthHandler`**: `GeneratePsk()`, `Validate(provided, expected)`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.AspNetCore.App` (FrameworkReference) | HTTP server/client primitives |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | `AgentEvent`, `KeyPress` |

## Tests

`tests/Harbor.Transport.Remote.Tests/` — covers gateway lifecycle, client connectivity, and PSK auth.

## Build

```bash
dotnet build src/Harbor.Transport.Remote/Harbor.Transport.Remote.csproj
```

## Known limitations

- PSK authentication is pre-shared key only — no TLS, no OAuth, no token refresh.
- Single event stream per connection; no multiplexing.
- Gateway is not production-hardened (no rate limiting, no connection pooling beyond ASP.NET defaults).
