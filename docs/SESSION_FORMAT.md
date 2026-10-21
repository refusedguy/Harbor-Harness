# Session file format

What a session file is, which generation of the format it is in, and what a
reader does with a file whose generation it does not know.

The session file is a public artifact. It is what the session picker lists,
what the export copies, and, in the keyless tier (#1740), what a repository
carries, because a log written in that tier holds no provider credential. A
change to its shape is therefore a change to a contract, and this document is
where that contract is written down.

- The code: [../src/Harbor.Storage.Jsonl/SessionFormatCatalog.cs](../src/Harbor.Storage.Jsonl/SessionFormatCatalog.cs) (the generations), [../src/Harbor.Storage.Jsonl/SessionFormatMigrations.cs](../src/Harbor.Storage.Jsonl/SessionFormatMigrations.cs) (the chain), [../src/Harbor.Storage.Jsonl/SessionStoreRecords.cs](../src/Harbor.Storage.Jsonl/SessionStoreRecords.cs) (the header record).
- The corpus: [../tests/Harbor.Storage.Jsonl.Tests/fixtures/](../tests/Harbor.Storage.Jsonl.Tests/fixtures/) — one committed file per generation.
- The tests: [../tests/Harbor.Storage.Jsonl.Tests/SessionFormatCorpusTests.cs](../tests/Harbor.Storage.Jsonl.Tests/SessionFormatCorpusTests.cs).

## The file

One session is one file, `<sessionId>.jsonl`, under the store's directory.
Line 1 is the header; the lines after it are the records, in the order they
were written.

```json
{"type":"session","version":1,"formatVersion":1,"id":"…","projectId":"…","directory":"…","title":"…","agent":"…","model":"…","providerId":"…","createdAt":"…","updatedAt":"…","parentSessionId":null,"status":0,"gitBranch":null,"gitIsDirty":false,"kind":0}
{"type":"message","id":"…","parentId":null,"role":"user","createdAt":"…","payload":{…}}
{"type":"checkpoint","id":"…","messageId":"…","messageIndex":0,"createdAt":"…","label":null,"rewindOf":null,"removed":0}
{"type":"messageRating","id":"…","messageId":"…","rating":"Helpful","category":"TaskResult","createdAt":"…","note":null}
{"type":"sessionRemark","id":"…","text":"…","createdAt":"…"}
```

There are four record kinds. A `message` is what the model reads. The other
three are not history: a `checkpoint` is a rewind marker (#1247), a
`messageRating` is the user's rating of one message, and a `sessionRemark` is
free text about the session (#1746). The message read path skips all three,
which is what makes "the feedback never reaches the model" a property of the
format rather than a promise.

### The header's two versions

The header carries two version fields, and they answer different questions.

| Field | Question it answers | Value |
|---|---|---|
| `version` | Which shape is this header record? | `1`, since the first commit of the store |
| `formatVersion` | Which generation of the session format is this file? | `1` in every file this build writes |

`formatVersion` is the generation of the whole contract, header and records
together. It is absent in a file written before the marker existed.

## The generations

| Generation | Header | Meaning |
|---|---|---|
| V0 (legacy) | no `formatVersion` | written before the format was versioned |
| V1 (current) | `"formatVersion":1` | what this build writes |

V1 is the current generation and it is generation 1, not generation 4,
because the format grew three times already and every one of those growths
was additive:

- the feedback records (#1746) added a record kind and changed no record that
  already existed;
- the keyless marker (#1740) added a header field and changed no record that
  already existed;
- the transcript contract (#1806) added a reader's view, derived in memory
  rather than stored.

An extension that had needed a retroactive migration would have shown up as a
second generation, with a migration step and a fixture of its own. None did,
so V1 covers every file the store has ever written, except the ones written
before the marker existed.

## The migration chain

A file is read through the chain from its own generation to the current one.
The chain is walked one generation at a time, so the order of the steps is
the order of the generations.

| Step | What it does |
|---|---|
| V0 to V1 | the header gains the format marker, and the last-activity stamp a V0 header cannot state is materialized from the file's own write time |

Every other field the V1 generation states is already at its V1 value in a
V0 header, because a V0 header simply did not state it. The reader has been
filling the V1 defaults in since the day those fields were added, so the
migration changes the header's shape and not what a caller sees.

The rules the chain obeys:

- **A step is idempotent.** Applied to a header that already carries the
  marker and a stamp, it returns the same header.
- **A gap is an error, not a guess.** A generation with no step fails the
  read with "this build has no migration from that generation".
- **The writer always writes the current generation.** So the first header
  rewrite upgrades a legacy file on disk, and a file this build writes is
  never a legacy file.

## A file this build does not read

A file whose generation is above the current one is refused, everywhere it
could be read: the session record, the message history, the feedback records
and the checkpoint markers. The refusal names the version:

```
Session 'abc' is written in session format version 2, which this build does not read (it reads up to version 1): /home/u/.harbor/sessions/abc.jsonl.
```

The listing is the one path that cannot refuse, because one unreadable file
must not empty the session picker. It skips the file and says so in the log,
with the same reason.

A session file is read as a whole or not at all. Reading half of a file whose
meaning this build does not have is how records go missing without a word.

## The corpus

Every generation has a committed fixture, and every step of the chain is
covered by the fixture of the generation it reads. A migration nothing
exercises is a migration nobody knows works.

| Generation | Fixture | What it holds |
|---|---|---|
| V0 | `session-format-v0-legacy.jsonl` | a header that states no generation and none of the V1 fields, plus two message records |
| V1 | `session-format-v1-current.jsonl` | a header that states the generation, two message records, a checkpoint and a rating |

The fixtures live in
[../tests/Harbor.Storage.Jsonl.Tests/fixtures/](../tests/Harbor.Storage.Jsonl.Tests/fixtures/)
and are copied next to the test binary, so the corpus is read as the on-disk
artifact it is. A fixture's file name is the session id inside it, so a
fixture copied into a store's directory is found under the name the store
looks it up by.

The policy is a test, not a promise:
`EveryGenerationInTheCatalog_HasACommittedFixture` fails when a generation is
declared without a fixture, and `EveryMigrationStep_IsCoveredByAFixture`
fails when a step reads a generation the corpus does not cover.

## The other backends

The SQLite backend has its own schema and evolves on its own path
(`SqliteSchema`); its `version` column holds the product version, not a
format generation. The memory backend has no file at all. The contract on
this page is the JSONL session file's, and the two backends are not compared
across formats.
