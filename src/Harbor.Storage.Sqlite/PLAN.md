# Plan — Harbor.Storage.Sqlite

## Status: Stable

## Done

- [x] SQLite schema with indexes (session id, timestamps)
- [x] Full `ISessionStore` contract
- [x] Bundled e_sqlite3 native lib (no system SQLite dependency)
- [x] WAL journal mode enabled for concurrent readers (`SqliteSessionStore.cs:345`)
- [x] Behind `HarborWithAllProviders` build flavor in the CLI csproj alongside extra providers (`apps/Harbor.App.Cli/Harbor.App.Cli.csproj:205-211`)

## TODO

- [ ] FTS5 virtual table for full-text search over message content
- [ ] Vacuum / cleanup on session delete
- [ ] Reader concurrency stress tests under high write load

## Known issues

- No FTS yet — content search requires LIKE scan.
- Single-writer serialization (SQLite default; WAL helps readers only).
