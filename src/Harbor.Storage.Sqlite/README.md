# Harbor.Storage.Sqlite

SQLite session storage backend. Indexed on `session_id` and `created_at` — efficient queries for session listing, time-range scans, and full-text search over message content.

## Layer

Infrastructure — session/persistence backend. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Data.Sqlite`
- `SQLitePCLRaw.lib.e_sqlite3` (bundled native lib)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `SqliteSessionStore` — implements `ISessionStore` from Harbor.Abstractions
- `SqliteSessionSchema` — DDL constants (creates tables on first use)
- `SqliteSessionQueries` — parameterized SQL for CRUD operations

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ISessionStore, SqliteSessionStore>();
```

The `AgentLoop` resolves `ISessionStore` and calls `AppendAsync` / `LoadAsync` / `CompactAsync` as messages flow.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
