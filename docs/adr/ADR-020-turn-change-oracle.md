# ADR-020: the per-turn change oracle is computed by the harness, outside the agent

> **Status (2026-10-20):** dated record, not normative. This document records
> a decision and the reasoning behind it. The parts that must stay true are
> fenced where they live: the oracle in
> `src/Harbor.Application/Observability/TurnChangeOracle.cs`, its tests in
> `tests/Harbor.Application.Tests/TurnChangeOracleTests.cs`, and the lines
> the hosts draw in `apps/Harbor.App.Cli/Repl/`.

## Status

Accepted as a record. Issue
[#1745](https://github.com/refusedguy/Harbor-Harness/issues/1745) asked for a
per-turn «what changed» that the agent cannot talk its way around. This
records the shape that answer took.

## Context

Two claims are easy to confuse, and the harness used to have only one of
them:

- **«the agent says it changed X»** — a sentence in the assistant's message.
- **«the world says X changed»** — the bytes on disk are different now.

The second is evidence and the first is a claim. Everything in Harbor that
reported on a change so far took the first: the session header carries a
branch and a dirty flag
(`src/Harbor.Storage.Jsonl/SessionStoreRecords.cs`), and the verification
report of an isolated run is built from the artifacts the run left behind
(`src/Harbor.ChangeTracking/`). Neither answers «what did THIS turn change»,
and neither is computed per turn.

The interactive case is the one that had nothing at all: `harbor run` had a
frozen change set, and the REPL and `harbor ask` had no change story at all.

The issue proposed git snapshots of the working tree, taken by the harness at
the start and the end of a turn. That is one honest way to answer the
question, and it is not the one this record takes; the alternatives and the
reason are in the last section.

## Decision

### What counts as a change

**A workspace path whose bytes differ across the turn, as the harness
fingerprinted them.** The oracle takes a fingerprint of the target of every
mutating tool call twice: once when the tool announces itself, before it
runs, and once when it finishes. The verdict comes from comparing the two.

The verdicts are:

| verdict | meaning |
|---|---|
| `Created` | the path was not there before the turn and is there now |
| `Modified` | it was there and its content hash differs |
| `Deleted` | it was there and is gone |
| `Unchanged` | the hash is identical — the tool ran, the file did not move |
| `Unverified` | the harness could not read the path, so it says nothing about it |

Three properties of that definition are the whole point:

1. **The agent's prose is not an input.** The oracle never reads message
   text. A turn whose assistant message claims three edited files and whose
   tools touched none reports no change. A turn whose `write` failed reports
   `Unchanged`, because the file says so.
2. **The tools that write without declaring a path are named, not assumed
   away.** `bash`, `task`, an MCP server and a plugin tool are recorded as
   *unobserved*, and a turn that ran one is marked incomplete. The oracle
   never says «nothing changed» about a turn whose tools it could not look
   through.
3. **A path the harness cannot read is a verdict, not a silence.** A path
   outside the workspace, a path too large to hash, a path the tool never
   declared: each is reported as `Unverified`.

### Where it is computed

**In the event bus, as a projection, outside the agent loop.** The oracle is
a headless core service in the Application layer, beside the diagnostics
aggregator, which is the same shape of thing: a stateful projection over
`IEventBus` that the hosts read. It subscribes to the tool events and the
turn boundary and knows nothing about the agent, the prompt or the renderer.

The attribution of a tool call to a turn comes from the turn-end event's
assistant message, which carries the call's name and arguments and names the
session the turn ran in. That is deliberate: the tool events themselves
carry no session, so an oracle that paired them by arrival order would
cross-contaminate two sessions running in one process.

### What survives a restart

**The verification does not; the attribution does.** The oracle is live
state. A restart ends the session's reports.

This is a decision, not an omission. Persisting the reports would mean
either a change to the frozen `ISessionStore` contract — three backends and
the public API freeze — or a sidecar file beside the session, which is the
shape the storage abstraction exists to avoid. And the part that cannot be
recovered after a restart is exactly the part that is worth having: a
fingerprint taken ten minutes ago says nothing about the file now, and the
attribution that IS recoverable (which tool was asked to touch which path,
and whether it succeeded) is already in the session transcript, which the
store keeps.

The consequence is stated rather than smoothed over: after a restart the
harness can still say «the agent ran `write` on this path and the tool
failed», and it cannot say «the file's bytes differ».

### What the user sees

One line per turn that has something to report, drawn by the host and decided
by the core:

- the REPL appends the line to the chat after the prompt completes;
- `harbor ask` writes it through the renderer, so it lands in the same
  stream as the assistant's own text;
- every turn is logged, so the same fact is in the log file whatever the
  chat showed.

The line names what changed, and it names what could not be seen:

```
changed 2 file(s): src/a.cs, src/b.cs
no file changes; declared but unchanged: src/a.cs; unobserved: bash
```

A turn that touched no file and ran no tool the oracle cannot see through
prints NOTHING. Silence is the right answer for a turn where nothing the
oracle could measure happened, and a line in the chat on every turn of every
session would be noise. The log still records every turn, so the record is
complete even where the chat is quiet.

### What the agent can do with it

Nothing, in this slice — and that is the property, not an omission. The
oracle's value cannot be moved by anything the agent says, because nothing
the agent says is an input to it. Surfacing the report TO the model (a tool,
or a note in the next turn's system prompt) is a separate decision: the
issue's own constraint («набор тулзов не расширяем») rules out the tool
route, and the prompt route changes what the model sees, which is a
different contract from the one this record takes.

## Consequences

- The REPL and `harbor ask` answer «what changed in this turn» with a fact
  the harness measured, and a turn in which the agent over-claimed is
  visible as such.
- The oracle is a singleton: it is one answer about the workspace, and a
  second instance would be a second, silent truth. It subscribes to the bus
  in its constructor, so the host resolves it at the composition root, before
  the first turn.
- The mutating-tool list (`write`, `edit`, `patch`) and the read-only list
  are a declared table in the oracle. A tool on neither list is unobserved,
  which is the conservative direction: a plugin tool that writes files
  cannot make the oracle claim the workspace was untouched.
- The layer matrix is unchanged: the oracle lives in the Application layer
  and references no new assembly. The frozen contract assemblies are
  untouched, so the public API freeze is untouched.

## Alternatives considered

**Git snapshots of the working tree, before and after the turn.** This is
what the issue proposed, and it is the strongest form of the answer: it
catches a `bash` that rewrote a file the tools never named. It was not
taken because it is a different product: it forks `git` from a layer that
has no business forking `git` (the seam #537 and #538 closed), it needs a
temporary index and a temporary object directory to leave the user's
repository untouched, and it answers a DIFFERENT question — «what does the
tree look like now» rather than «what did the agent's tools do». The
unobserved-tools row is this ADR's version of that honesty: the oracle
names the tool it could not look through instead of guessing.

**A new `AgentEvent` subtype carrying the report.** Every renderer would
then see the report without any host wiring, which is the shape this ADR
wants. It was not taken because the event union is a frozen public contract
(`Harbor.Abstractions.Contracts`), and a new member of it is a change to
that freeze for a report two hosts read. The hosts read the oracle directly
instead, which is what the diagnostics aggregator already does.

**Persisting the reports in the session store.** Discussed above: the cost
is the frozen store contract, and the part that is worth keeping is the
part that does not survive.

## Where the code is

- the oracle: `src/Harbor.Application/Observability/TurnChangeOracle.cs`
- the report types: `src/Harbor.Application/Observability/TurnChangeReport.cs`
- the registration: `src/Harbor.Hosting/Modules/CoreModule.cs`
- the line the hosts draw: `apps/Harbor.App.Cli/Repl/TurnChangeSummary.cs`
- the tests: `tests/Harbor.Application.Tests/TurnChangeOracleTests.cs`
