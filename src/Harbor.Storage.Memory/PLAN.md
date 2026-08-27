# Plan — Harbor.Storage.Memory

## Status: Stable

## Done

- [x] Thread-safe backing store
- [x] Full `ISessionStore` contract
- [x] No I/O — fastest backend, ideal for tests and ephemeral desktop sessions (desktop default, `HarborComposeOptions.cs:49`)

## TODO

- [ ] Optional size cap with LRU eviction
- [ ] Snapshot/restore for debugging

## Known issues

- Sessions lost on process exit (by design — use Jsonl or Sqlite for persistence).
