# PLAN.md — Harbor.Ipc.Server

## Done

- ✅ `HarborIpcServer` — `IHarborServer` impl composing transport + dispatcher + broadcaster
- ✅ `ServerPipeTransport` — Named Pipe (Windows) / Unix Domain Socket (Linux/Mac) accept loop
- ✅ `MessagePackRpcServer` — per-client request loop, concurrent multi-client
- ✅ `RequestDispatcher` — dispatches all 14 HarborRequest types via host's DI
- ✅ `EventBroadcaster` — `IEventBus` → all connected client streams, dead-client isolation
- ✅ `UseHarborIpcServer()` DI helper
- ✅ README.md

## Future

- Add `Microsoft.Extensions.Hosting.IHostedService` wrapper so the server auto-starts with the host.
- Add per-request throttling (e.g. max 100 requests/sec per client).
- Add a "graceful drain" mode: stop accepting new clients, wait for in-flight requests to complete, then close.
- Pluggable auth: per-client token validated before any non-`Connect` request.
- TLS transport variant.
