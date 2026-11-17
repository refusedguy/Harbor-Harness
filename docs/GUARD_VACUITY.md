# Can each guard fail? The vacuity sweep of issue #1730

> Status (2026-10-22): an audit, re-run on the tree this branch merges into (`dev` at
> `a55ea18c` plus #1725 and #1730). The first sweep was made on the tree at `550b5969`
> (2026-10-21); every self-test in the table below was **executed** again on the merged tree,
> and its verdict recorded. A dated record, not a normative claim about the current tree. Every
> "умеет падать" verdict below was **executed**, not read: the self-test of every gate in scope
> was run locally and its verdict recorded in the table.

Issue #1730 asks one question about every guard: **can it fail?** A guard that
cannot fail is not a guard — it is a green check that checks nothing, and it is
worse than no guard, because it is believed.

The answer has three parts:

1. **The gates.** Every gate in the architecture and security perimeter has a
   self-test that plants a violation of its own invariant and asserts the gate
   reports it. The table below is that inventory, with the run behind each row.
2. **The architecture guards.** Of the **160** guards in
   `tests/Harbor.Architecture.Tests/`, **152** declare a positive control or a
   discovery floor — a mechanism that proves the guard's matcher can report a
   violation. **8 declare neither** and are listed with the control each one is
   missing.
3. **The gap that was closed.** The banned-API suppression catalogue had no
   check at all, and the tree had drifted. A new compiled gate
   (`banned-api-exemptions`) now holds it, and it found two drifts on the tree
   it landed on — both fixed in the same commit.

## What "can fail" means here

A guard proves it can fail by one of three mechanisms, and the audit counts all
three:

| mechanism | what it is | why it is enough |
|---|---|---|
| a **positive control** | a planted offender the guard must report — a synthetic source, a fixture tree, a deliberately broken file | the matcher is driven by the real code path and its verdict is asserted |
| a **discovery floor** | a minimum the guard's own subject must meet — `MinHits`, a floor on the number of suppressions, a floor on the number of rows | a subject that vanished cannot satisfy the floor, so an empty walk reads as red, not as green |
| a **self-test** (for the gates in `tools/`) | a fixture tree with a real violation of the gate's invariant planted in it | the gate is executed against the fixture and the finding is asserted |

A gate that has none of the three is **vacuous**: it will pass on a tree where
its matcher stopped matching, which is the failure mode every gate in this
repository is written against.

## The gates

The perimeter of this audit is the architecture gates and the security gates.
Every self-test in the table was run on the merged tree; the verdict
column is the exit code and the tool's own last line.

### The architecture gates

| gate | умеет падать? | чем доказано | чего не хватает |
|---|---|---|---|
| `tests/Harbor.Architecture.Tests/` (160 guards) | **partly — 152 of 160** | 152 guards declare a positive control (`ScanRule.Controls`, a planted fixture, a control test) or a discovery floor | the 8 guards that declare neither: see [the eight](#the-eight-that-declare-neither) below |
| `tools/check-arch-anchors.py` | yes | `--self-test` plants a `file:line` anchor inside an architecture rule and asserts the gate reports it (rc=0, "4 checks passed") | — |
| `tools/check-arch-suite-once.py` | yes | `--self-test` plants ten workflow defects (a duplicate shard, a missing gate switch, no shard at all) and asserts each fires (rc=0, "still fails on every planted defect") | — |
| `tools/bin/harbor-gates-linux-x64 check-abstractions-contract` | yes | `--self-test` plants a phantom package and an empty declaration list and asserts both fire (rc=0, "16 fixtures") | — |
| `tools/bin/harbor-gates-linux-x64 gen-seam-graph` | yes | `--self-test` plants a stale document and asserts the gate reports it (rc=0, "the self-test passed") | — |
| `tools/check-result-chains.py` | yes | `--self-test` plants every guard shape the census looks for and asserts each is found and told apart (rc=0) | — |
| `tools/check-string-switch.py` | yes | `--self-test` plants a string switch and asserts the gate reports it (rc=0, "still finds a planted string switch") | — |
| `tools/guard-triage.py` | yes | `--self-test` plants a failure and asserts the triage reads it (rc=0) | — |
| `tools/ci-shard-slices.py` | yes | `--self-test` plants a desync in each of the places that names a test project (rc=0, "15 fixtures") | — |
| `tools/check-gate-policy.py` | yes | `--self-test` plants one defect per rule of the policy and asserts each raises its rule (rc=0, "20 planted defects") | — |

### The security gates

| gate | умеет падать? | чем доказано | чего не хватает |
|---|---|---|---|
| the banned-API ban, compiler half (RS0030 over `BannedSymbols.txt`) | yes | `BannedApiProbe` runs the **build's own analyzer** in-process over a planted `System.Reflection.Assembly.Load` and asserts it fires; the rule caught a real hole in the ban file in run 39395794130 | — |
| the banned-API ban, **catalogue half** (the `#pragma warning disable RS0030` exemptions) | **no — was not checked** | nothing: the catalogue is prose in a comment and no gate read it | **closed by this change**: `tools/gates/banned-api-exemptions` compares the catalogue with the tree in both directions and in count. It found two drifts: a stale row (`RegistriesModule.cs`) and a file with two suppressions under one row (`SettingsViewModel.cs`) |
| `tools/check-test-packages.py` | yes | `--self-test` plants a banned package directly, a banned package arriving transitively, a banned assembly in the output tree, a stale exception and an unjustified one (rc=0, "19 fixtures") | the **product** projects' package graph: the gate's scope is the test and sample projects, so a banned package that reaches a product project transitively is not covered. Proposed below |
| `tools/bin/harbor-gates-linux-x64 check-public-api-resign` | yes | `--self-test` plants an unre-signed declaration, a baseline edit with no code behind it and a row written twice (rc=0, "self-test PASS") | — |
| `tools/bin/harbor-gates-linux-x64 check-reference-trimmer` | yes | `--self-test` plants an uncarried finding, a stale baseline row and a row without a reason (rc=0, "self-test OK") | — |
| `tools/bin/harbor-gates-linux-x64 check-analyzer-scope` | yes | `--self-test` plants a family that spread back to every project, a family with no version pin and a table row with no declaration (rc=0, "12 fixtures") | — |
| `tools/check-tunit-analyzer-gate.py` | yes | `--selftest` plants an offender and a waiver and asserts each behaves as it must (rc=0) | — |
| `tools/bin/harbor-gates-linux-x64 aot-publish-gate` | yes | `--selftest` plants a new diagnostic id, a third-party id and a wrong recipe, and asserts each is rejected (rc=0, "25 cases") | — |
| `tools/bin/harbor-gates-linux-x64 check-no-test-sdk` | yes | `--selftest` plants a `PackageReference`, a `PackageVersion` pin and three spellings of each (rc=0, "11 fixtures") | — |
| `tools/check-test-json-reflection.py` | yes | `--self-test` plants a reflection call and asserts the gate reports it (rc=0) | — |
| `tools/check-tool-schema-pin.py` | yes | `--selftest` feeds the analysis every broken input and asserts each is rejected (rc=0) | — |
| `tools/bin/harbor-gates-linux-x64 check-coverage-registry` | yes | `--self-test` plants a file that stopped being covered and asserts the gate reports it on both sides of the ratchet (rc=0) | — |
| `tools/bin/harbor-gates-linux-x64 check-shard-balance` | yes | `--self-test` plants a runaway shard, a missing cost and a missing shard list (rc=0) | — |

**One gate in the whole table had no proof, and it was a security gate.** Every
other gate in the perimeter already had a self-test that plants a violation of
its own invariant. The catalogue of banned-API suppressions had none: it is a
comment block at the bottom of `BannedSymbols.txt`, and the diagnostic it
records is exactly the diagnostic a suppression switches off — so nothing in
the repository could see it. The tree had drifted: a catalogue row for a
`RegistriesModule.cs` suppression that no longer exists, and a file carrying
two suppressions under one row.

The table names the gates as the merged tree has them: eight of the scripts that
were python when the sweep started are now subcommands of the compiled binary
(#2085, #2115, #2127, #2134), and their self-tests were run through the binary
on the merged tree.

**Three more gates landed in the perimeter while the sweep was open**, and all
three carry the same kind of proof — a self-test that plants the violation of
their own invariant. They were run on the merged tree too:

| gate | умеет падать? | чем доказано |
|---|---|---|
| `tools/bin/harbor-gates-linux-x64 go-quality` | yes | `--self-test` plants an unformatted file, a package the registry does not name and a package without a self-test, and asserts each fires (rc=0, "self-test PASS"). The gate on the live tree also found a real defect in this pull request: the banned-API gate's own self-test was not gofmt-clean |
| `tools/bin/harbor-gates-linux-x64 check-event-topology` | yes | `--self-test` plants a producer row that names a call site the file does not hold (rc=0, "self-test PASS") |
| `tools/bin/harbor-gates-linux-x64 check-workflows` | yes | `--self-test` plants a duplicate key in a mapping and a block sequence's entry at the wrong indent, and asserts each fires (rc=0). It is the gate that would have caught the 2026-10-21 workflow-file failure this pull request inherited |

## The eight that declare neither

Eight of the 160 architecture guards declare **neither a positive control nor a
discovery floor**. Their verdict is derived from the tree, so a matcher that
stops matching reads as green. These are the eight, and what each one is
missing. The merge re-checked the census: none of the eight files changed, and
none of the 152 that declare a control lost one, so the split is 152 and 8 on
the merged tree too.

| guard | what it grades | how it can read green on a broken matcher | the control it needs |
|---|---|---|---|
| `LayerDependencyTests` | the forbidden reference set of every layer | the forbidden set is a written-down table; a table that lost its last row reads as "nothing is forbidden" | a planted Domain → Infrastructure edge, asserted to be reported, and a floor on the size of the forbidden set |
| `NetArchLayerRules` | the same edges, over the NetArchTest DSL | the same | the same pair, over the DSL |
| `AbstractionsSplitLayerRules` | the three contract assemblies' reference sets | the tests read the assemblies and compare; a comparison that stops matching reads as green | a floor that each of the three assemblies was FOUND, plus a planted contract → product edge |
| `FullLayerMatrixTests` | the matrix against the real reference sets | a row whose reference set is compared wrongly reads as green | a planted wrong edge in a copy of the matrix, asserted to be reported |
| `EnforcerIntegrityTests` | the rules about the rules | its liveness checks prove its own tables are accurate; nothing proves the matrix's verdict can be wrong | a planted project reference outside the matrix, asserted to be reported |
| `ReadmeCoverageTests` | every `src/` project has a README | the walk that finds the projects is the walk that finds the READMEs; a broken walk finds nothing and reports nothing | a floor on the number of `src/` projects the walk found — the shape `ScanUniverseRule` already measures |
| `ResultMaybeSignatureTests` | no `Result<T?>` in `src/` | a scan that stops matching reports nothing | a planted `Result<T?>` in a synthetic source, and a floor on the number of files the scan read |
| `ResultTryAdoptionTests` | `try/catch` → `Result` rides `Result.Try` | same; the exemption table's liveness check proves the table, not the matcher | a planted hand-rolled try/catch, and a floor on the number of files the scan read |

Two of the eight are the **layer edges**, and one of them exists twice — the
reflection-based rule and the NetArchTest-based one — so the cheapest repair is
one planted fixture and two assertions.

The scalable repair is a gate over the tree: a gate that reads the
architecture rule files and fails when a guard declares neither a control nor a
floor. It is not written here, and the reason is worth stating: such a gate
decides "does this file MENTION a control" by a text rule, which is the same
kind of instrument that #1725 classified as tier B — bound to form, and
noisy on an innocent rename. A gate that nags is worse than the eight
unguarded rules it would catch, because it turns every refactor of the rule
tree into a red build. The honest form of it is a **convention plus a
review**: the rule tree's own header says what a rule has to carry, and a
guard that does not carry it says so in its own header. That is a reviewer's
job, and it is why this table lists the eight rather than hiding them behind
a heuristic.

## What is still open

**A banned package in a PRODUCT project's transitive graph.**
`tools/check-test-packages.py` reads the banned names out of the same
`BannedSymbols.txt` and fails when a **test or sample** project resolves a
banned package, directly or transitively. The product projects are covered by
two other rules: RS0030 fires when the banned SYMBOL is used in source, and
`tools/check-reference-trimmer.py` fires when a declared reference is used by
nothing. What neither covers is a product project that declares a library
which itself drags a banned package in: the symbol is not used, the reference
is used, and the banned assembly ends up in the published output.

The proposed repair, in the shape of the gates above: one more rule in
`check-test-packages.py`'s scope, or a separate gate that reads the product
projects' `obj/project.assets.json` and fails on a banned name in the
resolved closure. The input is a file the restore already writes, so the gate
belongs in the `build` job of `ci.yml`, next to the other package gate, and
not in the SDK-free workflow.

## How the sweep is reproduced

The self-tests are ordinary commands, and the verdicts above are their exit
codes:

```bash
# one gate
python3 tools/check-arch-anchors.py --self-test
tools/bin/harbor-gates-linux-x64 banned-api-exemptions --self-test

# the gate itself, on the real tree
tools/bin/harbor-gates-linux-x64 banned-api-exemptions --min-exemptions 6

# the architecture guards that declare neither a control nor a floor
# (the list is in this document; the census is the rule tree's own)
```

The **architecture guard** census is a computation over the tree: a guard
proves it can fail when it declares a `ScanRule.Controls` table, a test whose
name carries a control word, a prose control marker, or a discovery floor. The
eight above are what is left over, and each one was read by hand.

## Related

- #1725, the tier classification of the same 160 guards — which ones catch a
  defect and which ones only react to the tree changing shape:
  [docs/ARCH_RULES.md](ARCH_RULES.md).
- #1726, the measured firings: what each guard firing MEANT over 43 CI runs:
  [docs/GUARD_VALUE_MEASUREMENT.md](GUARD_VALUE_MEASUREMENT.md).
- #1888, the policy the gates obey, and the checker that keeps the table
  honest: [docs/CI_GATES.md](CI_GATES.md).
