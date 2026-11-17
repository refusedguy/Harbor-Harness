# Contract domains — who uses the two contract roots, and what cutting them costs

**Issue #2113.** The issue asks whether the answer to "the contract roots
have a 103-project blast radius" is to CUT the root into one assembly per
kind of type, or to NARROW the contract (move out what almost nobody uses,
and leave one root). The issue's own decision rule is that the answer is
taken by the numbers of the compile-cost instrument (#2112), not by taste.

This document is the measurement that decides it, and the instrument that
produced it is committed, so the same numbers can be taken again after any
change.

Measured on `dev` @ `a55ea18c` (114 projects of `Harbor.slnx`). The
census is byte-for-byte the same on the tree the issue measured on: the
contract roots and their consumers did not move.

The short answer, measured on this tree:

| question | answer |
|---|---|
| Does splitting the root shrink its blast radius? | **No. It grows it.** `Harbor.Abstractions.Contracts`: 103 → 108 projects at the narrowing cut, 103 → 110 / 112 / 115 / 118 at a cut of 3 / 5 / 8 / 12 domains. `Harbor.Abstractions`: 96 → 98 / 103 / 101 / 102 / 104. |
| Why? | The types of a domain name the types that stay. The new assembly has to reference the root (and inherits its radius), or the root has to reference the new assembly (and grows it). Measured: only **10 of 114** consumer groups of `Contracts` and **44 of 114** of `Abstractions` have a cut that crosses no dependency. |
| Is the division by directory a division by domain? | **No.** The `Models/` folder of `Contracts` holds 82 types with **60 different consumer sets**; the widest of them is 42 projects. A `Models` assembly would have the union of all of them. |
| What does the narrowing buy? | `Contracts`: 12 of 170 types become cheap (a change in them rebuilds fewer projects than the root does today), for 10 new assemblies. `Abstractions`: 62 of 164 types, for 44 new assemblies. |
| What does one more assembly cost? | +1 line in the layer matrix, +1 row in the layer table, +1 entry in each of the 3 assembly lists of the layer rules, the moved types' rows in the frozen public-API baseline, and **+1 compilation in the architecture sweep**: 73.0 s / 64 compilations = **1.14 s a pass, 4.57 s over the 4 passes**. |
| What to do first? | Nothing in the build. The cheapest step is the one this document ships: the census and its snapshot. The next step is the #2112 measurement after #2107 (incrementality) lands. Only then does a build change have numbers to justify it — and then it is ONE domain, not N. |

## The instrument

`tools/gates/contract-domains/` — one subcommand of the compiled gate
binary, standard library Go, no build and no SDK needed:

```bash
tools/bin/harbor-gates-linux-x64 contract-domains            # the measurement
tools/bin/harbor-gates-linux-x64 contract-domains --json out.json
tools/bin/harbor-gates-linux-x64 contract-domains --write tools/contract-domains-baseline.txt
tools/bin/harbor-gates-linux-x64 contract-domains --check tools/contract-domains-baseline.txt
tools/bin/harbor-gates-linux-x64 contract-domains --self-test
```

The committed snapshot (`tools/contract-domains-baseline.txt`) is the
"before" of the comparison the issue asks for; `--check` is the "after",
and it goes red when the census moved.

The compile-cost baseline (`tools/compile-cost-baseline.txt`) is OPTIONAL:
it holds the seconds the compiler spent on every project, and with it the
gate prices the radii. It lands with the compile-cost instrument of #2112
(PR #2151), so on a tree without it the gate measures the consumer COUNTS
and says that the prices are unavailable. The gate does not import the
instrument's packages: it reads the baseline file itself, so that it runs
on a tree that does not have the instrument yet.

**How the census is measured.** For every type of the two roots, the
instrument counts the projects of the solution whose code NAMES the type.
The count is a fact about the sources, not a guess about the compiler: a
project uses a type when its code names it. Three things the scan gets
right, and how:

- **Comments and string literals are stripped** before the scan. A type
  named in a comment or in a string literal is not a use.
- **Five names in this tree are declared twice** (`None`, `Session`,
  `Auto`, `HarborPaths`, `IProcessHandle`). For those the mention is
  resolved per FILE by the namespaces the file imports: a file that imports
  the contract's namespace and not the other one's means the contract's
  type; a file that imports both is reported as ambiguous and not counted
  (24 files are ambiguous for `None`).
- **A project compiles the files under its own directory** (minus `bin/`,
  `obj/` and the directories of the projects nested in it), plus the files
  its project file links in, minus the files it removes. The project files
  of this tree write their paths with backslashes, and they are read on
  linux; the paths are normalized before they are resolved.

**What the scan cannot see**, and therefore does not claim: generic
instantiations that the source does not name, implicit conversions, and a
member whose name is the same as a type's name (the dependency figures
below are CANDIDATES: a name in a file, not a resolved symbol).

## The census

Measured on this tree (`Harbor.slnx`, 114 projects):

| root | types | files | lines | direct consumers | transitive consumers | radius price |
|---|---:|---:|---:|---:|---:|---:|
| `src/Harbor.Abstractions.Contracts` | 170 | 48 | 9 966 | 18 | **103** | 143.0 s † |
| `src/Harbor.Abstractions` | 164 | 71 | 8 199 | 61 | **96** | 134.4 с † |

The consumer counts are measured on this tree (`dev` @ `a55ea18c`, 114
projects) by the instrument above. They are the same numbers the issue
quotes for the tree it measured on, except the direct referencers of
`Contracts`: 18 here against 19 there, because
`tests/Harbor.E2E.Framework.Tests` no longer references the root (it was
split out in #1812/#2160).

† **The radius price is measured, but not on this tree**: it is the
compile-cost baseline's own number (the serial compile of 2026-10-20), and
that baseline lands with the compile-cost instrument of #2112 (PR #2151).
The gate reads the baseline when it is in the tree and says so when it is
not — on a tree without it, the CONSUMER COUNTS are measured and the
PRICES are unavailable.

The groups of types by consumer set — the raw material of the domains:

| root | groups | groups with ≤ 5 consumers | ≤ 10 | ≤ 20 | groups with a clean cut |
|---|---:|---:|---:|---:|---:|
| `Contracts` | 114 | 39 | 69 | 95 | **10** (12 types) |
| `Abstractions` | 114 | 61 | 98 | 107 | **44** (62 types) |

## What a domain is, and what is not

A **domain** is a set of types with a set of consumers of its own. The
criterion is mechanical: two types with the same consumer set are one
group, and two groups belong to the same domain when their consumer sets
are similar (Jaccard ≥ 0.75, complete linkage: every pair inside a domain
is similar).

Three things are **not** a domain, and the instrument says which is which:

1. **A type everybody uses.** The root's biggest group (`ToolName`, 57 of
   114 projects) cannot be separated: the assembly it would move into has
   the same consumers as the one it left, so the radius would not shrink by
   one project.
2. **A directory.** The issue's hypothesis is that `Contracts` divides into
   `Models` / `Events` / `Contracts` / `Identifiers` by its directories. The
   measurement says the directories are not domains:

   | folder of `Contracts` | types | users | widest consumer set | distinct consumer sets |
   |---|---:|---:|---:|---:|
   | `Models/` | 82 | 76 | 42 | **60** |
   | `Events/` | 35 | 51 | 38 | **34** |
   | `Permissions/` | 20 | 42 | 26 | **19** |
   | `Sessions/` | 13 | 10 | 9 | 11 |
   | `Models/Identifiers/` | 10 | 78 | 57 | 10 |
   | `Env/` | 1 | 6 | 6 | 1 |
   | `Extensions/` | 1 | 2 | 2 | 1 |

   A folder with one consumer set is a domain; a folder with sixty is a
   directory. `Abstractions` is the same: `Sessions/` holds 41 types with
   30 distinct consumer sets, `Tools/` 23 types with 21.

3. **A group whose types name the types that stay.** This is the finding
   that decides the issue, and it is what the next section measures.

## The cut: which domains can move out

A cut is **clean** when neither side depends on the other:

- the moved domain's types must not name a type that stays in the root —
  otherwise the new assembly references the root, and the new assembly's
  radius is AT LEAST the root's radius;
- the types that stay must not name a moved type — otherwise the root
  references the new assembly, and the root's radius grows by the moved
  domain's consumers.

A cut that is not clean moves the cost around; it does not remove it. And
the measurement says the contract roots are one knot:

| root | groups | clean cuts | types in them | groups that name the types that stay | groups the types that stay name |
|---|---:|---:|---:|---:|---:|
| `Contracts` | 114 | **10** | 12 of 170 | 104 | 0 |
| `Abstractions` | 114 | **44** | 62 of 164 | 70 | 0 |

The ten clean cuts of `Contracts`, narrowest first, with the number of
projects that use them:

| domain | types | consumers |
|---|---:|---:|
| `StringTruncation` | 1 | 2 |
| `UsdCell` | 1 | 2 |
| `DeferredContextMetadata` | 1 | 4 |
| `WorkspaceLimits` | 1 | 4 |
| `HarborEnv` | 1 | 6 |
| `ContextUsage` | 1 | 8 |
| `DiagnosticIssue` | 3 | 8 |
| `ProviderErrors` | 1 | 10 |
| `ModelRef` | 1 | 11 |
| `AgentName` | 1 | 41 |

(The 44 clean cuts of `Abstractions` are listed in the instrument's output;
the narrowest are `IHarborIdeClient` (4 types, 1 consumer),
`ITerminalPaneLauncher` (2, 1), `LlmToolResultBlock` (1, 1), `QrMatrix`
(1, 1), `HeuristicTokenEstimator` (1, 2), `IFileTreePolicy` (1, 2),
`ITerminalPane` (2, 2), `ITextFileStore` (1, 2), `JsonArgValidator` (3, 2).)

## The lever: who holds the radius

The radius of a root is held by its DIRECT referencers: every project that
reaches the root through one of them inherits the radius. Taking one edge
away and measuring what is left:

| root | radius | the referencer that holds the most | radius without it | the rest |
|---|---:|---|---:|---|
| `Contracts` | 103 | `Harbor.Abstractions` (the facade) | 74 | 17 referencers, 1–3 projects each |
| `Abstractions` | 96 | `Harbor.Telemetry.Core` | 93 | 60 referencers, 1 project each |

Two things follow. First, there is no single edge that removes the radius:
for `Contracts`, the facade holds 29 of the 103 projects and the other 17
referencers hold the other 74, and the sets overlap. Second, the facade is
the biggest single holder — and it holds it because its 43 interface
signatures name 43 of the root's types. That is the lever, and it is a
lever on the facade's 43 types, not on the root's 170.

## The price of one more assembly

Measured from the tree, not asserted:

| what | how much | where |
|---|---|---|
| layer matrix | +1 line | `docs/ARCHITECTURE_LAYERS.md` (the matrix has 32 assembly lines) |
| layer table | +1 row | `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs` (110 rows) |
| assembly lists | +1 entry in each of 3 lists | `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs` (3 lists, 83 entries) |
| CI project lists | **+0** | `tools/ci-project-lists.json` names 38 TEST projects and no product assembly; a new TEST project for the new assembly would need a row |
| frozen public API | the moved types' rows move with them | 3 167 frozen rows in the two roots (`PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt`) |
| contract dependency gate | +1 project in its scope | `tools/gates/check-abstractions-contract` guards 5 projects by name |
| seam graph | regenerate | `docs/SEAM_GRAPH.md` is generated from the tree |
| architecture sweep | **+1 compilation** | 64 compilations, 73.0 s a pass, 4 passes → **+1.14 s a pass, +4.57 s over the 4 passes** |
| `using` churn | **0** if the namespaces stay | 220 files name a moved type of `Contracts` (334 of `Abstractions`); with the namespaces preserved (what the contract split did, ADR-007) no file changes |

The `using` churn is an ESTIMATE of the upper bound: it counts the files
that name a moved type, which is what the churn would be if a moved domain
got a namespace of its own.

## The table: N domains

Each row is a cut of one root into N domains, measured on the re-wired
graph. "Cheap types" are the types whose change rebuilds fewer projects
than the root's radius does today. "Win" is the change of the worst
domain's radius against the previous row of the same root.

| root | cut | new assemblies | root radius before → after | worst radius | median radius | cheap types | +ProjectReference edges | sweep |
|---|---|---:|---|---:|---:|---:|---:|---|
| `Contracts` | 3 domains | 2 | 103 → **110** | 110 | 106 | 0 of 170 | 129 | +2 compilations, ~2.3 s |
| `Contracts` | 5 domains | 4 | 103 → **112** | 112 | 108 | 2 of 170 | 130 | +4, ~4.6 s |
| `Contracts` | 8 domains | 7 | 103 → **115** | 115 | 111 | 7 of 170 | 151 | +7, ~8.0 s |
| `Contracts` | 12 domains | 11 | 103 → **118** | 118 | 114 | 9 of 170 | 168 | +11, ~12.6 s |
| `Contracts` | **narrowing** (the 10 clean cuts) | 10 | 103 → **108** | 108 | 50 | **12 of 170** | 169 | +10, ~11.4 s |
| `Abstractions` | 3 domains | 2 | 96 → **103** | 103 | 103 | 10 of 164 | 53 | +2, ~2.3 s |
| `Abstractions` | 5 domains | 4 | 96 → **101** | 101 | 13 | 15 of 164 | 53 | +4, ~4.6 s |
| `Abstractions` | 8 domains | 7 | 96 → **102** | 102 | 14 | 23 of 164 | 57 | +7, ~8.0 s |
| `Abstractions` | 12 domains | 11 | 96 → **104** | 104 | 32 | 39 of 164 | 77 | +11, ~12.6 s |
| `Abstractions` | **narrowing** (the 44 clean cuts) | 44 | 96 → **98** | 98 | 24 | **62 of 164** | 210 | +44, ~50.3 s |

The price in seconds is a PROJECTION for the own cost of an assembly that
does not exist yet (the root's measured seconds divided among the domains
by their lines). The consumer counts and the consumers' seconds are
measured. On a tree without the compile-cost baseline (the tree this
document was written on) the price column reads `n/a`: the consumer
counts do not need it, the prices do.

**The threshold.** The win of one more domain is the number of cheap types
it buys. For `Contracts`: 3 → 5 domains buys 2 cheap types for 2
assemblies; 5 → 8 buys 5 for 3; 8 → 12 buys 2 for 4. Below one cheap type
per assembly the domain costs more than it saves — and the sweep cost is
1.14 s a pass for every one of them. For `Abstractions` the ratio is
better (5 → 8 buys 8 cheap types for 3 assemblies) but 44 assemblies cost
50.3 s a pass, which is 69 % of the sweep's own pass.

**Why the radius grows.** The cut adds projects to the root's radius: the
new assembly has to reference the root (the moved types name the types
that stay), and the projects that used a moved type now reach the root
THROUGH the new assembly. At the narrowing cut of `Contracts` the five
projects added are `Harbor.DesignSystem`, `Harbor.DesignSystem.Tests`,
`Harbor.Desktop.Animations`, `Harbor.Diagnostics.Abstractions` and
`_build`. This is the issue's own caveat, measured: **splitting does not
reduce the blast radius for a consumer until the types are separated by
contract** — and the types of these roots are not separated by contract.

## The recommendation

**Do not split the contract roots, and do not narrow them yet.** Both
options are rejected by the numbers, and for the same reason: the roots'
types are one dependency knot, so a cut inserts an assembly between the
root and its consumers instead of removing one.

| option | what it gives | what it costs | verdict |
|---|---|---|---|
| (a) cut the root into 3–4 projects by directory | nothing: the directories are not domains (60 distinct consumer sets inside `Models/`), and the root's radius grows 103 → 110 | 2–3 new assemblies, +193 rows in the layer rules, ~2.3–3.4 s a pass in the sweep, 2 075 public-API rows moved | **rejected** |
| (a′) cut the root into 3–12 projects by consumer set | 0–9 cheap types for `Contracts`, 10–39 for `Abstractions`; the root's radius grows 103 → 110…118 | 2–11 new assemblies, 129–168 new `ProjectReference` edges, ~2.3–12.6 s a pass | **rejected** |
| (b) narrow the contract: move out what the minority needs, leave one root | 12 cheap types of 170 for `Contracts` (10 assemblies), 62 of 164 for `Abstractions` (44 assemblies); the root's radius still grows (103 → 108, 96 → 98) | 10 or 44 new assemblies, ~11.4 s or ~50.3 s a pass in the sweep | **not now**: the win is real but small, and the root's radius still grows |
| (c) do nothing to the tree, and measure | the numbers above, and a re-runnable instrument | zero | **the first step** |

The order the issue itself sets is right and the numbers agree with it:
**#2107 (incrementality) first**, then the #2112 measurement, then this
decision. Until the build is incremental, a split cannot be measured: every
pull request rebuilds the whole tree anyway, and the radius of a root is
not what the wall is made of.

## The plan

Every step is separately executable and separately checkable.

1. **Ship the instrument and the census (this change).** The gate
   `contract-domains`, its self-test, the snapshot
   `tools/contract-domains-baseline.txt`. Proof: the self-test plants a
   fixture and goes red on a moved snapshot. Cost: no build change.
2. **Wait for #2107, then re-measure.** Run
   `harbor-gates contract-domains --check tools/contract-domains-baseline.txt`
   and `harbor-gates measure-compile-cost` on the same tree, and record the
   radius in `docs/BENCHMARKS.md` with the run number. Proof: the numbers
   are in the document with the run they came from.
3. **If the radius is still the whole tree, try ONE domain — not N.** The
   cheapest clean cut of `Contracts` is `StringTruncation` + `UsdCell`
   (2 consumers each, 1 type each, no dependency crossing). What moves:
   two types into `src/Harbor.Abstractions.Contracts.Small/`. What changes:
   the layer matrix (+1 line), the layer table (+1 row), the 3 assembly
   lists (+1 entry each), the public-API baseline (the 2 rows move), the
   contract-dependency gate's scope (+1 project), the seam graph
   (regenerate), the compile-cost baseline (+1 row). Proof: the
   architecture suite is green, `harbor-gates contract-domains` shows the
   two types in a domain of their own, and the radius of the root is
   measured before and after with the same instrument. Expected cost: one
   assembly, 1.14 s a pass in the sweep, ~220 consumer files with no
   `using` change if the namespaces stay.
4. **Measure the result and stop or continue.** If the root's radius does
   not shrink, the answer is the lever, not the split: the facade's 43
   types. If it does, take the next clean cut, in the order of the
   instrument's table (narrowest first), and re-run step 2's comparison
   each time. Stop at the threshold: below one cheap type per assembly the
   cost beats the win.

## What is not measured

- **The compiler's own view.** The census is by name, not by symbol: a
  generic instantiation the source does not name, an implicit conversion,
  or a member that shares a type's name is invisible to it. The compiler
  would answer the same question exactly, and it is not available here (the
  machine this runs on cannot hold a build of this tree).
- **The wall clock of a split.** No split was made, so no before/after
  wall exists. The radius prices are the compile-cost baseline's seconds
  for the projects in the set, and the own cost of an assembly that does
  not exist yet is a projection (the root's seconds divided among the
  domains by their lines). On a tree without the baseline the prices are
  unavailable altogether.
- **The seconds of the sweep on this tree.** The sweep's 64 compilations,
  73.0 s a pass and 4 passes are the measured numbers of the sweep
  (changelog fragment 2167, arm64, 2026-10-21), not a measurement of this
  tree; the tree has 61 product assemblies (measured), and the three
  compilations that are not product assemblies are not named by anything
  here. The +1.14 s a pass is the sweep's AVERAGE per compilation
  (73.0 / 64): the marginal cost of one more compilation is at least the
  average and probably more, because it is the smallest assembly and the
  sweep's per-compilation overhead does not shrink.
- **The dependency figures are candidates.** A dependency is a name in a
  file. A member whose name is the same as a type's name counts as a
  dependency here and would not be one in the compiler's view, so the
  "clean cut" count is a LOWER bound on the cuts that are clean in
  reality.
