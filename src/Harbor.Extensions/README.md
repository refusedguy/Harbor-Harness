# Harbor.Extensions

> Infrastructure helpers — ArrayPool, StringBuilder pool, FrozenSet materializers,
> MemoryPack round-trip helpers, Ensure argument guards. The single "infrastructure-as-helper" layer
> that sits between the pure domain models and the interface contracts.

## What's inside

| File                      | Contents                                                                                                                                                                                                                                                                                                         |
|---------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `ArrayPoolExtensions.cs`  | `ArrayPoolExtensions.RentScoped<T>(this ArrayPool<T>, int)` returning a disposable `RentedArray<T>` ref-struct (auto-returns on `Dispose`); `StringBuilderPool` static class with a **size-bucketed** `ConcurrentBag<StringBuilder>[]` backing (#1239 — one bag per `ceil(log2(capacity))`, 16 … 16 KiB, `MaxRetainedPerBucket` cap, `MaxRetainCapacity = 16 KiB` LOH-avoidance); `DictionaryPool<TKey, TValue>` + `PooledDictionary<TKey, TValue>` (same shape over entry bands 8 … 1024, comparers never pooled); the `PooledStringBuilder` readonly struct |
| `CollectionExtensions.cs` | `ToFrozenSet<T>`, `ToFrozenDictionary<...>` (3 overloads), `AsReadOnlyCollection<T>`, `AsReadOnlyList<T>`, `AddRange<T>(List<T>, ReadOnlySpan<T>)` — Frozen-collection materializers and read-only-collection upcasters for hot-path public APIs                                                                 |
| `MemoryPackExtensions.cs` | `ToMemoryPackBytes<T>(this T)` and `FromMemoryPackBytes<T>(this byte[])` — extension-method sugar over `MemoryPackSerializer.Serialize<T>` / `Deserialize<T>` for `IMemoryPackable<T>` types                                                                                                                     |
| `Ensure.cs` | `Ensure.NotNull<T>` / `NotBlank` / `NotEmpty` — thin behavior-identical wrappers over `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` / `ThrowIfNullOrEmpty` (#1553) |

## Layer

**Domain (helper).** The matrix places it in **Domain**, not Infrastructure — zero Harbor references, no HTTP clients, no DI registration, no filesystem code, and a Domain row may reference Domain only. Just the small set of pooling / materializer helpers that other infrastructure projects (`Harbor.Storage.*`, `Harbor.Providers.*`, `Harbor.Tools.*`) and the application layer (`Harbor.Application`) need to keep allocations low on hot paths.

## Dependencies

| Dependency                         | Why                                                                                                                                                                                                                                                                                   |
|------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `MemoryPack`                       | `MemoryPackSerializer.Serialize<T>` / `Deserialize<T>` — the `IMemoryPackable<T>` constraint and the serializer entry-point. `MemoryPackExtensions` is generic over `IMemoryPackable<T>`, so **no** concrete Harbor assembly is required.                                              |

`System.Buffers` (`ArrayPool<T>`) is in-box for `net10.0` — no package ref needed.

`System.Collections.Frozen` (`FrozenSet<T>` / `FrozenDictionary<K,V>`, used by
`CollectionExtensions`) is in-box too, since .NET 8 — no package ref needed.
`CommunityToolkit.HighPerformance` used to be listed here for exactly those
materializers and was removed: the compiled `check-abstractions-contract` gate measured
that no `.cs` file in this project had a `using` under any of its namespace
prefixes, so the dependency was declared on the strength of a need the framework
already met. The same reasoning removes it from `Harbor.Application`. The
`StringBuilderPool` in `ArrayPoolExtensions.cs` is likewise this solution's own
pool, not CommunityToolkit's namesake type — and it stays that way: #1239 rewrote it
size-bucketed and added the `DictionaryPool` counterpart without reaching for
`Microsoft.Extensions.ObjectPool`, because this project is one of the four
the compiled `check-abstractions-contract` gate guards and a dependency declared here is one
every consumer inherits.

**Zero Harbor project references** (F1 decoupling): no ref to `Harbor.Abstractions.Contracts`, no ref to the `Harbor.Abstractions` facade, no refs to `Harbor.Core` / `Harbor.Application` / `Harbor.Registries`, no refs to Infrastructure siblings (`Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tools.*`), no refs to Presentation (`Harbor.Tui.*`, `Harbor.Desktop.*`). This invariant is enforced by the test `Extensions_HasZeroHarborProjectReferences` in [`tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs`](../../tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs). Consumers that use these helpers reference `Harbor.Extensions` directly — it is **not** transitively re-exported by the facade.

## Namespaces preserved

All files in `Harbor.Extensions` keep their original `Harbor.Abstractions.Extensions` namespace:

| File                      | Namespace                        |
|---------------------------|----------------------------------|
| `ArrayPoolExtensions.cs`  | `Harbor.Abstractions.Extensions` |
| `CollectionExtensions.cs` | `Harbor.Abstractions.Extensions` |
| `MemoryPackExtensions.cs` | `Harbor.Abstractions.Extensions` |

This means **zero consumer code changes** — every project that does `using Harbor.Abstractions.Extensions;` keeps compiling as long as it adds its own direct reference to `Harbor.Extensions`.

## Public API surface (most-used types)

```csharp
// ArrayPool rental with auto-return
using RentedArray<byte> rented = ArrayPool.Shared.RentScoped(minimumLength: 4096);
rented.Span[0] = 42;
// ... use rented.Array / rented.Span / rented.ReadOnlySpan ...
// Dispose() returns the array to the pool automatically.

// StringBuilder pool (size-bucketed: a 256-char rent gets a 256-char builder,
// never the 16 KiB one a neighbour left behind)
using PooledStringBuilder sb = StringBuilderPool.Rent(capacity: 1024);
sb.Builder.Append("hello ").Append("world");
string result = sb.ToString();   // "hello world"
// Dispose() returns the builder to its band (unless it grew past 16 KiB).

// Dictionary pool — the counterpart for per-call dictionaries
using PooledDictionary<string, int> args = DictionaryPool<string, int>.Rent(8);
args.Dictionary["limit"] = 10;
// Dispose() returns it to its entry band (> 1024 entries, or a non-default
// comparer, is dropped instead of retained).

// Frozen materializers (build-once, read-many)
FrozenSet<string> allowedTools = toolNames.ToFrozenSet(StringComparer.Ordinal);
FrozenDictionary<string, ToolDescriptor> byName =
    tools.ToFrozenDictionary(t => t.Name.Value, t => t);

// MemoryPack round-trip
byte[] bytes = session.ToMemoryPackBytes();
Session restored = bytes.FromMemoryPackBytes<Session>();
```

## Design principles

1. **Pooling over allocating.** `ArrayPool<T>.Shared`, `StringBuilderPool` and `DictionaryPool<TKey, TValue>` cover the hottest allocation patterns in the agent loop: per-turn LLM-streaming buffers (rented byte arrays), per-turn system-prompt assembly (rented StringBuilders) and per-call argument maps (rented dictionaries). All three auto-return on `Dispose`.
2. **Ref-struct disposable for rented arrays.** `RentedArray<T>` is a `readonly ref struct` — the compiler prevents it from being captured into closures or async state machines, eliminating the most common misuse of pooled buffers (accidental lifetime extension past `Dispose`).
3. **LOH-avoidance and bounded retention for every pool.** `MaxRetainCapacity = 16 KiB` for builders and `MaxRetainEntries = 1024` for dictionaries mean anything that outgrew the small-object-heap boundary is dropped instead of returned — the pool never becomes a long-term LOH leak. Since #1239 both pools are **banded** (one `ConcurrentBag` per power-of-two size, ≤16 retained per band), so retention is bounded by the band count rather than by whatever callers happened to leave behind.
4. **Frozen over Immutable*.** `FrozenSet<T>` / `FrozenDictionary<TKey, TValue>` are JIT-specialized for read-only lookups (the JIT generates a separate specialized lookup path per `TKey` for `string` / `int` / `ValueTuple` etc.). For build-once / read-many patterns (tool registry, provider registry, agent registry after `Freeze()`), Frozen wins over `ImmutableHashSet<T>` by 2-4× on lookup.
5. **Extension-method sugar over direct API.** `ToMemoryPackBytes<T>(this T)` and `FromMemoryPackBytes<T>(this byte[])` exist purely so call sites read naturally (`session.ToMemoryPackBytes()` instead of `MemoryPackSerializer.Serialize(session)`). No behavior change.

## See also

- [../Harbor.Abstractions.Contracts/](../Harbor.Abstractions.Contracts/) — namespace-preserved model types ([MemoryPackable]) referenced by the generic MemoryPack helpers
- [../Harbor.Abstractions/README.md](../Harbor.Abstractions/README.md) — the interface facade (deliberately does not re-export this layer)
- `docs/ARCHITECTURE_LAYERS.md` — full layering matrix
- [`tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs`](../../tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs) (`Extensions_HasZeroHarborProjectReferences`) — enforces this layer's invariants
