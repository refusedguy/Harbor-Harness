# Harbor.Storage.Shared

Shared source code compiled into the session-store assemblies via `<Compile Include>` link items (same mechanism as `Harbor.Providers.Shared`). Contains the canonical per-session lock strip, the shared failure-text shapes, and the message-history stats fold.

## Layer

**Storage infrastructure (shared source).** Not a standalone runtime library — files are linked into `Harbor.Storage.Jsonl` and `Harbor.Storage.Sqlite` at build time. No `.csproj` here on purpose: a real project reference between storage assemblies would violate the `Storage_ReferencesOnlyAbstractions` architecture rule.

This folder is declared in `FullLayerMatrixTests.SharedSourceFolders` and held against the real csproj link items by `SharedSourceLinkRules` (#456): a csproj-less folder is invisible to `EnforcerIntegrityTests.SrcProjects_AreAllClassified`, which only enumerates directories that *have* a csproj, so being unlisted is what let the file-set rules read an incomplete picture of both stores. Adding a file here, dropping a `<Compile>` item, or adding a new shared-source folder all fail the gate.

`Harbor.Storage.Memory` links **neither** `SessionLockStrip.cs` nor `SessionStoreErrors.cs` — it has no `<Compile>` item at all, and hand-writes the `SessionNotFound` literal nine times in `MemorySessionStore.cs`. The declaration records that gap rather than implying all three stores share these files.

## What's in it

| File | Purpose |
|------|---------|
| `SessionLockStrip.cs` | `AcquireAsync` / `Evict` over a caller-owned `ConcurrentDictionary<string, SemaphoreSlim>` — per-session granularity replacing the old global `lock (_lock)` (#184). |
| `SessionStoreErrors.cs` | `SessionNotFound` / `MessageNotFound` / `InvalidSessionId` — byte-identical failure texts pinned by the #199 ROP suites. |
| `SessionStatsAggregator.cs` | `Aggregate` — the pure message-history → `SessionMetadata` fold moved verbatim out of `JsonlSessionStore.GetStatsAsync`. |

## Consuming it

```xml
<ItemGroup>
  <Compile Include="..\Harbor.Storage.Shared\SessionLockStrip.cs" LinkBase="Shared" />
  <Compile Include="..\Harbor.Storage.Shared\SessionStoreErrors.cs" LinkBase="Shared" />
  <Compile Include="..\Harbor.Storage.Shared\SessionStatsAggregator.cs" LinkBase="Shared" />
</ItemGroup>
```

Types live in `Harbor.Storage.Shared` and are `internal` to whichever assembly compiles them.
