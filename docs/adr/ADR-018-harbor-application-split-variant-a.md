# ADR-018: the `Harbor.Application` split, variant A (the maximal five-assembly plan)

> **Update 2026-10-21:** slice #1625 landed. The `PolicyConfig` cohort's remaining
> part — `Configuration/*` (11 files, 1 693 lines) — is `src/Harbor.PolicyConfig`, an
> Application-layer sibling with its own matrix row, `Harbor.slnx` row and README.
> The two thirds that had already left under other issues (`Harbor.Permissions`,
> #1615; `Harbor.Onboarding`, #1614) stayed where they were. #1626 (`Intelligence`)
> is the last slice still open. The tables below are the snapshot this ADR was
> written with and are left as they were; the current state lives in
> [docs/ROADMAP.md](../ROADMAP.md) and in §5.4's enforced matrix.
>
> **Status (2026-10-20):** dated record, not normative. The decision this file
> records was taken by the owner in a comment on issue #1608 on 2026-10-16; this
> ADR was written on 2026-10-20, at `dev` = `beee95c7`. It asserts no
> machine-checkable fact about the tree as it stands. The state of each slice
> below is the state the slice issues report, and the split itself is still in
> flight. It carries no `Status: normative` banner for that reason: the parts
> that must stay true are fenced where they live (the layer matrix in
> `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs`, the slice issues
> #1623–#1626, and the guard rules they each land).
>
> The issue this records was OPEN when this file was written, with the decision
> already made in its comments. The comment is the sign-off; this file is the
> durable place to point at, so that the next reader does not have to reconstruct
> the reasoning from a comment thread.

## Status

**Decided: variant A**, the maximal plan. All five assemblies named in the
issue are selected, not the incremental `C → B` path the research recommended.
Date of decision: **2026-10-16** (owner comment on
[#1608](https://github.com/refusedguy/Harbor-Harness/issues/1608#issuecomment-6209288178)).
This ADR was written on 2026-10-20; it records the decision, it does not make it.

The exit condition #1608 set for itself was *"Done when: владелец выбрал вариант
комментарием; после этого дочерние assembly-issues разблокируются"* (the owner
picks a variant in a comment; the child assembly issues are then unblocked).
It was met on 2026-10-16.

## Context

Issue #1608 was the decision slice of the `Harbor.Application` decomposition
family. It opened with the two measurements the whole plan rests on, taken from
the project as it then stood:

> **Исследования:** структура (14 областей, 19.5k строк, Sessions 44% + Agents 27%)
> и связность (настоящих циклов 0 — Resilience→Agents using мёртв).
>
> *(Research: structure, 14 areas and 19.5k lines, Sessions 44% and Agents 27%;
> connectivity, no real cycles at all, because the Resilience→Agents using is dead.)*

Three facts follow from that, and the decision is a direct answer to all three:

1. **The project is a god-project, not a god-class.** 14 areas and 19.5k lines in
   one assembly, with 71% of the mass in two of them (Sessions 44%, Agents 27%).
   The plan's unit is therefore still large: the biggest cohort, `AgentRuntime`,
   is ~5.9k lines on its own. That is exactly why the owner wanted the
   boundary to be a physical assembly rather than a convention: at this size, a
   boundary held in the head is a boundary that erodes.
   #1623 adds the second half of the same diagnosis from inside the move
   itself: *"10 общих зависимостей Loop/TurnRunner едут пачкой через
   parameter-object, иначе god переведётся в фабрику"*. Ten shared dependencies
   of `AgentLoop`/`TurnRunner` have to travel as a pack through a parameter
   object, otherwise the god relocates to the factory and the split buys
   nothing.
2. **The connectivity is already acyclic.** Zero real cycles, and the one
   candidate (the `Resilience → Agents` using) is dead code. So the split is
   not blocked by a dependency knot that has to be untangled first. It is
   blocked by nothing but the decision itself.
3. **Every assembly in the plan costs a row in the layer matrix.** Each new
   `src/` project is one `FullLayerMatrixTests` row, one `Harbor.slnx` row, one
   `ProjectReference` edit in the composition root, one DI move, and one README
   under the project README template. That cost is the price of variant A, and
   it is the reason variants B and C existed as alternatives.

What the research recommended, and what the owner decided instead:

> **Рекомендация исследований: C → B, на A только если C окупится.**
>
> *(The research recommendation: C → B, and A only if C pays off.)*

> **РЕШЕНИЕ ВЛАДЕЛЬЦА: вариант A — максимальный (все ~5 сборок сразу, не C→B).
> Причины: быстрее параллельная компиляция, ЯВНЫЕ границы (кодят только агенты —
> границы должны быть физическими, а не в голове), сборки потом можно тащить в
> другие проекты как пакеты. Полумеры отменяются.**
>
> *(Owner's decision: variant A, the maximal plan (all ~5 assemblies at once,
> not C→B). Reasons: faster parallel compilation; explicit boundaries (only
> agents work here, so the boundaries must be physical, not in the head); the
> assemblies can later be pulled into other projects as packages. Half-measures
> are cancelled.)*

The three reasons are the owner's own, and they are the ones this ADR carries
forward. Note that they are **not** the reasoning the research used to prefer
`C → B`. That reasoning is **not recorded in the issues**, and this ADR does
not reconstruct it.

## Decision

**Variant A.** The `Harbor.Application` decomposition is a five-assembly split,
taken as a maximal plan rather than as the incremental `C → B` ladder.

The five assemblies, with the cohort each one owns as the issue names it:

| assembly | cohort | lines | slice issue | state |
|---|---|---|---|---|
| `Harbor.AgentRuntime` | Agents + Pipeline + Resilience | ~5.9k | [#1623](https://github.com/refusedguy/Harbor-Harness/issues/1623) | **MERGED** (PR #1666, commit `5b1ad7bc`) |
| `Harbor.SessionContext` | compaction / prompt / tracker | ~3.1k | [#1624](https://github.com/refusedguy/Harbor-Harness/issues/1624) | **MERGED** (PR #1714, commit `c1ae1f49`) |
| `Harbor.PolicyConfig` | Permissions + Configuration + Onboarding | ~3.4k | [#1625](https://github.com/refusedguy/Harbor-Harness/issues/1625) | **OPEN** |
| `Harbor.Intelligence` | Skills + Hooks + Diagnostics + Git + Fs | ~3.5k | [#1626](https://github.com/refusedguy/Harbor-Harness/issues/1626) | **OPEN** |
| `Harbor.ChangeTracking` | ChangeSetFreezer / CheckRunner / OwnerReport / ChangeReport / WorkspaceMaterializer / FileClaimRegistry / ChangeApplier / … | ~5.6k | [#1616](https://github.com/refusedguy/Harbor-Harness/issues/1616) | **MERGED** (PR #1660) |

The project names in that table are the cohort names the slice issues use,
spelled the way the landed slices spell them (`src/Harbor.AgentRuntime`,
`src/Harbor.SessionContext`). **The issues do not fix the project names for the
two slices that have not landed**. #1625 and #1626 name their cohorts
("PolicyConfig", "Intelligence"), not an assembly path, so the two names in
those rows are this ADR's reading of the pattern, not a recorded decision.

Three things about that table are worth stating plainly, because they are not
visible from the issue titles alone.

**`ChangeTracking` is variant A's fifth assembly, and it is the only one that
was not taken under a `[decomp-max]` slice.** #1616 filed it as *"Первый
assembly-кандидат (вариант C)"*, the first assembly candidate, from variant C,
and it was blocked on the #1608 decision rather than on a #162x slice. It landed
first, under #1616. The `[decomp-max]` series (#1623–#1626) therefore numbers
the remaining four as *часть 1/4 … 4/4*, and the five-assembly plan in the
variant-A table is the union of the two series, not five `[decomp-max]` slices.

**The cohort boundaries have moved since the decision.** The variant-A table was
written against the project as it stood on 2026-10-16. Every one of the five
cohorts has since moved. Two of them moved as their own `[decomp-max]` slice,
three of them partly, by other issues. That shrinks what the remaining slices
still owe:

| cohort | what already left `Harbor.Application` | landed by |
|---|---|---|
| SessionContext | the whole cohort, as `src/Harbor.SessionContext` | #1624 (PR #1714) |
| PolicyConfig | `Permissions/*` → `src/Harbor.Permissions` | #1658 (issue #1615) |
| PolicyConfig | `Onboarding/*` → `src/Harbor.Onboarding` | #1701 (issue #1614) |
| Intelligence | `Hooks/*` → `src/Harbor.Hooks` | #1613 |

So **#1625's remaining cohort is `Configuration/*` alone**, and #1626's
remaining cohort is Skills + Diagnostics + Git + Filesystem + Attachments +
ProviderHealthCheck + Telemetry. #1625's own headline and title still name the
original cohort (`PolicyConfig assembly (Permissions+Configuration+Onboarding
~3.4k)`); the issue was not re-titled when two thirds of its scope left
under other issues. This is recorded here so that a reader does not size the
remaining work from the issue titles.

### What each slice owes, mechanically

The slice bodies fix the mechanics, and they are consistent:

- **One assembly per cohort**, in `src/`, an Application-layer sibling of
  `Harbor.Application`, not a folder inside it. #1623's cohort landed as
  `src/Harbor.AgentRuntime`, #1624's as `src/Harbor.SessionContext`.
- **A matrix row** in `FullLayerMatrixTests` (§5.4 of
  [ARCHITECTURE_LAYERS.md](../ARCHITECTURE_LAYERS.md)), with its allowed-edge
  set, plus the `Harbor.slnx` row and the coverage-guard updates. This is the
  "5 строк матрицы" cost variant A names.
- **The DI move** into the composition root: #1625 names it explicitly
  (*"перенос … DI в ConfigurationModule/IntelligenceModule"*), and #1658 landed
  its shape: the composition root registers the new assembly, and the
  Application side realizes the seam through an adapter.
- **The tests move with the code**, and the per-project README follows the
  template gate (`ReadmeCoverageTests`).
- **The seams are cut before the move.** Every extraction in this family
  introduces the seam first and moves the code second: #1610 (`IWorkspacePaths`,
  killing a 42-reference hub), #1612 (`ICompactionView`, taking `CompactionPolicy`'s
  statics behind an interface), #1615 (the single `IConfigStore` seam), #1614
  (the one-way leaf).

## Alternatives considered

The three variants and their recorded trade-offs are the issue body's own. The
**plus** and **minus** lines are quoted from it, and nothing in them is
attributed to anyone beyond the issue.

### Variant B: stability, 3 assemblies

`Runtime 10k / Policy 3.4k / Services 6k`.

- **Plus:** 3 matrix rows.
- **Minus:** the `ChangeTracking` coma does not decompose. That is the issue's own
  wording, *"ChangeTracking-комок не расщепляется"*.
- **Not recorded in the issues:** why the research considered a three-assembly
  split to be "stability" as opposed to the other two shapes, and what the
  `Runtime 10k` / `Services 6k` boundary was drawn against. The cohort names
  are in the issue; the reasoning behind the cut is not.

### Variant C: minimum

Only `ChangeTracking 5.6k` + optionally `Configuration 1.5k`.

- **Plus:** 1–2 matrix rows; easy to roll back.
- **Minus:** *"вопрос вернётся через полгода"*, the question comes back in six
  months.
- **Not recorded in the issues:** the rollback plan itself. "Легко откатить" is
  stated as a property of the shape, but no rollback procedure is written down
  anywhere in the issue.

### Why A, and what the record does not say

The owner's three reasons are in the decision comment quoted above: faster
parallel compilation, physical boundaries, and the assemblies being reusable as
packages in other projects later. That is the whole of the recorded rationale
for A.

Two gaps, stated rather than filled:

- **The research's reasoning for preferring `C → B` is not recorded in the
  issues.** The recommendation itself is there; the argument behind it is not,
  and this ADR does not invent one.
- **The issues do not record an explicit acceptance of variant A's own stated
  costs**: *"5 строк матрицы, DI-переезд, ослабление доктрины sibling-проектов"*
  (5 matrix rows, a DI move, a weakening of the sibling-project doctrine). The
  decision comment gives three reasons and cancels half-measures; it does not
  say what the owner decided about these three costs. They are the visible
  price of A either way, and the slice issues carry them as work items.

## Sequencing

The DAG the issues state, in order:

```
#1609  dead usings + SkillProvider namespace      (free DAG, CLOSED, PR #1633)
   ↓
#1616  ChangeTracking assembly ~5.6k               (blocked by the #1608 decision, CLOSED, PR #1660)
   ↓
#1623  AgentRuntime assembly ~5.9k                 (precondition #1609; order: after #1616, MERGED, PR #1666)
   ↓
#1624  SessionContext assembly ~3.1k               (preconditions #1610, #1612, MERGED, PR #1714)
   ↓
#1625  PolicyConfig assembly ~3.4k                 (OPEN)
   ↓
#1626  Intelligence assembly ~3.5k                 (OPEN)
```

The stated preconditions, verbatim from the slice bodies:

- **#1609** is *"Дешёвый шаг перед любым разрезом"*, a cheap step before any
  cut: two dead `using`s and one namespace move (`SkillProvider` into
  `Harbor.Application.Skills`, so that a namespace-based cut would not miss it).
  Its own body calls it *"ДAG free"* and *"Риск 0"*.
- **#1616** is blocked by the #1608 decision itself (*"Заблокировано решением
  #1608"*), and it lands the pipeline over `Resilience`/`Abstractions` only.
- **#1623** names #1609 as its precondition and orders itself after
  ChangeTracking (#1616).
- **#1624** names two preconditions, both since closed: #1610 (`IWorkspacePaths`,
  which kills the 42-reference `WorkspaceMaterializer` hub in one move) and #1612
  (`ICompactionView`, `CompactionPolicy`'s statics behind an interface, which
  is what detaches Agents from Sessions).
- **#1625** lists no precondition of its own; it says *"Самая стабильная
  сборка — застабилизировать первой"*, the most stable assembly, stabilize it
  first, which sits in tension with its *часть 3/4* numbering. The issues do
  not reconcile the two.
- **#1626** says *"Листья — выносятся первыми, разблокируют остальное"*, the
  leaves go first and unblock the rest. Its named leaves are the ones already
  extracted (#1613 `Hooks`), so what is left of that cohort is the tail.

One sibling in the same family is still open and is **not** listed as a
precondition of any slice: **#1611** (`DefaultSessionContext` → its own file).
Its body still points at `src/Harbor.Application/Agents/DefaultAgent.cs`, a
path that moved to `src/Harbor.AgentRuntime/Agents/DefaultAgent.cs` when #1623
landed. That staleness is recorded in the issue, not fixed by this ADR.

## Standing rules this plan runs under

The plan is a refactor, and #555's sequencing rule (*"Write the guard BEFORE
the rewrite"*) applies to every slice of it: a refactor without a non-vacuous
guard gets silently undone by the next PR. The slices already honour it (each
lands its matrix row and its seam in the same change as the move).

The freeze on **new extension axes** itself was **lifted by the owner on
2026-10-20** (#555, closing comment). The lift does not lift the rules the
freeze was carrying; what stays in force is stated in that comment:

1. **Guard before rewrite.** Unchanged.
2. **A new axis must be additive and registered**: a marker interface (what a
   contributor implements) **and** a door (`Register*` on `IPluginLoadHost`) that
   is actually dispatched. *"a door nobody calls is exactly the half-open shape
   #620 found, and a marker with no door is a promise the host never keeps."*
   #620 is where the half-open detection came from: it found two axes
   (`RegisterSessionStore`, `RegisterTuiBackend`) that were declared, wired
   into the registries, and never called.
3. **The hand-maintained-list discipline stands**: adding an axis must not add a
   second, drifting copy of the axis list. That was the freeze's real cost.
4. AOT rules (#626) and the closed `ITuiPlugin` seam (#564) are their own
   decisions and are unaffected by the lift.

The successor rule that makes (2) machine-enforced is **#1719**
(`ExtensionAxisFreezeRule` → "axes are additive and registered"), and it is
**still OPEN**. Until it lands, the code still refuses an eighth axis
(`tests/Harbor.Architecture.Tests/ExtensionAxisFreezeRule.cs`, 848 lines), and
the "additive + registered" rule is a **policy**, enforced by review rather
than by the build. The freeze was lifted as policy and the code follows in its
own PR. That PR is #1719, and it is not part of this split.

Note the boundary this puts around the split itself: an assembly split is not a
new extension axis. Nothing here adds a marker interface, a `Register*` door, or
a second hand-maintained axis list. The split adds *projects*, and the cost of
a project is a matrix row, which is exactly the cost variant A's own minus
column names.

## Definition of done

**For #1608 itself (the decision issue):** the owner picks a variant in a
comment, and the child assembly issues are then unblocked. Met on 2026-10-16;
the issue is closed on that basis.

**For "the `Harbor.Application` split is done":** the issues do not state one
combined exit condition. Each slice states its own scope, and no issue says
"the split is done when …". Assembled from the four `[decomp-max]` slice bodies
and the extractions they name, the split is done when:

1. **Every cohort in the variant-A table has left `Harbor.Application`**. As
   of this writing, `Harbor.AgentRuntime` (merged), `Harbor.SessionContext`
   (merged) and `Harbor.ChangeTracking` (merged) have; #1625 and #1626 have not.
2. **Each of them is a real project, not a folder**: `src/<Assembly>`, an
   Application-layer sibling, with its own `ProjectReference` edges.
3. **Each has its matrix row**, its `Harbor.slnx` row, its coverage-guard
   entry, and its per-project README under the template gate.
4. **The DI move is done**: the composition root registers the new assembly and
   the Application side realizes the seam through an adapter (#1658's
   `PermissionConfigStoreAdapter` is the shape the family uses).
5. **The tests moved with the code**, and CI is green on the slice.
6. **The guard landed with the rewrite**: no slice lands a move without its
   matrix row and its seam (the #555 rule).

## Caveats, recorded honestly

- **The measurements behind the decision live only in the issue body.** The
  "14 areas / 19.5k lines / Sessions 44% + Agents 27%" figures and the cohort
  line counts (~5.9k, ~3.1k, ~5.6k, ~3.4k, ~3.5k) are quoted from #1608 and
  the slice issues. No committed measurement document backs them, and they
  describe the project as it stood on 2026-10-16. Three extractions later, they
  are already stale.
- **The cohort boundary is moving, and the slice titles do not follow it.** See
  the table above: #1625's headline still names Permissions and Onboarding,
  both of which have already left under other issues. The remaining work in
  #1625 is `Configuration/*`, and in #1626 it is the tail after the leaves.
- **The "additive + registered" rule is not yet machine-enforced.** #1719 has
  to land for that. Until it does, the rule that keeps a new axis honest is
  enforced by review, not by the build.
- **#1623's own precondition is a warning about how this can go wrong.** Ten
  shared dependencies travelling as a pack through a parameter object is the
  difference between a split and a god that moved to the factory. The other
  slices have the same exposure and no recorded equivalent note.
- **The two preconditions #1624 names were closed before the slice ran, and the
  slice has now landed** (#1610, #1612, then PR #1714). What #1624 shows about
  the plan's cost is the two files it had to move beyond the issue's list
  (`CompactionView`, `SkillProvider`): keeping either behind would have made
  `Harbor.Application` bind a moved type, and the cohort already binds
  Application's own types, so the two edges together were a project-reference
  cycle. The issue body does not name either file.
- **Two of the five assemblies are named by nobody yet.** `Harbor.PolicyConfig`
  and `Harbor.Intelligence` are this ADR's reading of the pattern the two landed
  slices set; the issues name cohorts, not assembly paths.

## References

- [#1608](https://github.com/refusedguy/Harbor-Harness/issues/1608): the
  decision issue; body carries the research and the three variants, the
  closing comment carries the owner's decision.
- [#1623](https://github.com/refusedguy/Harbor-Harness/issues/1623),
  [#1624](https://github.com/refusedguy/Harbor-Harness/issues/1624),
  [#1625](https://github.com/refusedguy/Harbor-Harness/issues/1625),
  [#1626](https://github.com/refusedguy/Harbor-Harness/issues/1626): the four
  `[decomp-max]` slices.
- [#1609](https://github.com/refusedguy/Harbor-Harness/issues/1609),
  [#1610](https://github.com/refusedguy/Harbor-Harness/issues/1610),
  [#1611](https://github.com/refusedguy/Harbor-Harness/issues/1611),
  [#1612](https://github.com/refusedguy/Harbor-Harness/issues/1612),
  [#1613](https://github.com/refusedguy/Harbor-Harness/issues/1613),
  [#1614](https://github.com/refusedguy/Harbor-Harness/issues/1614),
  [#1615](https://github.com/refusedguy/Harbor-Harness/issues/1615),
  [#1616](https://github.com/refusedguy/Harbor-Harness/issues/1616): the
  seams and the fifth assembly.
- [#555](https://github.com/refusedguy/Harbor-Harness/issues/555): the freeze,
  its sequencing rule, and its 2026-10-20 lift.
- [#620](https://github.com/refusedguy/Harbor-Harness/issues/620): the
  marker-and-door axis definition and the half-open detection.
- [#1719](https://github.com/refusedguy/Harbor-Harness/issues/1719): the
  successor rule for "axes are additive and registered" (OPEN).
- [ARCHITECTURE_LAYERS.md](../ARCHITECTURE_LAYERS.md) §1 and §5.4: the layer
  diagram and the matrix every new assembly adds a row to.
- [DECISIONS.md](DECISIONS.md): the ADR log.
