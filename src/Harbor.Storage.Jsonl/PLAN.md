# Plan — Harbor.Storage.Jsonl

## Status: Stable

## Done

- [x] Append-only JSONL writer (one message per line)
- [x] Full `ISessionStore` contract (create/get/list/append/update/delete/stats)
- [x] AOT-compatible codec: `JsonlCodecContext : JsonSerializerContext` source-gen, no reflection serialization (`JsonlCodecContext.cs`)
- [x] Wired as CLI default backend via `StorageModule` (`HARBOR_STORAGE=jsonl`)
- [x] Round-trip equality tests (see `tests/Harbor.Storage.*.Tests`)

## TODO

- [ ] Atomic file replace on compaction (crash mid-compact risk)
- [ ] Per-session size cap with auto-compact threshold
- [ ] Async stream-flush tuning
- [ ] Light index / footer to avoid full-file scan on load

## Known issues

- Compaction rewrites are not atomic; a crash mid-rewrite can corrupt the session file.
- No indexing — loading a session requires full-file scan.

## Next priorities

1. **P1**: Atomic compaction (temp + rename)
2. **P2**: Snapshot + WAL-style recovery for long-running sessions
