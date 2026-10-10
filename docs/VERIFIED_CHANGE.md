# Verified Change Workflow

One command that runs the whole change chain end to end, with a fail-closed
state machine. Epic #42, entry point issue #397.

```text
pin contract (S1, #161)
  -> materialize worktree (S2, #376)
    -> agent runs in the worktree
      -> freeze change set (S3, #377)
        -> run checks (S4, #378)
          -> report (S5, #379)
            -> [operator] accept (S6, #382) | reject (S7, #385)
              -> owner report (S8, #392)
```

Run it:

```bash
harbor run change agent=<name> "<task>" [--checks <file>] [--dry-run] [--repo <path>]
```

## Status

Only the first two stages are implemented. The command runs them and then
stops fail-closed: the run is left honestly at `Isolated`, exit code 4 names
the missing stage, and `harbor run list` shows the run with its last
completed transition. Nothing is faked past `Isolated`.

| Slice | Stage | State |
|---|---|---|
| S1 | pin contract | landed |
| S2 | worktree + manifest | landed |
| S3 | frozen change set | open (#377) |
| S4 | checks | open (#378) |
| S5 | verification report | in progress (#379, slice 1: pure renderer, no verb yet) |
| S6 | accept | open (#382) |
| S7 | reject | slice 1 (#385): verb + `--all-effects` (exit 5); `--patch`/`--worktree`/`--undo-apply` pending |
| S8 | owner report | open (#392) |
| S9 | end-to-end driver | in progress (#397, this slice) |

## Run directory layout

Each run owns one directory, minted id, never user input:

```text
~/.harbor/runs/<RunId>/
  manifest.json   pinned contract fields, worktree path, lifecycle state
  worktree/       detached git worktree at the pinned revision
```

Later slices add their artifacts beside the manifest (`change.patch`,
`changeset.json`, `checks.json`, `report.json`, `owner-report.md`,
`accept.log`). The manifest is written atomically (temp + rename), so a
killed process leaves either the previous state or the new one, never a
half-written file. `report.json` is rendered by slice S5-1
(`src/Harbor.Application/Sessions/ChangeReport.cs`) from the frozen set
and the recorded checks; see Report format below.

## State machine

One explicit machine drives every run. States are persisted in the manifest;
the legal moves live in exactly one place,
`src/Harbor.Application/Sessions/RunChangeTransitions.cs`:

```text
Pinned -> Isolated -> Changed -> Checked -> Reported -> Accepted | Rejected
Isolated -> Released
Rejected -> Released
```

`Accepted` and `Released` are terminal: no outgoing transition. Every other
move is rejected with a named reason (`cannot apply a run that is not
Reported`), never silently ignored. An interrupted run stays in the last
completed state; there is no auto-retry and no blind resume.

## Exit codes

Documented in `HelpVerbs`; one code per terminal class, each non-zero exit
names the stage:

| Code | Meaning |
|---|---|
| 0 | dry-run plan printed (`--dry-run` writes nothing); later: reported, all checks passed |
| 1 | reported, at least one check failed (reserved for S4/S5) |
| 2 | bad usage (missing agent, missing task, missing checks file, unknown option) |
| 3 | pre-flight conflict: dirty workspace, moved base, or not a repository |
| 4 | internal failure at a named stage (today: `isolate`, or the missing `freeze`) |
| 5 | out-of-reach inventory (`--all-effects`): read-only, nothing written |

`--dry-run` performs the pin pre-flight and prints the plan, then stops
without creating a worktree. A failed pre-flight never leaves a worktree
registered.

## Report format (S5 slice 1)

Slice 1 is the pure renderer: `ChangeReport.Create` takes the frozen set
(S3) and the recorded checks (S4) as hand-built inputs and derives the
rest. No disk reads, no git, no new lifecycle state.

Six blocks, always in this order, never omitted — an empty block prints
`(none)`:

```text
== Run (3) ==
== Changed (N) ==
== Verified (N) ==
== NotVerified (N) ==
== Limits (2) ==
== KnownRisks (N) ==
```

- `Verified` cites, per check: name, full command line, exit code, the
  revision it ran on, and the effective env keys. A check with no recorded
  revision never appears here.
- `NotVerified` is derived, never authored: non-zero / timed-out /
  spawn-failed / cancelled checks, revision mismatches, the explicit
  `no checks declared` case, ignored-path exclusions, and the constant
  `external side effects: not observable`.
- `KnownRisks` is non-empty whenever ignored paths were dropped, the change
  set is empty, the worktree holds state not in the frozen set, or a check
  timed out — otherwise explicitly `[]`.
- Deterministic: sorted entries, fixed JSON key order, no clock reads — the
  same artifacts produce byte-identical `report.json`.
- Plain text fits 80 columns and contains no ANSI escapes.
- Exit codes: `0` all declared checks passed (zero declared checks is legal
  and exits `0` with the explicit entry), `1` at least one check failed,
  timed out, or never ran, `2` the report cannot be produced (reserved for
  the future `harbor run report` verb: missing or corrupt artifact).

Deferred to later slices: the `harbor run report <RunId>` verb reading
`changeset.json` / `checks.json` from the run directory (needs S3/S4
formats to land first), and the release-then-reread byte-identity test
(needs the S3 release path).

## Reject modes (S7)
Rejection is three separately invocable operations, never one blurred action:

```bash
harbor run reject <RunId> (--patch | --worktree | --all-effects | --undo-apply) [--force]
```

- `--patch`: drop the run's frozen artifacts; the worktree is untouched.
  Pending: the frozen set (S3, #377) has not landed, so slice 1 refuses
  with the stage named and deletes nothing.
- `--worktree`: remove the isolated working copy; pinned state becomes `Released`.
  Pending: slice 1 refuses with the stage named and removes nothing.
- `--all-effects`: implemented in slice 1. Prints the out-of-reach
  inventory below and exits 5. Read-only: no artifact is deleted, no
  working copy is removed, the manifest is unchanged.
- `--undo-apply`: reverses exactly the paths the frozen set touched, and only
  after accept. Pending: slice 1 refuses with the stage named and reverses
  nothing.

A mode flag is required: a bare `harbor run reject <RunId>` prints the
three modes and exits 2. There is no default.

Reject never touches the operator tree by default.

## What Harbor cannot undo

Stated once, here, so no later slice can promise it quietly: commits already
pushed, PRs or issues created, network calls, external API writes, database
migrations, files written outside the repository, `~/.harbor` session and log
records, and anything already consumed by downstream tooling. Anything outside
the frozen change set is, by construction, not observable by the report and
not reversible by reject. The runnable copy of this list lives in exactly
one place, `RunRejectEffects.OutOfReach` in
`src/Harbor.Application/Sessions/RunReject.cs`: the S5 report (#379)
renders from that constant instead of restating it.

## Agent discipline

The agent does not set its own acceptance criteria, does not weaken the
checks, and does not accept its own work. Checks are declared by the
operator, never generated by the agent. Accept and reject are operator
actions; the owner report is signed `Prepared by: agent` /
`Accepted by: <operator>`, and self-acceptance is rejected.
