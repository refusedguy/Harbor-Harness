# Harbor.Storage.Memory

In-memory session storage backend — sessions live in dictionaries for the lifetime of the process. Used by tests and ephemeral desktop GUI sessions (no persistence across restarts); it is the default backend for desktop apps (`HarborComposeOptions.cs:49`: CLI default `jsonl`, desktop default `memory`).

## Layer

Infrastructure — session/persistence backend. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)

## Public API

- `MemorySessionStore` — thread-safe in-memory implementation of `ISessionStore`

## Usage

Selected via `HARBOR_STORAGE=memory` or config; registered by the host's `StorageModule`:

```csharp
"memory" => new MemorySessionStore()
```

(`src/Harbor.Hosting/Modules/StorageModule.cs:31`). The agent persists messages through the same `ISessionStore` contract as every other backend (`AppendMessageAsync`, `GetMessagesAsync`, …).

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
