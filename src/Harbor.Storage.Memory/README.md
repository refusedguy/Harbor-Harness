# Harbor.Storage.Memory

In-memory session storage backend — sessions live in a `ConcurrentDictionary` for the lifetime of the process. Used by tests and ephemeral desktop GUI sessions (no persistence across restarts).

## Layer

Infrastructure — session/persistence backend. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)

## Public API

- `InMemorySessionStore` — implements `ISessionStore` from Harbor.Abstractions
- `InMemorySessionStore` — thread-safe in-memory implementation

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ISessionStore, InMemorySessionStore>();
```

The `AgentLoop` resolves `ISessionStore` and calls `AppendAsync` / `LoadAsync` / `CompactAsync` as messages flow.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
