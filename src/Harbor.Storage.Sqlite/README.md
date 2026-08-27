# Harbor.Storage.Sqlite

SQLite session storage backend. `SqliteSessionStore` keeps sessions and messages in indexed tables — efficient session listing, message reads, and stats without a full-file scan. Selected via `HARBOR_STORAGE=sqlite` (`src/Harbor.Hosting/Modules/StorageModule.cs:33`); the database file lives at `<HarborDir>/sessions.db`.

## Layer

Infrastructure — session/persistence backend. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Data.Sqlite`
- `SQLitePCLRaw.lib.e_sqlite3` (bundled native lib)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `SqliteSessionStore(dbPath, logger)` — implements `ISessionStore` from Harbor.Abstractions; schema + queries live inside this single implementation (DDL on first use, parameterized SQL)

## Usage

Registered via DI by the host's `StorageModule`:

```csharp
string sqlitePath = Path.Combine(ctx.Options.HarborDir, "sessions.db");
services.AddSingleton<ISessionStore>(new SqliteSessionStore(sqlitePath, logger));
```

WAL mode is enabled for concurrent readers (`PRAGMA journal_mode = WAL`,
`src/Harbor.Storage.Sqlite/SqliteSessionStore.cs:345`). The agent persists messages through the same `ISessionStore` contract as every other backend.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
