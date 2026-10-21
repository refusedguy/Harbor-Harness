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
- `SessionArchiveWriter` — writes one session as a ZIP archive: the canonical log, the content-addressed attachments and the manifest (#1750)
- `SessionArchiveReader` — reads such an archive back: the manifest, the session record and the whole log, attachments resolved into bytes (#1750)
- `SessionArchiveManifest` / `SessionArchiveAttachment` / `SessionArchiveIntegrity` — what the archive says about itself (#1750)

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

## The session archive (#1750)

`harbor sessions export-zip <session-id> [file]` writes one session as ONE zip. The archive is the artifact a user attaches to a bug report, and it has three parts:

```
manifest.json                     the marker, the format version, the counts, the attachment index
session.jsonl                     the canonical log: a header line, then one line per record
attachments/<sha256>.<ext>        the attachment bytes, named by the SHA-256 of their content
```

**Why it is not a copy of the file.** The JSONL backend happens to keep a session in one file, so exporting it is a copy — but the SQLite backend keeps the same session in tables and has no such answer. The archive is assembled through `ISessionStore`, so every backend exports the same bytes for the same session. The file a backend happens to keep is that backend's business, not the session's.

**The log, not the surface.** The archive holds the WHOLE log: the version of a message an edit replaced, and the records a rewind took out of the surface. Each line carries the fold's verdict (`current` / `shadowed` / `logOnly`), because "current" is a property of the log as a whole and a reader must not be left to re-derive it. No backend has been migrated to the append-only log yet (#1784), so where the store offers no log the archive holds the surface and the manifest says so — `logSource` is `log` or `surface`, and a reader is never left to infer which.

**Content-addressed attachments.** An attachment's file is named by the SHA-256 of its bytes, so the same image attached to three turns is one file in the zip and three references to it. The bytes live in the file; the message line carries the reference plus the description the session recorded (path, MIME type, pixel size).

**No credential travels with it.** The archive is the one artifact that leaves the machine. The keyless tier (#1740) scrubs the log on the way to disk and the archive is built from that already-scrubbed log; the local tier does not scrub the log, and that is where the archive scrubs — with the same `SecretRedactor`, so both artifacts read alike.

**What the archive does not carry.** A `file` part names a path on the machine that produced the session; the file itself is not in the archive. Collecting files from disk would turn "export my session" into an exfiltration channel, and the session file does not carry those bytes either — the archive is exactly as complete as the log it is built from.

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

## Format version

**The session file is a public artifact, so its format has a version, a catalog of generations, and a chain of migrations from a file's own generation to the current one (#1749).**

The header carries the generation in `formatVersion`:

```json
{"type":"session","version":1,"formatVersion":1,"id":"…", …}
```

`version` (1) is the version of the header RECORD's field set. `formatVersion` (1) is the generation of the SESSION FILE — the whole contract, header and records together. A file that states no `formatVersion` was written before the marker existed: it is the legacy generation, V0.

**The current generation is 1, not 4.** The format grew three times already — the feedback records (#1746), the keyless marker (#1740), the transcript contract (#1806) — and every one of those growths was ADDITIVE: a new optional field, a new optional record kind, never a change to a record that already existed. An extension that had needed a retroactive migration would have shown up as a second generation, with a migration step and a fixture of its own.

**Every read runs the chain.** A V0 file is carried to V1 before anything reads its header: the header gains the format marker, and the last-activity stamp a V0 header cannot state is materialized from the file's own write time. The step is idempotent, and the writer always writes the current generation, so the first header rewrite upgrades a legacy file on disk.

**A file in a generation this build does not read is refused, everywhere it could be read** — the session record, the message history, the feedback records, the checkpoint markers — with the version in the message. The listing is the one path that cannot refuse (one unreadable file must not empty the session picker), so it skips the file and says so in the log.

The full contract, the catalog and the corpus are in [../../docs/SESSION_FORMAT.md](../../docs/SESSION_FORMAT.md). The corpus is one committed fixture per generation under `tests/Harbor.Storage.Jsonl.Tests/fixtures/`, and a test fails when a generation is declared without one.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md) — switching storage backends
