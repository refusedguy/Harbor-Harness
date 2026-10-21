# Harbor.Storage.Jsonl

JSONL (newline-delimited JSON) session storage backend. Each session is a single `.jsonl` file under `<HarborDir>/sessions/` — append-only writes. Default backend for the CLI (`HARBOR_STORAGE=jsonl`, `src/Harbor.Hosting/Modules/StorageModule.cs:20-35`).

## What this backend is

A session log on disk, one file per session, in the order the records were written. The file is the durable artifact of a run: it is what the session picker lists, what the export copies, and — in the keyless tier (#1740) — what a repository can carry, because a log written in that tier holds no provider credential.

The store owns the on-disk schema and nothing else. It does not decide what a session IS, and it does not talk to a provider: a message arrives at `AppendMessageAsync` already built, and the store writes it.

## Layer

Infrastructure — session/persistence backend. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `JsonlSessionStore(sessionsDir, logger)` — implements `ISessionStore` from Harbor.Abstractions
- `JsonlSessionStore(sessionsDir, logger, tier, literalSecrets)` — the same store, writing in a chosen tier (#1740)
- `SessionLogTier` — `Local` (the default: nothing is scrubbed) or `Keyless` (credentials are scrubbed on the way to disk, and the header says so) (#1740)
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

## Wiring

The store is registered by the host's `StorageModule` as `ISessionStore`, and the host picks the backend with `HARBOR_STORAGE`.

The TIER of the logs it writes is picked with `HARBOR_SESSION_TIER`:

- `local` (the default) means the log stays on this machine. Nothing is scrubbed, and the file holds the agent's text verbatim.
- `keyless` means the log is meant to be committed and replayed. Every text-bearing field is scrubbed of provider credentials on the way to disk, and the header carries `"sharing":"keyless"` so a reader can tell a shareable artifact from a private notebook.

An unknown value fails the start-up rather than falling back. The default tier is the UNSCRUBBED one, so a typo in the variable name would otherwise write a log full of credentials into a repository.

The keyless tier also needs the credential values the process holds. The provider key is the one secret whose shape no rule can recognise, so the host passes the values of the credential-named environment variables to the store, and the store replaces every occurrence of them.

## Limitations

- The tier scrubs CREDENTIALS. It does not scrub paths: the absolute working directory in the header still names the machine, and a transcript full of `/home/<user>/...` is still what it is. Normalizing paths is a separate axis from removing a secret.
- The provider id, the model id and the base URL are NOT credentials. They are published in the provider catalogue the repository already carries, and a log that does not say which model produced it cannot be replayed against anything.
- A rule that matches the SHAPE of a credential cannot catch a credential with an unfamiliar shape. The value-based pass (the environment the process holds) is what closes that gap, and it only closes it for the values this process actually holds.
- The tier is a property of the STORE, so it is a property of the process. One sessions directory carries one tier: a directory written by a keyless run and then appended to by a local run holds both kinds of record.
- The tier changes what LEAVES the machine, never what the agent sees. The in-memory message keeps its real text, so a run in the keyless tier behaves exactly like a local run.

## Record order

**The order of the records in the file is the order they were inserted in, and an update does not change it (#1798).**

- `AppendMessageAsync` appends one line at the end.
- `UpdateMessageAsync` REPLACES the record where it stands — the fresh record takes the line the stale one held, and every other record keeps its own. A stale duplicate of the same id (a file written before the duplicate-drop, or an append that raced the edit) is dropped, so the file holds one record per id.
- `DeleteMessagesAfterAsync` (the rewind) anchors on a record's POSITION in the file and drops every message record after it.

The invariant is what makes the rewind symmetric: a rewind to a message that was edited removes the messages that follow it. It was broken by a rewrite that dropped the stale record and appended the fresh one at the end of the file — the edited message jumped to the tail of the history, and a rewind anchored on it removed nothing, because the anchor was the last record in the file.

The read path is separate from the file order: the reader keeps the arrival order and sorts by `CreatedAt` only when the file is not append-linear in time. So a moved record could still read back in the right order while the FILE said otherwise — the guard for this invariant is about the file (`tests/Harbor.Storage.Jsonl.Tests/JsonlSessionStoreUpdatePositionTests.cs`).

## Feedback records

**The feedback is the USER's, and it is not history.**

A session file may carry two kinds of feedback record, appended to the same file beside the messages:

- `{"type":"messageRating",...}` — the user's rating of ONE message: a value (`Helpful`, `Neutral`, `Unhelpful`), the axis it was given on (`TaskResult`, `InstructionFollowing`, `ProductInteraction`, `ServiceStability`, `ResourceCost`, `SecurityPrivacyPermission`, `Other`), and an optional note.
- `{"type":"sessionRemark",...}` — free text about the session as a whole.

The message read path skips both, exactly as it skips the checkpoint markers. That is what makes "the feedback never reaches the model" a property of the format: the messages the model context is built from carry no feedback, and a session file written before the feedback axis existed reads as it always did.

The records are append-only, like the rest of the store. A second rating of the same message is appended, not overwritten: the effective rating is the newest one, and the older one stays in the log. A rating of a message the session does not hold is refused before anything is written, so the feedback never names a message that is not there.

Read them back through `ISessionFeedbackStore` — the seam the JSONL store implements beside `ISessionStore` (the SQLite and memory backends do not implement it yet).

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
