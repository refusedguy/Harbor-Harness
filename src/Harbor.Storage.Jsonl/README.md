# Harbor.Storage.Jsonl

JSONL (newline-delimited JSON) session storage backend. Each session is a single `.jsonl` file under `<HarborDir>/sessions/` — append-only writes. Default backend for the CLI (`HARBOR_STORAGE=jsonl`, `src/Harbor.Hosting/Modules/StorageModule.cs:20-35`).

## Layer

Infrastructure — session/persistence backend. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `JsonlSessionStore(sessionsDir, logger)` — implements `ISessionStore` from Harbor.Abstractions
- `JsonlMessageCodec` — line serialization helpers for messages
- `JsonlCodecContext` — AOT-compatible compile-time `JsonSerializerContext` for the wire format (`JsonlCodecContext.cs:1-34`; per-line parsing avoids reflection on hot paths)

## Usage

Registered via DI by the host's `StorageModule`:

```csharp
string sessionsDir = Path.Combine(ctx.Options.HarborDir, "sessions");
services.AddSingleton<ISessionStore>(new JsonlSessionStore(sessionsDir, logger));
```

The agent persists messages as they flow via `ISessionStore.AppendMessageAsync`
(see `src/Harbor.AgentRuntime/Agents/DefaultAgent.cs:293`). Anchored-summary compaction is owned by `CompactionService` in `Harbor.Application` (`src/Harbor.SessionContext/Sessions/CompactionService.cs`) and folds old turns into a summary line in the same store.

## Record order

**The order of the records in the file is the order they were inserted in, and an update does not change it (#1798).**

- `AppendMessageAsync` appends one line at the end.
- `UpdateMessageAsync` REPLACES the record where it stands — the fresh record takes the line the stale one held, and every other record keeps its own. A stale duplicate of the same id (a file written before the duplicate-drop, or an append that raced the edit) is dropped, so the file holds one record per id.
- `DeleteMessagesAfterAsync` (the rewind) anchors on a record's POSITION in the file and drops every message record after it.

The invariant is what makes the rewind symmetric: a rewind to a message that was edited removes the messages that follow it. It was broken by a rewrite that dropped the stale record and appended the fresh one at the end of the file — the edited message jumped to the tail of the history, and a rewind anchored on it removed nothing, because the anchor was the last record in the file.

The read path is separate from the file order: the reader keeps the arrival order and sorts by `CreatedAt` only when the file is not append-linear in time. So a moved record could still read back in the right order while the FILE said otherwise — the guard for this invariant is about the file (`tests/Harbor.Storage.Jsonl.Tests/JsonlSessionStoreUpdatePositionTests.cs`).

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
