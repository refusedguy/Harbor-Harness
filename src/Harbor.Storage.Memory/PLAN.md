# Plan — Harbor.Storage.Memory

## Status: Stable

## Done

- [x] Thread-safe ConcurrentDictionary backing store
- [x] Full ISessionStore contract
- [x] No I/O — fastest backend, ideal for tests

## TODO

- [ ] Optional size cap with LRU eviction
- [ ] Snapshot/restore for debugging

## Known issues

- Sessions lost on process exit (by design — use Jsonl or Sqlite for persistence).

## Next priorities

1. **P1**: Concurrency stress tests under high write load
2. **P2**: Snapshot + WAL for crash recovery (long-running sessions)
