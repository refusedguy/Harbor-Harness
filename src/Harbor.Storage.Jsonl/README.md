# Harbor.Storage.Jsonl

JSONL (newline-delimited JSON) session storage backend. Each session is a single `.jsonl` file under `~/.harbor/sessions/` — append-only writes, compaction rewrites the file in place. Default backend for the CLI.

## Layer

Infrastructure — session/persistence backend. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `JsonlSessionStore` — implements `ISessionStore` from Harbor.Abstractions
- `JsonlSessionFormat` — low-level line serialization helpers
- `JsonlCompactor` — anchored-summary compactor that folds old turns into a single summary line

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ISessionStore, JsonlSessionStore>();
```

The `AgentLoop` resolves `ISessionStore` and calls `AppendAsync` / `LoadAsync` / `CompactAsync` as messages flow.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
