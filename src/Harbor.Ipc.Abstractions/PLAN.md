# PLAN.md — Harbor.Ipc.Abstractions

## Done

- ✅ `IHarborClient` interface — 13 methods (agent control, sessions, providers, tools, events, connection)
- ✅ `IHarborServer` interface — `StartAsync` / `StopAsync` / `IsRunning` / `Endpoint`
- ✅ `IPipeTransport` interface — transport abstraction
- ✅ `HarborEvent` — 11-case discriminated union (simplified projection of `AgentEvent`)
- ✅ `HarborEventMapping` — bidirectional `HarborEvent ↔ HarborEventData` mapping
- ✅ `HarborRequest` — MessagePack `[Union]` of 14 request types
- ✅ `HarborResponse` — MessagePack `[Union]` of 3 response shapes (Ok, Error, EventEnvelope)
- ✅ `HarborEventData` — MessagePack `[Union]` of 11 event wire DTOs
- ✅ `WireCodec` — length-prefixed framing + `SerializeDomain<T>` / `DeserializeDomain<T>` helpers
- ✅ README.md

## Future

- Add TLS transport variant (`TlsPipeTransport`) — see `docs/IPC.md` §Security.
- Add WebSocket transport (`WebSocketPipeTransport`) for browser-based clients.
- Migrate from typed MessagePack to a hand-rolled formatter for the
  domain types — would let us drop the MessagePack runtime dependency on
  the IPC client and ship a smaller client binary.
- Add per-method throttling / rate-limiting to the server's `RequestDispatcher`.
