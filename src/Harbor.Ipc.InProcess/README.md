# Harbor.Ipc.InProcess

Default `IHarborClient` implementation. Calls `IAgent` / `ISessionStore` / `IProviderRegistry` / `IToolRegistry` / `IEventBus` directly — **zero serialization, zero network**.

## When to use

- **Default.** This is the implementation used when `HARBOR_MODE=inprocess` (the default).
- Single-process apps where the UI and the agent loop live in the same process.
- Tests that want to exercise the full `IHarborClient` surface without spinning up a pipe server.

## When NOT to use

- UI runs in a different process than the agent loop (e.g. remote TUI over SSH, mobile client talking to a desktop server, web UI talking to a backend).
- UI is written in a non-.NET language (Python/JS/Rust/Go). Use `Harbor.Ipc.Client` (or write your own client using the MessagePack protocol directly — see `docs/IPC.md`).

## Registration

```csharp
// In your composition root (Program.cs / AppHost.cs):
services.UseInProcessHarborClient();
```

The host must also register the application-layer services (`IAgent`, `IAgentRegistry`, `ISessionStore`, `IProviderRegistry`, `IToolRegistry`, `IEventBus`) — these are normally wired by `HostBuilder.Build()` (CLI) or `AppHost.BuildAsync()` (Avalonia).

## Event bridging

`SubscribeToEventsAsync` subscribes to `IEventBus` and projects the rich `AgentEvent` hierarchy down to the wire-stable `HarborEvent` union via a bounded channel (1024 events, drop-oldest on overflow). This is the same projection the IPC server uses, so in-process and IPC clients see identical event streams.

## Files

- `InProcessHarborClient.cs` — the client.
- `InProcessHarborClientExtensions.cs` — `UseInProcessHarborClient()` DI helper.
