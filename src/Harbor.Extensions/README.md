# Harbor.Extensions

> Infrastructure helpers — ArrayPool, StringBuilder pool, FrozenSet materializers,
> MemoryPack round-trip helpers. The single "infrastructure-as-helper" layer
> that sits between the pure domain models and the interface contracts.

## What's inside

| File                      | Contents                                                                                                                                                                                                                                                                                                         |
|---------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `ArrayPoolExtensions.cs`  | `ArrayPoolExtensions.RentScoped<T>(this ArrayPool<T>, int)` returning a disposable `RentedArray<T>` ref-struct (auto-returns on `Dispose`); `StringBuilderPool` static class with a `ConcurrentBag<StringBuilder>` backing and `MaxRetainCapacity = 16 KiB` LOH-avoidance; `PooledStringBuilder` readonly struct |
| `CollectionExtensions.cs` | `ToFrozenSet<T>`, `ToFrozenDictionary<...>` (3 overloads), `AsReadOnlyCollection<T>`, `AsReadOnlyList<T>`, `AddRange<T>(List<T>, ReadOnlySpan<T>)` — Frozen-collection materializers and read-only-collection upcasters for hot-path public APIs                                                                 |
| `MemoryPackExtensions.cs` | `ToMemoryPackBytes<T>(this T)` and `FromMemoryPackBytes<T>(this byte[])` — extension-method sugar over `MemoryPackSerializer.Serialize<T>` / `Deserialize<T>` for `IMemoryPackable<T>` types                                                                                                                     |

## Layer

**Infrastructure (helper).** Not pure infrastructure (no HTTP clients, no DI registration, no filesystem code) — just the small set of pooling / materializer helpers that other infrastructure projects (`Harbor.Storage.*`, `Harbor.Providers.*`, `Harbor.Tools.*`) and the application layer (`Harbor.Application`) need to keep allocations low on hot paths.

## Dependencies

| Dependency                         | Why                                                                                                                                                                                                                                                                                   |
|------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Harbor.Domain` (project)          | `MemoryPackExtensions.ToMemoryPackBytes<T>` is constrained to `IMemoryPackable<T>` — types like `Session`, `AgentMessage`, `ToolResult` (declared in `Harbor.Domain`) implement this interface. Without the project ref, the extension methods couldn't be tested against real types. |
| `CommunityToolkit.HighPerformance` | `FrozenDictionary<TKey, TValue>` / `FrozenSet<T>` are actually in-box (`System.Collections.Frozen`), but the toolkit is referenced for incidental helpers and consistency with the rest of the codebase.                                                                              |
| `MemoryPack`                       | `MemoryPackSerializer.Serialize<T>` / `Deserialize<T>` — the `IMemoryPackable<T>` constraint and the serializer entry-point.                                                                                                                                                          |

`System.Buffers` (`ArrayPool<T>`) is in-box for `net10.0` — no package ref needed.

**No references to `Harbor.Abstractions`** (the facade), no references to `Harbor.Core` / `Harbor.Application` / `Harbor.Registries`, no references to Infrastructure siblings (`Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tools.*`), no references to Presentation (`Harbor.Tui.*`, `Harbor.Desktop.*`). This invariant is enforced by `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs::Extensions_ReferencesOnlyDomain`.

## Namespaces preserved

All files in `Harbor.Extensions` keep their original `Harbor.Abstractions.Extensions` namespace:

| File                      | Namespace                        |
|---------------------------|----------------------------------|
| `ArrayPoolExtensions.cs`  | `Harbor.Abstractions.Extensions` |
| `CollectionExtensions.cs` | `Harbor.Abstractions.Extensions` |
| `MemoryPackExtensions.cs` | `Harbor.Abstractions.Extensions` |

This means **zero consumer code changes** — every project that does `using Harbor.Abstractions.Extensions;` keeps compiling and resolves to the types in `Harbor.Extensions.dll` via the transitive project reference chain `Harbor.Abstractions → Harbor.Extensions`.

## Public API surface (most-used types)

```csharp
// ArrayPool rental with auto-return
using RentedArray<byte> rented = ArrayPool.Shared.RentScoped(minimumLength: 4096);
rented.Span[0] = 42;
// ... use rented.Array / rented.Span / rented.ReadOnlySpan ...
// Dispose() returns the array to the pool automatically.

// StringBuilder pool
using PooledStringBuilder sb = StringBuilderPool.Rent(capacity: 1024);
sb.Builder.Append("hello ").Append("world");
string result = sb.ToString();   // "hello world"
// Dispose() returns the builder to the pool (unless it grew past 16 KiB).

// Frozen materializers (build-once, read-many)
FrozenSet<string> allowedTools = toolNames.ToFrozenSet(StringComparer.Ordinal);
FrozenDictionary<string, ToolDescriptor> byName =
    tools.ToFrozenDictionary(t => t.Name.Value, t => t);

// MemoryPack round-trip
byte[] bytes = session.ToMemoryPackBytes();
Session restored = bytes.FromMemoryPackBytes<Session>();
```

## Design principles

1. **Pooling over allocating.** `ArrayPool<T>.Shared` and `StringBuilderPool` cover the two hottest allocation patterns in the agent loop: per-turn LLM-streaming buffers (rented byte arrays) and per-turn system-prompt assembly (rented StringBuilders). Both auto-return on `Dispose`.
2. **Ref-struct disposable for rented arrays.** `RentedArray<T>` is a `readonly ref struct` — the compiler prevents it from being captured into closures or async state machines, eliminating the most common misuse of pooled buffers (accidental lifetime extension past `Dispose`).
3. **LOH-avoidance for the StringBuilder pool.** `MaxRetainCapacity = 16 KiB` means builders that grew past the small-object-heap boundary are dropped instead of being returned to the pool — prevents the pool from becoming a long-term LOH leak.
4. **Frozen over Immutable*.** `FrozenSet<T>` / `FrozenDictionary<TKey, TValue>` are JIT-specialized for read-only lookups (the JIT generates a separate specialized lookup path per `TKey` for `string` / `int` / `ValueTuple` etc.). For build-once / read-many patterns (tool registry, provider registry, agent registry after `Freeze()`), Frozen wins over `ImmutableHashSet<T>` by 2-4× on lookup.
5. **Extension-method sugar over direct API.** `ToMemoryPackBytes<T>(this T)` and `FromMemoryPackBytes<T>(this byte[])` exist purely so call sites read naturally (`session.ToMemoryPackBytes()` instead of `MemoryPackSerializer.Serialize(session)`). No behavior change.

## See also

- `src/Harbor.Domain/README.md` — the domain layer this project references
- `src/Harbor.Abstractions/README.md` — the interface facade that re-exports these helpers transitively
- `docs/ARCHITECTURE_LAYERS.md` — full layering matrix
- `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs::Extensions_ReferencesOnlyDomain` — enforces this layer's invariants
