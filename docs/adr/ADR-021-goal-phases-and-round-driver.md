# ADR-021: the goal is a durable, phased record, and its rounds ride the existing turn loop

> **Status (2026-10-21):** dated record, not normative. This document
> records a decision and the reasoning behind it. The parts that must stay
> true are fenced where they live: the contract in
> `src/Harbor.Abstractions/Sessions/Goal.cs` and
> `src/Harbor.Abstractions/Sessions/ISessionGoalStore.cs`, the durable axis
> in `src/Harbor.Storage.Jsonl/JsonlGoalStore.cs`, the driver in
> `src/Harbor.AgentRuntime/Agents/GoalRoundDriver.cs`, and the tests that
> pin them.

## Status

Accepted as a record. Issue
[#1761](https://github.com/refusedguy/Harbor-Harness/issues/1761) asked for a
long-lived «goal» with phases and a driver of autonomous rounds. This records
the shape that answer took, and — just as importantly — the shape it did NOT
take.

## Context

Harbor can already "spin turns": `AgentDefinition.MaxSteps` caps the loop,
`RunBudgetTracker` caps the spend, and the run-lifecycle contour (#41) owns
the honest question of why a run stopped. What it cannot do is agree with the
model on **what the work is**, and then carry that agreement to the end.

Three claims are easy to confuse, and the harness had only two of them:

- **"the agent says it changed X"** — a sentence in the assistant's message.
- **"the world says X changed"** — the bytes on disk differ.
- **"the work is done"** — the objective was met, and something that is not
  the model says so.

The third one is the one the run contour has no word for. A run that ends on
the step cap and a run that ends because the agent is done produce
structurally identical transcripts: a terminal assistant message and nothing
else. The caller cannot accept one and escalate the other, because the
transcript does not say which happened.

The source material for this issue (DSH) models the answer as a durable
entity with a phase (`active` / `blocked` / `complete`), a round budget, and
a driver that injects the next round into the same session. The owner's note
on the issue adds the constraint that decides the shape here:

> «цель» должна быть **надстройкой над run-lifecycle**, а не третьим
> параллельным механизмом. Правило: не заводить отдельный цикл раундов —
> использовать существующий run/limit/verification-контур и добавить в него
> лишь «фазу цели».

## Decision

### The goal is a durable record in the session log, and its phase is the fact

**A goal is a record, not a flag.** It carries the objective, the phase, the
round budget, the round counter, and — when it is blocked — a code and a
reason. It lives in the session file, in its own records (`"type":"goal"`),
beside the messages, the checkpoints and the feedback.

The phase is a three-way answer on purpose. `complete` and `blocked` are
both terminal and both say something a caller can act on: `complete` can be
accepted, `blocked` can be escalated. A goal that could only say "done" or
"not done" would push the guess onto the caller, and the guess is the thing
this axis exists to remove.

**The phase is durable; the activation is not.** Whether a driver is running
the goal right now is a fact about THIS process and lives nowhere durable.
The split is deliberate: "the process restarted" must not read as "the goal
was cancelled". A goal that was active when the process died is still active
when the next process opens the session.

The record lives in its own lines rather than as a field on a message, for
the same reason the feedback axis (#1746) does: a message is what the MODEL
reads, and a goal is what the HARNESS tracks. Keeping them apart in the file
makes "the goal never reaches the model context" a property of the format
rather than a promise.

### The rounds ride the existing turn loop; there is no second loop

**The round is reserved at the agent's QUIESCENCE point** — the turn in which
the model stopped asking for tools. That is the only moment at which "the
agent has nothing left to do on its own" is a fact rather than a guess, and
reserving a round earlier would spend rounds on a turn that was still
working.

At that point the driver either reserves the next round (appending the round
prompt to the SAME session, so the round continues the conversation rather
than starting a new one) or it ends the run. The driver's decision is small
and the loop stays the loop: `AgentLoop` asks the driver once, at the
quiescence boundary, and carries the answer.

**A goal that runs out of rounds ends the run with
`RunLimitKind.MaxGoalRounds`** — the same shape as every other bounded run
(`MaxSteps`, `MaxTokens`, `MaxCost`, `MaxOutputBytes`, `Timeout`). The goal
does not introduce a second way for a run to end; it adds one more reason a
run can end, and the terminal event carries it like the others.

**The round budget and the step budget are different bounds and both
apply.** The step cap still caps the whole run; the round cap caps the goal.
A run that hits the step cap first ends with `MaxSteps` even if the goal had
rounds left — the goal's phase stays `active`, and the next run picks it up.

### The revision is the concurrency token

Two writers write a goal: the driver counts rounds, and the agent completes
or blocks the goal. They can be writing at the same moment.

Every goal write appends a WHOLE snapshot with a revision, and a write whose
revision is not exactly one past the stored one is REFUSED. The loser of the
race re-reads and decides again; it does not overwrite the newer fact with
its older one. The revision is a counter rather than a timestamp precisely
because two writers can be told apart by it without a clock.

The store keeps every snapshot, so the phases a goal went through survive in
the log, exactly like the rest of the session's history (#1784).

### The agent's verdict is a tool call, not a paragraph

The agent is the only party that can say "the objective is met". The harness
does not take the agent's word for a change (ADR-020: "the agent's prose is
not an input"), and the same rule holds for the agent's word about its own
progress. "I have finished the task" in an assistant message is a CLAIM.

The `goal` tool is the channel: a tool call goes through the dispatcher, it
is recorded in the transcript, and it moves a durable record. The verdict is
therefore the same kind of fact as the rest of the session's state — dated,
readable and auditable. The tool does NOT start a goal: the objective is the
user's, and a model that could set its own objective would be marking its
own homework.

## What this slice does NOT do, and the order of the rest

This is a vertical slice, not a subsystem. What is here:

| Piece | Where |
|---|---|
| The contract (`Goal`, `GoalPhase`, `GoalBlockCode`) | `src/Harbor.Abstractions/Sessions/Goal.cs` |
| The store seam (`ISessionGoalStore`) | `src/Harbor.Abstractions/Sessions/ISessionGoalStore.cs` |
| The durable axis (JSONL) | `src/Harbor.Storage.Jsonl/JsonlGoalStore.cs` |
| The round driver | `src/Harbor.AgentRuntime/Agents/GoalRoundDriver.cs` |
| The model's channel | `src/Harbor.Tools.Builtin/Tools/Goal/GoalTool.cs` |

The rest, in the order it should be built:

1. **The user-facing way to set a goal.** A CLI verb (`harbor goal set
   "<objective>" --rounds N --session <id>`) and a REPL command (`/goal`).
   Today the goal is created through `ISessionGoalStore.AppendGoalAsync` —
   an embedding host can do it, the shipped CLI cannot. This is the first
   thing that makes the feature reachable for a user, and it is deliberately
   NOT in this slice: the CLI surface has its own shape (the verb registry,
   the REPL command catalog, the renderers), and a half-built one would be
   worse than none.
2. **A phase event on the event bus.** A `GoalPhaseChangedEvent` so a live
   UI can show "goal blocked" as it happens rather than only reading it out
   of the session afterwards. It is deliberately not in this slice because
   the event union lives in `Harbor.Abstractions.Contracts`, which cannot
   reference the goal types in `Harbor.Abstractions` — so the event needs
   either the goal types moved into the contract assembly or the event
   declared with its own phase vocabulary. That is a design decision, not a
   line of code, and it belongs with the UI work that needs it.
3. **The other backends.** The SQLite and memory backends implement
   `ISessionGoalStore` too. The seam was split out for exactly this: a
   backend that does not implement it has no goals, and the run behaves
   exactly as it did before the goal existed.
4. **Several goals per session, and editing one.** The slice has ONE goal per
   session, identified by the session that holds it; the record therefore
   carries no id of its own. Several goals need an id, a selector, and a
   `pause`/`resume`/`clear` operation set.
5. **The wrap-up grounding prompt.** The final round asks the model to report
   what the session actually establishes, and to say "not in the session"
   rather than invent. The round prompt of this slice carries the grounding
   clause for the CONTINUE case; the wrap-up case is the same principle at
   the end of the goal, and it wants the verification evidence (#42) to lean
   on.
6. **The goal as an input to the verification contour.** A `complete` goal
   and a `verified change` are related but not the same fact: the goal says
   the objective was met, the change report says what moved and which checks
   passed. #42 owns the second; the goal axis should cite it rather than
   re-derive it.

## Consequences

- **The run contour grows one member, not one mechanism.**
  `RunLimitKind.MaxGoalRounds` is the only change to the existing run
  vocabulary.
- **The driver is inert without a store.** A backend with no goal axis has
  no goals; a run then ends exactly where it ended before. That is the shape
  of every optional dependency in this codebase — absent means "the old
  behaviour", never "a broken one".
- **The goal is a harness fact, not a model fact.** It never enters the
  model context as history; the model learns about it through the round
  prompt and reports through the tool.
- **The slice is honest about its scope.** One goal per session, one
  backend, no user-facing entry point, no live event. The order above says
  what comes next and why.

## Links

- Issue #1761 — the goal with phases and the round driver.
- ADR-020 — the per-turn change oracle: the harness measures, the model
  reports, and the two must not be confused.
- ADR-018 / #1784 — the append-only session log this axis follows.
- #41 — the run lifecycle the goal rides rather than replaces.
- #42 — the verified change workflow, which the goal's `complete` phase
  will cite.
