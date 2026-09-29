# PLAN.md — Harbor.Ipc.Abstractions

## Done

- ✅ `IHarborClient` interface — 13 methods (agent control, sessions, providers, tools, events, connection)
- ✅ `IHarborServer` interface — `StartAsync` / `StopAsync` / `IsRunning` / `Endpoint`
- ✅ `IPipeTransport` interface — transport abstraction
- ✅ `HarborEvent` — 11-case discriminated union (simplified projection of `AgentEvent`)
- ✅ `AgentEventProjector` — the single `AgentEvent → HarborEvent` projection shared by
      every host, with an explicit per-type decision for each of the 17 `AgentEvent`
      subtypes (10 emitted, 7 documented no-wire-case) and a loud `Unmapped` outcome for
      anything left over (#495)
- ✅ `HarborEventMapping` — bidirectional `HarborEvent ↔ HarborEventData` mapping
- ✅ `HarborRequest` — MessagePack `[Union]` of 15 request types
- ✅ `HarborResponse` — MessagePack `[Union]` of 3 response shapes (Ok, Error, EventEnvelope)
- ✅ `HarborEventData` — MessagePack `[Union]` of 11 event wire DTOs
- ✅ `WireCodec` — length-prefixed framing + `SerializeDomain<T>` / `DeserializeDomain<T>` helpers
- ✅ `SubscriptionAck` + session-scoped MessagePack formatters (`Protocol/SubscriptionAck.cs`, `Protocol/SessionMessagePackFormatters.cs`)
- ✅ README.md

## Future

- Add TLS transport variant — see the transport section in `Harbor.Ipc.Client/README.md`.
- Add WebSocket transport (`WebSocketPipeTransport`) for browser-based clients.
- Migrate from typed MessagePack to a hand-rolled formatter for the
  domain types — would let us drop the MessagePack runtime dependency on
  the IPC client and ship a smaller client binary.
- Add per-method throttling / rate-limiting to the server's `RequestDispatcher`.
