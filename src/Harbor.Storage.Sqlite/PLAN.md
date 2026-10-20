# Plan — Harbor.Storage.Sqlite

## Status: Stable

## Done

- [x] SQLite schema with indexes (session id, timestamps)
- [x] Full `ISessionStore` contract
- [x] Bundled e_sqlite3 native lib (no system SQLite dependency)
- [x] WAL journal mode enabled for concurrent readers (`SqliteSessionStore.cs:345`)
- [x] Behind `HarborWithAllProviders` build flavor in the CLI csproj alongside extra providers (`apps/Harbor.App.Cli/Harbor.App.Cli.csproj:205-211`)
- [x] FTS5 index over the searchable text of the message log, kept in sync by
      triggers on the message table and ranked with BM25 (#1743,
      `SqliteSessionQueryIndex.cs`); the backend probes the bundled SQLite for
      FTS5 and falls back to a documented `LIKE` scan when it is absent
- [x] `ISessionQuery` implementation: filters, a time window, a limit and a
      ranking pushed into SQL (`SqliteSessionQuery.cs`)

## TODO

- [ ] Vacuum / cleanup on session delete
- [ ] Reader concurrency stress tests under high write load
- [ ] Persist the agent event stream, so the `Event` record kind and the
      event-type filter have rows to select (#1743, next slice)
- [ ] A JSONL implementation of `ISessionQuery` — the contract is
      backend-neutral, but the JSONL backend has no index to search

## Known issues

- The full-text path matches TOKENS, the literal path matches SUBSTRINGS; the
  result reports which path ran (`SessionQueryResult.Mode`).
- The FTS5 index is created on the first text query, and the rows written
  before it existed are backfilled then.
- Single-writer serialization (SQLite default; WAL helps readers only).
