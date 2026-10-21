# Harbor.Extensions — PLAN

## Status

**Stable.** Extracted from `Harbor.Abstractions` in round 6 (Task A1); decoupled to zero Harbor project references in F1. Namespaces preserved — zero consumer code changes.

## Done

- [x] Split `Harbor.Abstractions` god-project: extension helpers moved to `Harbor.Extensions`
- [x] F1 decoupling: **zero Harbor project references** — helpers are pure BCL/NuGet wrappers; zero `Microsoft.Extensions.*`, zero `Harbor.Abstractions` / `Harbor.Abstractions.Contracts` / `Harbor.Core` / `Harbor.Application` / `Harbor.Registries` / sibling-Infrastructure / Presentation refs
- [x] Namespace preserved (`Harbor.Abstractions.Extensions`) — consumers' `using` lines unchanged
- [x] Architecture test `Extensions_HasZeroHarborProjectReferences` added in `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs:196`
- [x] **#1239 — `DictionaryPool<TKey, TValue>`** landed in `ArrayPoolExtensions.cs` next to `StringBuilderPool`: one `ConcurrentBag<Dictionary<TKey, TValue>>` per power-of-two entry band (8 ... 1024 entries, <=16 dictionaries per band), cleared on rent, dropped on return past the largest band. Comparers are deliberately not pooled — an instance is only recycled into a renter that wants `EqualityComparer<TKey>.Default`, so a custom comparer can never leak into a comparer-agnostic rent.
- [x] **#1239 — `StringBuilderPool` v2, size-bucketed.** The single global bag became `ConcurrentBag<StringBuilder>[]` indexed by `ceil(log2(capacity))` (11 buckets, 16 ... 16384 chars, <=16 retained per bucket). Fixes the Known issue below. Still hand-written: no `Microsoft.Extensions.ObjectPool` package on a project the compiled `check-abstractions-contract` gate guards.
- [x] **#1239 — pool benchmarks.** `StringBuilderPoolBenchmark` gained the ~10 KiB system-prompt rows (`Prompt_Pooled_10KB` vs `Prompt_New_10KB`) and a new `DictionaryPoolBenchmark` class landed in `tests/Harbor.Benchmarks/PoolingAndFormattingBenchmark.cs`. **Numbers are not recorded yet** — see `docs/BENCHMARKS.md` §5.8 for why (no run has happened on a branch this change reaches, and no figure may be quoted without one). The invariants are pinned by unit tests instead.

## TODO

### P1 — broader helper coverage

- [ ] Add `SpanList<T>` — a struct wrapping a `Span<T>` + `int count` for stack-allocated list-style building (avoid `List<T>` heap allocation when the upper bound is known). Useful in `PermissionRuleset.Evaluate` (which currently uses `Dictionary<string, PermissionRule>` for merge) and in `AgentLoop` (which builds per-turn tool-result lists).
- [ ] Add `StringPool` (interned strings) — for high-cardinality repeated strings like tool names, model ids, agent names. Cuts long-term memory footprint for long-running sessions.
- [ ] Add `MemoryPackStreamExtensions.SerializeAsync<T>(Stream, T)` / `DeserializeAsync<T>(Stream)` — non-blocking async serialize/deserialize for large payloads (e.g. session snapshots) where the synchronous `MemoryPackSerializer.Serialize` would block on a large allocation.

### P2 — ergonomics

- [ ] `RentedArray<T>` currently exposes `.Array` (the full rented array, possibly larger than requested), `.Span` (length = `Length`), and `.Length`. Add a `.Slice(int, int)` returning a `Span<T>` view of a sub-range — useful for chunked writes (e.g. streaming a large tool output into a rented buffer).
- [ ] `ToFrozenSet<T>` and `ToFrozenDictionary` should accept a `Func<T, T>` normalize step (e.g. lowercase string keys) before freezing — currently callers must pre-normalize the input sequence.

### P2 — performance validation

- [ ] **Still open:** run the #1239 pool benchmarks and record the numbers. The rows exist (`Prompt_Pooled_10KB` vs `Prompt_New_10KB`, plus the new `DictionaryPoolBenchmark`), but no run has happened on any branch this change reaches — the CI `benchmark` workflow is push-to-`dev`-only, cooldown-gated, and its curated filter matches neither class. `docs/BENCHMARKS.md` §5.8 stays `⏳ not yet measured` until it does. The question it answers is unchanged: the pool pays off only if `Rent` + `Dispose` is faster than `new` + GC, and for short-lived builders the `ConcurrentBag` traffic may dominate — if the 1 KB rows lose to the raw baseline, deprecate rather than defend the pool.
- [ ] Wire `DictionaryPool<TKey, TValue>` into the per-call argument dictionaries (message converters, tool dispatcher). The pool ships unused on purpose: a hot-path call-site change belongs behind its own allocation tripwire, not inside the pool slice.
- [ ] Benchmark `FrozenDictionary<string, T>` vs `Dictionary<string, T>` for the post-`Freeze()` tool registry lookup path. Frozen's JIT specialization should win for `string` keys, but the win may be smaller than expected for ≤50 tools (the typical builtin set).

## Known issues

- **`RentedArray<T>` is a `ref struct`** — it cannot be stored in fields of non-`ref` classes or passed across `await` boundaries. This is intentional (prevents the most common misuse) but occasionally forces call sites to copy data out of the rented array before awaiting. Document this in the XML doc on `RentScoped`.
- ~~**`StringBuilderPool` is global.**~~ **Fixed in #1239.** The single bag is gone; see the Done entry. The residual property worth knowing is the bucketing trade-off itself: a rent is served *only* from its own band, so a 10 KiB rent takes a 16 KiB builder (the next band up) and a cold band allocates a fresh right-sized builder rather than raiding a larger neighbour. That is the deliberate choice — hitting a bigger band would reintroduce exactly the mixed-workload bloat the bucketing removes.
- **`MemoryPackExtensions.FromMemoryPackBytes<T>` returns `T?`** — the nullable return is correct (deserialization can fail) but the call site reads awkwardly (`session = bytes.FromMemoryPackBytes<Session>() ?? Session.Empty`). A `Result<T>` return would be more idiomatic for the codebase.

## Next priorities

1. **P2 — Run the #1239 pool benchmarks** (1 hour, plus a 30-minute BDN run on a quiet box): the bench is written, the numbers are not. It validates that the pool is actually a win — if it isn't, deprecate it. See `docs/BENCHMARKS.md` §5.8.
2. **P1 — Wire `DictionaryPool` into a hot path** (2 hours, with an allocation tripwire in the same diff).
3. **P1 — `SpanList<T>`** and **`StringPool`** still unwritten (see TODO).
