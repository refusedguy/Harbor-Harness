# Harbor.ChangeTracking

## What lives here

The **verified-change pipeline** as its own assembly (#1616). Everything between
"an agent's tree change happened in an isolated worktree" and "the operator
accepted or rejected it" lives here: worktree materialization, the frozen
change set, check execution, the rendered verification report, and the
accept / reject decisions that move the run manifest through
`Checked -> Reported -> Accepted / Rejected`. It is a `src/` leaf above
`Harbor.Application` — it *reads* the shared run vocabulary Application owns
and owns nothing Application binds to (#1616 extracted it verbatim, zero
opposite edges).

## Public API

Namespaced `Harbor.ChangeTracking`. Static maps, one per pipeline stage, with
the run manifest as the single point of truth:

| Type | Surface | Stage |
|---|---|---|
| `WorkspaceMaterializer` | `Result<RunManifest> TryLoad(string? runIdValue)` | S2 — locate + validate the run under the runs directory, refuse unknown/corrupt |
| `ChangeSetFreezer` | `Task<Result<FrozenChangeSet>> FreezeAsync(…)` · `Result<FrozenChangeSet> TryLoad(string? runIdValue)` · `record FrozenChangeSet(IReadOnlyList<FrozenChangeEntry> Entries, …)` | S3 — pin the change per claimed path, load the frozen set back |
| `CheckRunner` | `Task<Result<CheckReport>> RunAsync(…)` · `Result<CheckReport> TryLoad(string? runIdValue)` · `record CheckReport(…)` · `enum CheckOutcome` | S4 — run the declared checks against the materialized worktree |
| `RunReporter` | `Task<ReportResult> ReportAsync(string? runIdValue, CancellationToken ct = default)` · consts `ReportJsonFileName` / `ReportTextFileName` | S5 — render the deliverable report from recorded artifacts only |
| `RunChangeTransitions` | `Result<RunState> TryTransition(RunState from, RunState to)` · `bool IsTerminal(RunState)` | the manifest state map used by every stage |
| `ChangeApplier` | `Task<AcceptResult> AcceptAsync(string? runIdValue, bool dryRun = false, bool reverify = false, CancellationToken ct = default)` · `enum AcceptOutcome` | S6 — base-gate + apply `change.patch`, dry-run-cancel / conflict-aware |
| `RunReject` | `RejectResult Decide(string? runIdValue, string? modeFlag, bool force = false)` · `enum RejectOutcome` | S7 — refuse/reject effects, incl. the out-of-reach inventory |
| `OwnerReport` | `Result<OwnerReport> Create(…)` · `Result<string> Save(OwnerReport)` · `Result<string> TryLoad(string?)` · `class OwnerReportFormat` (render/validate helpers) | S8 — per-record verified/unverified-owner summary |
| `FileClaimRegistry` | `FileClaimRegistry(string directory, TimeSpan? staleGrace = null)` · `IDisposable` · `class FileClaim` (`Scope` / `ClaimPath` / `OwnerPid`) | cross-process path claiming so two runs cannot materialize one tree |

Every public surface speaks `Result` (CSharpFunctionalExtensions) — expected
refusals are returned, never thrown. The run vocabulary types appear in
signatures from `Harbor.Application.Sessions` (`RunManifest`, `RunState`,
`IsolatedWorkspace`, `WorkspacePaths`) and `Harbor.Abstractions.Models.Identifiers`
(`RunId`).

## Wiring

No DI registration and no env var. The assembly is bound directly by the
consumer: `apps/Harbor.App.Cli` carries the `ProjectReference` and its
`run report / accept / reject`, `run owner-report` and `task-run` verbs
(`apps/Harbor.App.Cli/Commands/Run*Verb.cs`, `Commands/TaskRunRunner.cs`)
call the static maps above. `Harbor.Application` itself binds nothing
ChangeTracking offers — the #1616 extraction was closure-verified as having
zero reverse edges.

## Usage

```csharp
using Harbor.ChangeTracking;
using Harbor.Application.Sessions;

// Load a run manifest ( verdict is Result, so an unknown or corrupt run id
// is a refused value, not an exception ).
Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runIdValue);

// Verify the manifest is where the caller expects to enter from, and only
// then ask for the report. ReportAsync itself re-gates with
// RunChangeTransitions.TryTransition, is idempotent when the run is
// already Reported, and fails closed with the named reason otherwise.
ReportResult report = await RunReporter.ReportAsync(runIdValue, ct);

// Apply the frozen change to the operator's tree. dryRun: true prints the
// exact git commands and diff stat and writes nothing; a run already
// Accepted is refused and nothing changes.
AcceptResult applied =
    await ChangeApplier.AcceptAsync(runIdValue, dryRun: true, ct: ct);
```

## Dependencies

- `Harbor.Abstractions.Contracts` — the models the pipeline's outputs go through.
- `Harbor.Application` — the shared run vocabulary the pipeline reads:
  `RunId` (Models.Identifiers), `IsolatedWorkspace` / `RunManifest` / `RunState`
  and `WorkspacePaths` / `IWorkspacePaths` (sessions/, #1610 — deliberately
  kept in Application so Application stays a leaf over Abstractions).
- `Harbor.Extensions` — cross-cutting helpers.
- Referenced by: `apps/Harbor.App.Cli` (direct — the verbs above), the
  `Harbor.ChangeTracking.Tests` suites (the 12 moved pipeline tests), and
  `Harbor.Architecture.Tests` (pins the edges declared here).

## Known limitations

- `AcceptAsync(reverify: true)` — re-run the S4 suite before applying — is
  refused until full re-verification wiring is complete; dry-run and plain
  accept are the supported paths.
- Check execution (S4) drives the materialized worktree from the caller
  side, so the check matrix that exercises `Harbor.Tools.Builtin` tools
  (bash / write) lives in the *tests* — the library itself carries no
  tools dependency.
- No compaction interplay by design: the pipeline is about run manifests,
  not session transcripts.
