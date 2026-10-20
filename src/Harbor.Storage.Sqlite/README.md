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
- `SqliteSessionQuery(dbPath, logger)` — implements `ISessionQuery` from Harbor.Abstractions: the read side a search needs (filters, a time window, a limit, a ranking)

## Session query (#1743)

`SqliteSessionQuery` is the query surface over the session log — the read side
the store does not cover. `ISessionStore` answers EXACT reads (one session, one
message list); a search is not an exact read, and answering it through the
store meant reading every message of every session.

```csharp
var query = new SqliteSessionQuery(sqlitePath, logger);
var result = await query.QueryAsync(new SessionQueryOptions
{
    Text = "brown fox",                      // the phrase to find
    Roles = ["user", "assistant"],          // AND across clauses, OR inside one
    Window = new SessionQueryWindow(from, to),
    Limit = 20,
    Rank = SessionQueryRank.Relevance,
});
```

- **Filters reach SQL.** The session-id, role and window clauses are pushed
  into the statement, so a query reads the rows it needs instead of the whole
  log.
- **Two text paths, and the result says which ran.** The full-text path is an
  FTS5 `MATCH` over the index the query service keeps (ranked by BM25, token
  semantics — the query is a phrase of tokens); the literal path is a `LIKE`
  scan over the same projection (substring semantics, ranked by the position of
  the first match). `SessionQueryResult.Mode` reports the one that ran, so a
  caller that needs substring semantics cannot mistake a token match for one.
- **FTS5 availability is probed, not assumed.** Whether FTS5 exists is a
  property of the bundled SQLite build; the backend creates a throwaway
  virtual table to find out and falls back to the literal path when it is
  absent.
- **The index is kept honest by triggers.** Three triggers on the message
  table maintain the index, so it cannot drift from the rows it projects; a
  one-shot backfill covers the rows written before the index existed. The
  index is only built when a text query needs it — a filter-only query never
  turns a read into a write.
- **The event kind is not served yet.** No backend persists agent events, so
  `SessionRecordKind.Event` is not in `SupportedKinds`: a kind that is missing
  there was NOT searched, which is not the same as "nothing matched".

## Usage

Registered via DI by the host's `StorageModule`:

```csharp
string sqlitePath = Path.Combine(ctx.Options.HarborDir, "sessions.db");
services.AddSingleton<ISessionStore>(new SqliteSessionStore(sqlitePath, logger));
```

WAL mode is enabled for concurrent readers (`PRAGMA journal_mode = WAL`,
`src/Harbor.Storage.Sqlite/SqliteSessionStore.cs:646`). The agent persists messages through the same `ISessionStore` contract as every other backend.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
