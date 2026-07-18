# Plan — Harbor.Storage.Jsonl

## Status: Stable

## Done

- [x] Append-only JSONL writer (one event per line)
- [x] Concurrent reader/writer with file lock
- [x] Compaction (anchored-summary fold)
- [x] Round-trip equality tests

## TODO

- [ ] Atomic file replace on compaction (currently in-place rewrite, fsync gap)
- [ ] Per-session size cap with auto-compact threshold
- [ ] Async stream-flush tuning

## Known issues

- Compaction is not atomic; a crash mid-compact can corrupt the session file.
- No indexing — loading a session requires full-file scan.

## Next priorities

1. **P1**: Concurrency stress tests under high write load
2. **P2**: Snapshot + WAL for crash recovery (long-running sessions)
