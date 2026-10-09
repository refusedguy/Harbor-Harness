# ADR-014: `Harbor.Ui.Framework.Sessions` stays where it is — the interface already exists, and the matrix forbids the move

> **Status (2026-10-02):** dated record, not normative. Every `file:line` below was
> read at `dev` = `4a55260b`. This document records a MEASUREMENT and a deferred
> decision; it asserts no machine-checkable fact about the tree as it stands, so it
> carries no `Status: normative` banner. The one fact it does fence is fenced in
> `tests/Harbor.Architecture.Tests/ModelRefSingleParserTests.cs`, red on the pre-fix
> tree.

## Status

**Open — the owner decides.** #439's three acceptance items were measured rather
than executed. Two of the three ask for something the tree does not permit, and the
third asks for an extraction that is already done. No product code moved, no project
was added, deleted or renamed, and no interface was introduced.

## Context

Issue #439 (slice `33/T6` of epic #33) asks for three things:

1. move `src/Harbor.Ui.Framework.Sessions/Sessions/` into `Harbor.Application`;
2. extract `IChatViewState`, `IToolCallViewModel`, `IDiffRenderer` into
   `Harbor.Ui.Framework.Abstractions` instead of leaving them beside the widgets;
3. answer #33's three discussion questions.

Item 1's premise — that `Sessions/` is *Harbor-specific session orchestration, not a
UI framework concern* — is the claim worth testing, because a project move is a
boundary change and not a rename. Measured on `dev` = `4a55260b`.

## 1. Is there already an interface for this?

**Yes, and it was added for exactly this reason in #470.**
`src/Harbor.Ui.Framework.State/Panels/IPanelSessionGateway.cs` is the extracted,
lower-layer session port, and its own XML doc states the rule the issue re-derives:

> the real owner (`Harbor.Ui.Framework.Sessions.ISessionManager`) lives in an
> assembly that already depends on this one — naming it from `PanelContext` would
> invert the layer. This interface is the seam.

`CellForgeTuiRenderer` already consumes it that way (`CellForgeTuiRenderer.cs:130`,
"#470: an ISessionManager IS an IPanelSessionGateway"), and the composed facade
`ISessionManager : ISessionQueries, ISessionLifecycle, ISessionStatusTracker,
IPanelSessionGateway` (`ISessionManager.cs:16`) is the surface panels and renderers
are meant to bind. So the "generic interface extraction" for the session surface is
**done, and done the way the layer model requires** — a narrow port below, adapted at
the composition root, not the owner dragged downward.

That is why this record says *do not extract* rather than *extract*: the question is
not "is a seam missing", it is "is there a second, competing place doing this by hand".
Measured, there is not one.

## 2. The three interfaces named in acceptance item 2 do not exist

| named in #439 | occurrences in `src/`, `apps/`, `tests/` |
|---|---|
| `IChatViewState` | **0** |
| `IToolCallViewModel` | **0** |
| `IDiffRenderer` | **0** |

This is the `IFailure` shape the #33 series keeps meeting: the acceptance criterion
names a type the tree has never had. What exists under those names is a set of
concrete types, and none of them is co-located with widget code in a way the
criterion describes:

| the concrete type | where it lives | verdict |
|---|---|---|
| `ToolCallViewModel` | `Harbor.Ui.Framework.ViewModels/ViewModels/` | already in the reusable layer |
| `ChatViewModelBase` | `Harbor.Desktop.Abstractions/ViewModels/` | already out of the widgets |
| `DiffRenderer` | `src/Harbor.Tui.CellForge/Chat/Widgets/ToolCallBlock.cs:637` | `internal static` helper |

Writing those three interfaces is therefore not an extraction but an **introduction**,
and an introduction is a new axis: each one needs an implementation, a DI
registration, and every consumer rewired — the hand-maintained list that #555 freezes
and that #557 was filed to count. The history on both sides is unfavourable:
`IPluginLoadHost.TuiPlugins` turned out to be a **non-substantive** member attributed
in seven places, `RouteKey` and `DefaultClientFactory` turned out to be **dead** beside
five live implementations, and #581 was raised specifically about fake seams.

## 3. What the §2 matrix says about the move

`docs/ARCHITECTURE_LAYERS.md` §2 is a TL;DR; the mechanical source of truth is
`tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs`, and the two agree here.
The rows that decide it:

| row | layer | allowed references |
|---|---|---|
| `Harbor.Application` | **Application** | `Harbor.Abstractions`, `Harbor.Diagnostics.Abstractions`, `Harbor.Extensions` |
| `Harbor.Ui.Framework.Sessions` | **Presentation** | `Harbor.Abstractions`, `Ui.Framework.{State,Services,Abstractions,ViewModels}` |

The proposal is to make the second row's contents the first row's, which requires
`Harbor.Application` → `Harbor.Ui.Framework.{State,Services,ViewModels}` — three
edges that are not in its allowed set, and all three pointing **upward** into
Presentation. `FullLayerMatrixTests` blocks this twice over:

- `EverySrcAssembly_ReferenceSet_MatchesMatrix` reads the real assembly references
  out of the compiled IL, so a new edge fails by name;
- `MatrixTable_RespectsLayerRules` refuses even to *pre-declare* it, because
  `Layer.Application => toLayer is Layer.Domain or Layer.Application`. There is no
  table edit that permits this move short of a `DocumentedExceptions` entry with a
  reason.

That reason already exists, for the same shape, one layer down. The
`Harbor.DesignSystem` entry records that its watcher is "an edge UPWARD on purpose"
and that "Application cannot hold it either — it sits below Infrastructure". §2's own
prose says the same: *"Application layer (Core/Application) may not depend on UI
vocabulary … Core is the agent-harness application layer and must not know about UI
vocabulary."*

**So the matrix is not merely inconvenient — it is the correct reading.** `Sessions/`
is classified Presentation because it genuinely is Presentation-bound: **11 of its 14
files use a `Layer.Presentation` type**, comment-stripped.

| Presentation type | assembly | files that use it |
|---|---|---|
| `UiStore` | `Ui.Framework.State` | 3 |
| `StatusChanged` | `Ui.Framework.State` | 3 |
| `ChatAppMsg` | `Ui.Framework.State` | 2 |
| `HydrateSession` | `Ui.Framework.State` | 2 |
| `IPanelSessionGateway` | `Ui.Framework.State` | 2 |
| `ConfigureRuntime` | `Ui.Framework.State` | 1 |
| `GitSessionInfo` | `Ui.Framework.Services` | 3 |
| `GitService` | `Ui.Framework.Services` | 3 |
| `SessionStatusTracker` | `Ui.Framework.Services` | 2 |
| `StatusMappers` | `Ui.Framework.ViewModels` | 1 |

(Counts are per type, and overlap: `SessionManager` names three of them.)

The three files that bind nothing Presentation are `ISessionLifecycle.cs`,
`SessionEventRouter.cs` and `SessionFactory.cs` — and that is not a seam. The
composition chain is `SessionManager → SessionLifecycleService → SessionFactory`
(recorded as fact in `SessionForkPortSeamRules.cs:28`), so the one large Presentation-free
file is reached *through* `SessionLifecycleService`, which is not Presentation-free.
Extracting three files would fragment the chain and leave the big one stranded behind
it.

Incidental finding, not fixed here: `SessionFactory.cs:8` carries
`using Harbor.Ui.Framework.State;` that names no type — every `UiStore` in that file
is inside a doc comment. It is invisible to the compiler because unused-using is an
IDE-only diagnostic, so it is recorded rather than changed.

## 4. Is there a cycle? No.

If a cycle existed, the move would be forced rather than proposed. Measured, it does
not: `Harbor.Ui.Framework.{State,Services,ViewModels,Abstractions}` reference only
`Harbor.Abstractions` and each other. **None of them references `Harbor.Application`
or `Harbor.Registries`.** The move would add three new upward edges, not break a loop
— which is why §2 is the whole answer to "is this needed?", and the answer is no.

## 5. Is `Sessions/` dead? No — and deletion is off the table.

`src/Harbor.Ui.Framework.Sessions/` contains **14 files, 1547 lines, 14 public types**
and nothing else. Consumers, counted by word-boundary reference **outside** the
defining project, with comments and string literals separated out so a fixture string
cannot inflate the number:

| type | external code refs | verdict |
|---|---|---|
| `SessionManager` | 49 | live |
| `SessionFactory` | 38 | live |
| `SessionContext` | 34 | live |
| `ISessionManager` | 29 | live |
| `SessionSwitcher` | 14 | live |
| `IChatViewBinder` | 11 | live |
| `SessionGitTracker` | 9 | live |
| `SessionEventRouter` | 6 | live |
| `SessionLifecycleService` | 5 | live |
| `SessionOptionalFactories`, `SessionStatusService` | 3 each | live |
| `ISessionLifecycle`, `ISessionQueries`, `ISessionStatusTracker` | 1 each | live |

**0 of 14 types are dead** — the total is 204 external code references across `src/`,
`apps/` and `tests/`. `SessionManager` is resolved by DI in
`apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs:103`, `UiEventRouter.cs:57`
and `App.axaml.cs`, and injected into `CellForgeTuiRenderer`. Since #966 deleted the
dead `TuiPlugins` and #984 deleted a dead `DiffPreview.cs`, deletion was checked first
and it does not apply.

## 6. Is there anything to merge? No.

Three folders are named `Sessions/`, so "move it into Application" could have meant a
merge. Measured, all three declare **disjoint type names** — zero collisions, zero
shared filenames:

| folder | files | types | lines |
|---|---|---|---|
| `Harbor.Abstractions/Sessions` | 14 | 14 | 603 |
| `Harbor.Application/Sessions` | 13 | 13 | 3203 |
| `Harbor.Ui.Framework.Sessions/Sessions` | 14 | 14 | 1547 |

They are Domain contracts, Application services and the Presentation facade
respectively. There is no duplicated logic to collapse, which is the third sense in
which "extract a generic interface" has nothing to extract.

## 7. What the move would cost: eight path-pinned guards

This is the "перенос контракта" case, not the cheap one, and the pinning is worth
recording because it splits into two behaviours.

| guard | on a move |
|---|---|
| `ProviderModelAbsenceRules.CallerRule_ReachesEveryFileItClaimsToPolice` | **red** — asserts each consumer file exists |
| `SessionForkPortSeamRules` (`ConsumerPath`, line 277) | **red** — "does not exist, so there is no consumer left" |
| `MapErrorFailureShapeTests.SessionsSlice_HasNoHandBuiltFailureMessage` | **red** — its `scanned > 3` non-vacuity floor collapses to 0 |
| `ModelRefSingleParserTests.TheConfigSeam_QualifiesAtTheProducer_NotInTheConsumer` | **was silently green** — fixed in this PR |

That last row was the only genuine defect found in the blast radius, and it is the
`#591` shape: `CountDelegations` caught `IOException`, `FileNotFoundException`
derives from it, so the `== 0` assertion reported *"this file does no hand-rolled
qualification"* — the answer the test wanted to prove — for a file that was not there.
The fix is in the same commit as a red-first regression test, and it is fenced by
`ADelegationCount_NeverReportsZeroForAFileItCouldNotRead`, whose synthetic path cannot
exist.

Note the contrast with `CommonConfigContractRules`, which is *also* path-pinned but
whose negative assertion would go vacuous on a move — and whose
`SeamPerimeter_CoversEveryProductFileThatNamesThePort` is **derived** from files that
name the port, so it follows the file and catches the change. Derived perimeters
survive a move; typed constants do not. That is the distinction #767 already learned
and it is worth keeping.

## Decision

Deferred to the owner. On the measurement above:

- **`Sessions/` does not move.** §2 and the mechanical matrix forbid it twice, no
  cycle justifies it, and 11 of 14 files are Presentation-bound. Making it legal would
  mean either re-classifying eight `Ui.Framework.*` rows or writing a
  `DocumentedExceptions` reason that contradicts §2's own sentence.
- **No interface is introduced.** `IPanelSessionGateway` is the extracted session
  port already; the other three names in the acceptance criteria do not exist, and
  writing them is a new axis under the #555 freeze.
- **Nothing is deleted.** 0 of 14 types are dead.

If the intent behind the issue was to *shorten the distance from session code to the
composition root* rather than to re-label the folder, the measured seam that would do
it is `IPanelSessionGateway` — it is the one place where a session fact crosses into
a lower layer, and widening it is a presentation decision, not a project move.

## Consequences

- #33's task list can mark this slice as **measured and declined** with this record
  as the link, instead of leaving three unchecked boxes that describe work the tree
  forbids.
- One real defect was fixed on the way: a swallowed exception that made a path-pinned
  guard report a plausible zero. Red-first, with the CI log in PR #1021.
- No guard was added for the layer question itself. `FullLayerMatrixTests` already
  enforces it at two independent levels, from the IL and from the table; a third rule
  would be a guard counting its own enforcement, which is the #899/#901 shape.
- Answers to #33's questions (a), (b) and (c) are **not** given here. They are
  questions about `Harbor.Ui.Framework`'s published surface and its
  `Harbor.Abstractions` dependency, none of which this slice measured, and guessing
  at them would be the fabrication this record exists to avoid. Question (c) is
  stated by #33 itself to be answered by #33/T5.
- Full measurements: this document. Method and per-file binding table reproducible
  from the three `Sessions/` folders and `FullLayerMatrixTests.cs`'s rows.
