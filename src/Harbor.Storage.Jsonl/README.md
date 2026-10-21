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

**The file is an append-only log (#1752): every record is written once and never rewritten. The order of the records is the order they were written in, and the SURFACE — what the model and the UI see — is a fold of that log.**

- `AppendMessageAsync` appends one line at the end.
- `UpdateMessageAsync` appends a NEW VERSION of the message. The record written before stays where it was, and the surface shows the newest version. The message keeps its place in the conversation: a message's place is where it was written FIRST, so an edit changes what was said, not when it was said.
- `RewindMessagesAfterAsync` (the rewind) appends a `{"type":"rewind","id":...,"anchorId":...,"at":...,"removed":N}` record. The messages after the anchor leave the SURFACE — the records stay in the file.
- `PurgeMessagesAfterAsync` (the prune) is the ONE verb that removes records: it rewrites the file without the tail after the anchor. It is never automatic — a caller has to ask for it by name.

The read path is separate from the file order: the reader keeps the arrival order and sorts by `CreatedAt` only when the file is not append-linear in time. The fold of the log (`SessionLogFold`, in `Harbor.Abstractions.Contracts`) applies the same rule, so the reader and the fold cannot disagree about where a message sits.

Read the two views apart: `GetMessagesAsync` gives the surface (the current state of the conversation), `GetSessionLogAsync` gives the whole log (every version, every rewind event). A message that a rewind removed is SHADOWED — present in the log, not gone from it.

A legacy file — one written before the rewind record existed — cannot contain a `{"type":"rewind"}` record, so the reader needs no format bump to understand one, and a legacy file reads exactly as it always did.

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
