# Plan — Harbor.Storage.Sqlite

## Status: MVP

## Done

- [x] SQLite schema with indexes on session_id + created_at
- [x] Full ISessionStore contract
- [x] Bundled e_sqlite3 native lib (no system SQLite dependency)

## TODO

- [ ] FTS5 virtual table for full-text search over message content
- [ ] Vacuum / cleanup on session delete
- [ ] WAL mode for concurrent readers

## Known issues

- No FTS yet — search requires LIKE scan.
- Single-writer serialization (SQLite default).

## Next priorities

1. **P1**: Concurrency stress tests under high write load
2. **P2**: Snapshot + WAL for crash recovery (long-running sessions)
