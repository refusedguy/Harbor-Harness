# Harbor.Storage.Shared

Shared source code compiled into the session-store assemblies via `<Compile Include>` link items (same mechanism as `Harbor.Providers.Shared`). Contains the canonical per-session lock strip, the shared failure-text shapes, and the message-history stats fold.

## Layer

**Storage infrastructure (shared source).** Not a standalone runtime library — files are linked into the session-store assemblies at build time. No `.csproj` here on purpose: a real project reference between storage assemblies would violate the `Storage_ReferencesOnlyAbstractions` architecture rule.

## Which store links which file

| File | Jsonl | Sqlite | Memory |
|------|-------|--------|--------|
| `SessionStoreErrors.cs` | yes | yes | **yes** (#887) |
| `SessionLockStrip.cs` | yes | yes | no |
| `SessionStatsAggregator.cs` | yes | no | no |

Each exclusion is a real difference in what the store does, not an oversight:

- **Memory does not link `SessionLockStrip.cs`.** The strip is a `SemaphoreSlim` per session over a `ConcurrentDictionary` the store owns as a field, and both Jsonl and Sqlite own one. Memory takes `lock (list)` on the per-session `List<AgentMessage>` instead — a different primitive with the same exclusion guarantee — and its read paths (`GetAsync`, `DeleteAsync`, `GetStatsAsync`, `UpdateAsync`) take no lock at all. There is no strip there to hand out.
- **Sqlite and Memory do not link `SessionStatsAggregator.cs`.** It folds `SessionMetadata` out of message history, which is what the JSONL store does on every `GetStatsAsync`. The other two persist the metadata record and return `session.Metadata`.

`SessionStoreErrors.cs` reaches all three, so `InvalidSessionId` sits unused in Sqlite and in Memory. That is not new and not a warning: it guards caller-supplied ids reaching `File.*` (#83), which neither of those two stores does, and both compile under `TreatWarningsAsErrors` today.

This folder is declared in `FullLayerMatrixTests.SharedSourceFolders` and held against the real csproj link items by `SharedSourceLinkRules` (#456): a csproj-less folder is invisible to `EnforcerIntegrityTests.SrcProjects_AreAllClassified`, which only enumerates directories that *have* a csproj, so being unlisted is what let the file-set rules read an incomplete picture of both stores. Adding a file here, dropping a `<Compile>` item, or adding a new shared-source folder all fail the gate.

`Harbor.Storage.Memory` used to link **neither** file and hand-wrote the failure texts **eleven** times in `MemorySessionStore.cs` — nine `SessionNotFound` and two `MessageNotFound`. #887 linked `SessionStoreErrors.cs` into it and replaced all eleven with factory calls, so the duplication is gone rather than merely held in agreement.

No string changed in that change: all eleven were byte-identical in shape to the factories here, so a person saw the same text whichever backend `HARBOR_STORAGE` selects, before and after. It was never a behaviour bug, only eleven hand-maintained copies of three sentences with no owner — and seven of the eleven were pinned by no test at all, so a twelfth or a drifted spelling would have compiled and shipped. `SessionStoreFailureTextParityRules` (#764) is the guard: its inline ratchet now reads **zero**, and its R4 rule holds every store project to declaring a `<Compile Include>` for the file below.

## What's in it

| File | Purpose |
|------|---------|
| `SessionLockStrip.cs` | `AcquireAsync` / `Evict` over a caller-owned `ConcurrentDictionary<string, SemaphoreSlim>` — per-session granularity replacing the old global `lock (_lock)` (#184). |
| `SessionStoreErrors.cs` | `SessionNotFound` / `MessageNotFound` / `InvalidSessionId` — the failure texts all three stores emit, linked by every one of them. Pinned by the #199 ROP suites. |
| `SessionStatsAggregator.cs` | `Aggregate` — the pure message-history → `SessionMetadata` fold moved verbatim out of `JsonlSessionStore.GetStatsAsync`. |

## Consuming it

Link only what the consumer uses — see the table above for which store links which file. The full set, for a store that needs all three:

```xml
<ItemGroup>
  <Compile Include="..\Harbor.Storage.Shared\SessionLockStrip.cs" LinkBase="Shared" />
  <Compile Include="..\Harbor.Storage.Shared\SessionStoreErrors.cs" LinkBase="Shared" />
  <Compile Include="..\Harbor.Storage.Shared\SessionStatsAggregator.cs" LinkBase="Shared" />
</ItemGroup>
```

Types live in `Harbor.Storage.Shared` and are `internal` to whichever assembly compiles them, so linking a file you do not call costs nothing at runtime but does widen that assembly's type list — which is why `SharedSourceLinkRules.The_Per_Project_File_Walk_Does_Not_Invent_Links` holds each project to the files its own csproj names.
