# Harbor.Extensions — PLAN

## Status

**Stable.** Extracted from `Harbor.Abstractions` in round 6 (Task A1); decoupled to zero Harbor project references in F1. Namespaces preserved — zero consumer code changes.

## Done

- [x] Split `Harbor.Abstractions` god-project: extension helpers moved to `Harbor.Extensions`
- [x] F1 decoupling: **zero Harbor project references** — helpers are pure BCL/NuGet wrappers; zero `Microsoft.Extensions.*`, zero `Harbor.Abstractions` / `Harbor.Abstractions.Contracts` / `Harbor.Core` / `Harbor.Application` / `Harbor.Registries` / sibling-Infrastructure / Presentation refs
- [x] Namespace preserved (`Harbor.Abstractions.Extensions`) — consumers' `using` lines unchanged
- [x] Architecture test `Extensions_HasZeroHarborProjectReferences` added in `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs:196`

## TODO

### P1 — broader helper coverage

- [ ] Add `DictionaryPool<TKey, TValue>` — counterpart to `StringBuilderPool` for `Dictionary` instances that are constructed on hot paths (e.g. message converters that build per-call arg dictionaries). Use `ConcurrentBag<Dictionary<TKey, TValue>>` with a `MaxRetainCount` to bound memory.
- [ ] Add `SpanList<T>` — a struct wrapping a `Span<T>` + `int count` for stack-allocated list-style building (avoid `List<T>` heap allocation when the upper bound is known). Useful in `PermissionRuleset.Evaluate` (which currently uses `Dictionary<string, PermissionRule>` for merge) and in `AgentLoop` (which builds per-turn tool-result lists).
- [ ] Add `StringPool` (interned strings) — for high-cardinality repeated strings like tool names, model ids, agent names. Cuts long-term memory footprint for long-running sessions.
- [ ] Add `MemoryPackStreamExtensions.SerializeAsync<T>(Stream, T)` / `DeserializeAsync<T>(Stream)` — non-blocking async serialize/deserialize for large payloads (e.g. session snapshots) where the synchronous `MemoryPackSerializer.Serialize` would block on a large allocation.

### P2 — ergonomics

- [ ] `RentedArray<T>` currently exposes `.Array` (the full rented array, possibly larger than requested), `.Span` (length = `Length`), and `.Length`. Add a `.Slice(int, int)` returning a `Span<T>` view of a sub-range — useful for chunked writes (e.g. streaming a large tool output into a rented buffer).
- [ ] `StringBuilderPool.Rent(int capacity)` should round `capacity` up to the next power of two (or at least to a multiple of 256) to reduce the number of distinct capacity buckets in the `ConcurrentBag`. Currently each rent either reuses the first available builder (regardless of capacity mismatch) or allocates a new one — rounding would improve hit rate.
- [ ] `ToFrozenSet<T>` and `ToFrozenDictionary` should accept a `Func<T, T>` normalize step (e.g. lowercase string keys) before freezing — currently callers must pre-normalize the input sequence.

### P2 — performance validation

- [ ] Benchmark `StringBuilderPool` vs `new StringBuilder()` for the actual system-prompt assembly path (system prompt is ~10 KiB on a typical turn). The pool pays off only if `Rent` + `Dispose` is faster than `new` + GC; for short-lived builders the pool's `ConcurrentBag` contention may dominate. `tests/Harbor.Benchmarks/StringBuilderPoolBenchmark.cs` is a stub — implement and run.
- [ ] Benchmark `FrozenDictionary<string, T>` vs `Dictionary<string, T>` for the post-`Freeze()` tool registry lookup path. Frozen's JIT specialization should win for `string` keys, but the win may be smaller than expected for ≤50 tools (the typical builtin set).

## Known issues

- **`RentedArray<T>` is a `ref struct`** — it cannot be stored in fields of non-`ref` classes or passed across `await` boundaries. This is intentional (prevents the most common misuse) but occasionally forces call sites to copy data out of the rented array before awaiting. Document this in the XML doc on `RentScoped`.
- **`StringBuilderPool` is global.** All callers share the same `ConcurrentBag`. If one caller rents a builder and lets it grow to 16 KiB (the `MaxRetainCapacity`), every other caller's `Rent` will get a 16 KiB builder even if they only need 256 bytes. Not a correctness bug — just a memory-efficiency bug for mixed workloads. A size-bucketed pool (`ConcurrentBag<StringBuilder>[]` indexed by `ceil(log2(capacity))`) would fix this.
- **`MemoryPackExtensions.FromMemoryPackBytes<T>` returns `T?`** — the nullable return is correct (deserialization can fail) but the call site reads awkwardly (`session = bytes.FromMemoryPackBytes<Session>() ?? Session.Empty`). A `Result<T>` return would be more idiomatic for the codebase.

## Next priorities

1. **P1 — `DictionaryPool<TKey, TValue>`** (2 hours, fills an obvious gap next to `StringBuilderPool`).
2. **P2 — `StringBuilderPoolBenchmark`** (1 hour, validates that the pool is actually a win — if it isn't, deprecate it).
3. **P2 — Size-bucketed `StringBuilderPool`** (3 hours, only if the benchmark shows the global pool is a memory hog under mixed workloads).
