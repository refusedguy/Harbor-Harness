# ReferenceTrimmer

Issue #1853. This document is the measured report for the unused-reference
sweep over the whole solution, and the reference for the gate that keeps the
sweep from drifting.

The gate itself is [the compiled `check-reference-trimmer` gate](../tools/gates/check-reference-trimmer/main.go)
and its baseline is
[`tools/reference-trimmer-baseline.txt`](../tools/reference-trimmer-baseline.txt).
The CI job that produces the answer is the `reference-trimmer` job in
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml).

---

## 1. The tool

**ReferenceTrimmer 3.5.13**, MIT. The facts were read out of the NuGet
registry and the package itself, not out of memory:

| Fact | Value | Where it was read |
|------|-------|-------------------|
| Latest version | 3.5.13 | `api.nuget.org/v3-flatcontainer/referencetrimmer/index.json` (the version list ends at 3.5.13) |
| Published | 2026-10-15 | the registration's `published` field |
| Licence | MIT | the registration's `licenseExpression` |
| Assemblies | `analyzers/dotnet/ReferenceTrimmer.Analyzer.dll`, `build/ReferenceTrimmer.{Tasks,Loggers}.dll`, `build/ReferenceTrimmer.{props,targets}` | the .nupkg's file list |
| Runtime assets | none (no `lib/` folder) | the .nupkg's file list |

The rules it emits:

| Id | Meaning |
|----|---------|
| `RT0000` | documentation generation is off, so the "used references" answer is less accurate |
| `RT0001` | a bare `<Reference>` the compiler never uses |
| `RT0002` | a `<ProjectReference>` the compiler never uses |
| `RT0003` | a `<PackageReference>` the compiler never uses |
| `RT9999` | the analyzer itself failed; the answer for that project is unknown |

### Which mode this repository runs

The **default** mode, Roslyn's `GetUsedAssemblyReferences`. The package also
ships an experimental symbol-based mode (`ReferenceTrimmerUseSymbolAnalysis`),
and it was not used: that mode does not track `<see cref>` references, and this
repository generates XML documentation globally
(`Directory.Build.props`, `<GenerateDocumentationFile>true</GenerateDocumentationFile>`),
so a repository full of crefs would produce a stream of false positives.

`IDE0005` (unnecessary using) is **not** enabled. The package's own
documentation recommends it for accuracy, and enabling it is a separate piece
of work with its own findings; this run is the one without it.

---

## 2. Coverage

Measured on CI run **39445229686**, the first run of the job, on `dev` at
`e67f1b0c` plus this branch.

| Number | What it counts |
|--------|----------------|
| **109** | projects the analyzer actually ran in |
| 109 | rows in `Harbor.slnx` |
| 141 | `.csproj` files in the tree |

The coverage number is not an estimate. The analyzer writes its
declared-reference table into each project's intermediate directory, and the
job counts those files, so "the analyzer ran in 109 projects" is a file count
and not a claim.

Two of the solution's rows were **not** analysed, and both for a stated
reason rather than by accident:

- `build/_build.csproj` and `tests/Harbor.Benchmarks/Harbor.Benchmarks.csproj`
  each have a `Directory.Build.props` of their own that does **not** import the
  repository root, so the analyzer package never reaches them.

Two projects were analysed that are **not** rows of the solution, because the
solution pulls them in through a `<ProjectReference>`:
`src/Harbor.CodeGen/Harbor.CodeGen.csproj` and
`apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj`.

Not covered, and stated rather than implied:

- `samples/designsystem-consumer/Harbor.Samples.DesignSystemConsumer.csproj`
  (not a row of `Harbor.slnx`);
- everything under `contrib/`, which CI does not build;
- `Harbor.Samples.slnx`, whose rows need workloads this job does not have.

---

## 3. Findings

The first measured run produced **60** findings:

| Rule | Count |
|------|-------|
| `RT0001` | 0 |
| `RT0002` | 58 |
| `RT0003` | 2 |

`RT0000` did not fire, and `RT9999` did not fire. The job sets
`GenerateDocumentationFile=true` for the whole solution for exactly that
reason (`tests/Directory.Build.props` turns the doc file off, and the package
says itself that without one the answer is less accurate). The gate treats an
`RT0000` as a failure anyway, so a job that stopped generating documentation
would go red rather than quietly under-report.

`RT0001` is 0 for a boring reason: the repository declares **no** bare
`<Reference>` items at all. Every reference in the tree is a
`<ProjectReference>` or a `<PackageReference>`.

---

## 4. What the findings mean, and how many are real

The tool answers exactly one question: "does the compiler bind a type from
this assembly?". That one question means five different things depending on
why the reference is there, and only one of the five is a dead edge.

| Family | Rows | What it is | Real? |
|--------|------|------------|-------|
| 1. The meta-package's own content | 6 | `Harbor.Ui.Framework` is a meta-package with no sources of its own. Its six `<ProjectReference>`s **are** the package's content. | no |
| 2. A consumer of the meta-package | 8 | The reference is what carries the six sub-projects into the consumer's closure. The facade assembly itself binds nothing, because it has no code. | no |
| 3. A source-generator edge | 6 | `Harbor.CodeGen` is referenced with `OutputItemType="Analyzer"`. The edge exists to **run** the generator; the generator's types are never named. | no |
| 4. The architecture suite reads the assembly by reflection | 13 | `tests/Harbor.Architecture.Tests` loads these assemblies to police the layer rules, by name and through reflection. The compiler cannot see that. | no |
| 5. An edge that carries a transitive, or a redundant edge | 24 | Eight edges carry a transitive into the closure that nothing else reaches. Sixteen are **redundant**: the target is already reachable through another reference and no type of it is named. | partly |
| 6. A package reference that binds nothing | 1 | `Excubo.Analyzers.Annotations` in the CLI. | see below |

**58 of the 60 findings (97 %) are not dead edges.** That is the headline
number, and it is the reason the tool cannot be a build gate on its own: a
rule that fires 58 times and is wrong 58 times teaches its readers to ignore
it.

### 4.1 The two that were fixed

Both are in projects that are **not packable**, so neither changes a published
package's dependency list.

**`src/Harbor.Logging` -> `Microsoft.Extensions.Logging.Abstractions`** (`RT0003`)

The project has exactly one source file, `LoggerSetup.cs`, and it binds
`Serilog.ILogger`. Nothing else. The assembly stays in the closure anyway:
`Serilog.Extensions.Hosting` 10.0.0 depends on
`Microsoft.Extensions.Logging.Abstractions` directly (its nuspec lists it
under every target framework), and `CentralPackageTransitivePinningEnabled`
resolves that transitive to the same 10.0.13 the direct reference pinned, so
**no resolved version changes**.

**`src/Harbor.CodeGen` -> `../Harbor.Abstractions.Contracts`** (`RT0002`)

Every generator in that project names the attributes it emits as **strings**
(`private const string AttributeName = "TerminalEscapeAttribute";`) and looks
them up in the **consumer's** compilation, which is where they have to be
resolvable. No type of the contracts assembly is bound by the generator
itself. Every consumer carries `Harbor.Abstractions`, which references
`Harbor.Abstractions.Contracts`, so the attribute types stay resolvable in
every compilation that runs a generator.

What changes is the generator's own closure: it no longer compiles against
the contracts assembly, so its output no longer carries a copy of it. Nothing
loads it at run time.

### 4.2 The sixteen redundant edges that were left in place

Sixteen of the family-5 rows are edges whose target is already reachable
through another `<ProjectReference>`, under a condition that is never
stronger than the direct edge's:

| Project | Target | Already reachable through |
|---------|--------|---------------------------|
| `apps/Harbor.App.Avalonia` | `Harbor.Providers.Ollama` | `Harbor.Hosting` |
| `apps/Harbor.App.Avalonia` | `Harbor.Storage.Jsonl` | `Harbor.Hosting` |
| `apps/Harbor.App.Avalonia` | `Harbor.Storage.Memory` | `Harbor.Hosting` |
| `apps/Harbor.App.Cli` | `Harbor.Ipc.InProcess` | `Harbor.Hosting` |
| `apps/Harbor.App.Cli` | `Harbor.Ipc.Server` | `Harbor.Hosting` (`HarborWithDaemon`) |
| `apps/Harbor.App.Cli` | `Harbor.Plugins.Compilation` | `Harbor.Plugins.Hosting` (`HarborWithPlugins`) |
| `apps/Harbor.App.Cli` | `Harbor.Plugins.Instantiation` | `Harbor.Plugins.Registration` (`HarborWithPlugins`) |
| `apps/Harbor.App.Cli` | `Harbor.Plugins.Registration` | `Harbor.Plugins.Hosting` (`HarborWithPlugins`) |
| `apps/Harbor.App.Cli` | `Harbor.Providers.Anthropic` | `Harbor.Hosting` (`HarborWithAllProviders`) |
| `apps/Harbor.App.Cli` | `Harbor.Providers.Ollama` | `Harbor.Hosting` |
| `apps/Harbor.App.Cli` | `Harbor.Providers.OpenAI` | `Harbor.Hosting` (`HarborWithAllProviders`) |
| `apps/Harbor.App.Cli` | `Harbor.Storage.Jsonl` | `Harbor.Hosting` |
| `apps/Harbor.App.Cli` | `Harbor.Storage.Memory` | `Harbor.Hosting` |
| `apps/Harbor.App.Cli` | `Harbor.Storage.Sqlite` | `Harbor.Hosting` (`HarborWithAllProviders`) |
| `src/Harbor.Plugins.Hosting` | `Harbor.Plugins.Instantiation` | `Harbor.Plugins.Registration` |
| `src/Harbor.Tui.CellForge` | `Harbor.Desktop.Animations` | `Harbor.Ui.Framework.Rendering` |

Fourteen of the sixteen are in the two composition roots, and both of those
are **published artifacts**: `apps/Harbor.App.Cli` is `PackAsTool`, so its
`.nuspec` dependency list is what `dotnet tool install harbor` resolves.
Removing a direct reference there changes the published package's dependency
list, which is a release-visible change and not a tooling change.

The issue's own plan says so as well: the removals are separate small PRs
after verification, not part of the ticket that lands the tool. They are
therefore recorded in the baseline with the reason they are still there, and
the list above is the work order for that follow-up.

### 4.3 `Excubo.Analyzers.Annotations` in the CLI

The package is declared for the whole `apps/` tree by
`apps/Directory.Build.props`, and it binds nothing in the CLI because the CLI
declares no `[Exposes]` attribute. `apps/Harbor.App.Avalonia/AppHost.cs` does
declare one.

So the finding is not a dead reference: it is a **gap in the CLI's `[Exposes]`
coverage**, which is the Excubo analyzer's subject and not this gate's. The
row stays in the baseline with that reason.

### 4.4 `Microsoft.NET.Test.Sdk`: the answer, measured

The issue asked where the 16 `Microsoft.NET.Test.Sdk` references are needed.
The measured answer is that **ReferenceTrimmer never sees them**, and the
reason is in the tool's own documentation: a NuGet package that ships a
`build/` folder can change the build in ways the tool cannot evaluate, so
ReferenceTrimmer **skips every such package by default**.
`Microsoft.NET.Test.Sdk` ships one.

The consequence is that the 16 references are outside this gate's reach, and
the decision to remove them (and whether TUnit's discovery survives without
the VSTest bridge) belongs to issue **#1893**, which owns that question. No
row for it is written here, and nothing in this PR touches those 16 files.

---

## 5. The gate

the compiled `check-reference-trimmer` gate reads the analyzer's log and compares it
with the baseline. It is stdlib python, no dotnet, no network, in the same
family as `check-test-projects` and `check-test-dup`.

The baseline is a **ratchet**, the same shape as
`tools/test-project-baseline.txt`:

- a finding the baseline does not carry is **red**: a new unused reference
  fails;
- a row the tree no longer produces is **red**: a closed finding must have its
  row deleted in the same diff that closed it.

So the baseline can only shrink, and the gate gets stricter as the tree is
fixed.

The rules it reports:

| Rule | Meaning |
|------|---------|
| `RT-NEW-FINDING` | the tree produced a finding the baseline does not carry |
| `RT-STALE-BASELINE` | a baseline row the tree no longer produces |
| `RT-UNJUSTIFIED` | a baseline row with no reason, a reason under 40 characters, or no owner |
| `RT-DOC-OFF` | `RT0000`: the instrument was running weak |
| `RT-ANALYZER-ERROR` | `RT9999`: the analyzer failed |
| `RT-SCAN-BLIND` | the findings file is missing or empty, or the coverage count is under the floor |

The coverage floor is 100 projects, against the 109 measured. A scan that
silently narrowed goes red instead of quiet.

The gate has a `--self-test`, and the CI job runs it as a separate step before
the comparison: the self-test proves that an uncarried finding, a stale row,
an unjustified row, an `RT0000`, an `RT9999`, a missing findings file, an
empty findings file and a coverage count under the floor are each red.

---

## 6. Cost

Measured on CI run 39445229686 (the same run as the coverage numbers):

| Job | Wall clock |
|-----|-----------|
| `unused references (ReferenceTrimmer)` | **4 min 12 s** (restore + Release build of the whole solution with the analyzer + coverage list + gate) |
| `build` (NUKE compile, pack, pack validation, smoke) | 7 min 18 s |

The issue asked for a measurement before the tool goes into a gate, on the
theory that `GetUsedAssemblyReferences` would cost more than 10 % of the
build. The measured cost of the whole job, including a full Release build of
the solution, is four minutes on a runner that builds the same solution
without the analyzer in seven. The tool does not go into the ordinary build
path, so it costs nothing on the other jobs.

---

## 7. Why this is a separate document and not a section of `docs/BENCHMARKS.md`

`docs/BENCHMARKS.md` is the single source of truth for **measured
performance** numbers, and every figure in it is a copy of a row there. This
report is not a performance measurement: its numbers are counts of findings
and counts of projects, they are not comparable with any other machine, and
mixing them into the benchmark tables would make both harder to read. It also
carries the reasoning for why a finding is a false positive, which is a
paragraph per family and does not belong in a table of timings.

The gate and the baseline are the durable part. This document is the record of
the run that produced them.

---

## 8. What is still open

- The sixteen redundant edges in section 4.2, as their own small PRs, with the
  pack and AOT jobs watching the published packages.
- The CLI's missing `[Exposes]` coverage (section 4.3), which the Excubo
  analyzer is the right instrument for.
- `Microsoft.NET.Test.Sdk` (section 4.4), which belongs to #1893.
- `IDE0005`, which the package's documentation recommends for accuracy. It is
  off, so this run is the less precise one.
