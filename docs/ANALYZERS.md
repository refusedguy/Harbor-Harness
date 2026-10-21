# Analyzers

Harbor runs three layers of static analysis + DI validation:

1. **Roslyn analyzers** — pinned in `Directory.Packages.props`, wired in
   `Directory.Build.props` (solution-wide) and `apps/Directory.Build.props`
   (apps-only). Severities configured in `.editorconfig`.
2. **DI registration tests** — per-composition-root test projects under
   `tests/Harbor.App.*.Tests/` (Wpf/Maui/Blazor variants live in
   `contrib/tests/` since sprint-2) that build the host and assert every
   expected service is resolvable.
3. **Architecture tests** — `tests/Harbor.Architecture.Tests/` enforce layer
   dependencies, and re-run inside the Release build via the
   `HarborArchitectureGate` target in `Directory.Build.props` (the external
   `dotnet-arch` tool and its stale `dotnetarch.json` were removed in ROP-D; see
   §3).

A fourth tool, **ReferenceTrimmer**, is not an analyzer in the sense above:
it runs in its own CI job, it reports on the project files rather than on the
source, and it is gated by a baseline rather than by a severity table. It is
catalogued in [§4](#4-reference-trimmer-the-unused-reference-gate) and
measured in [docs/REFERENCE-TRIMMER.md](./REFERENCE-TRIMMER.md).

> **Writing a new rule?** [ANALYZERS_AUTHORING.md](./ANALYZERS_AUTHORING.md) is
> the guide: where the file goes, how the diagnostic is shaped, how the rule is
> waived, and the controls that prove the rule can fail. Read it before writing
> the first rule.

---

## 1. Roslyn analyzers

| Package | Scope | Rule IDs |
|---------|-------|----------|
| `Roslynator.Analyzers` | solution-wide | RCS0xxx |
| **`SonarAnalyzer.CSharp`** | **`tests/` only** | S0xxx (see [§Analyzer scope](#analyzer-scope-where-each-family-runs-and-what-it-costs)) |
| `Microsoft.CodeAnalysis.NetAnalyzers` | solution-wide | CA0xxx |
| **`AsyncFixer`** | **`src/` + `apps/`** | AsyncFixer01–05 |
| `ReflectionAnalyzers` | solution-wide | REFL001–REFL046 |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | solution-wide | RS0030 (config in [`BannedSymbols.txt`](../BannedSymbols.txt)) |
| **`Microsoft.CodeAnalysis.PublicApiAnalyzers`** | **two frozen assemblies** | **RS0016, RS0017, RS0022, RS0024–RS0027, RS0036, RS0037, RS0041, RS0048, RS0050** |
| `Meziantou.Analyzer` | solution-wide | MA0xxx |
| **`DotNetProjectFile.Analyzers`** | **solution-wide** | **Proj0xxx** (severity table in [`/Directory.Build.globalconfig`](../Directory.Build.globalconfig), not here — see §Project-file rules) |
| **`DependencyInjection.Lifetime.Analyzers`** | **`src/` + `apps/`** | **DI001–DI037** |
| `CSharpFunctionalExtensions.Analyzers` | solution-wide | CFE0001 (see [§Analyzer scope](#analyzer-scope-where-each-family-runs-and-what-it-costs)) |
| **`Excubo.Analyzers.DependencyInjectionValidation`** | **apps-only** | **EDI01–EDI04, ADP0001** |
| **`Microsoft.VisualStudio.Threading.Analyzers`** | **src/ + apps/ only** | **VSTHRD001–VSTHRD202 (one enabled)** |

The table above is the set of analyzers this repository runs. It is not the
set of analyzers that were looked at. The candidates that were checked and not
taken are in [§Checked and rejected candidates](#checked-and-rejected-candidates),
so that the next person does not spend a day of NuGet registry reads and
GitHub API calls rediscovering that a package was abandoned in 2019.

### Analyzer scope: where each family runs, and what it costs

**The scope of an analyzer package is a decision, not a detail, and this
section is the decision.** It answers three questions: what each package
costs the build, why the scope table says what it says, and what the gate
is that keeps the table and the tree in step.

#### What a package costs

An analyzer package runs inside the compiler of every project that
references it. The cost of that is not the number of findings — it is the
number of **analyzer classes** the package registers times the number of
**compilations** it is referenced by.

The compiler can be asked for the number. `-p:ReportAnalyzer=true` makes it
print, for every compilation, the elapsed time per analyzer assembly; the
numbers sum over the solution. Measured on `dev` (the Release build of
`Harbor.slnx`, 113 compilations):

| package | analyzer seconds | share |
|---------|------------------|-------|
| `SonarAnalyzer.CSharp` | 204.1 | 34 % |
| `DependencyInjection.Lifetime.Analyzers` | 75.8 | 13 % |
| `Microsoft.VisualStudio.Threading.Analyzers` | 58.3 | 10 % |
| `AsyncFixer` | 33.9 | 6 % |
| `Meziantou.Analyzer` | 23.2 | 4 % |
| `Microsoft.CodeAnalysis.NetAnalyzers` | 9.3 | 2 % |
| `Roslynator.Analyzers` | 7.4 | 1 % |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 5.2 | 1 % |
| `DotNetProjectFile.Analyzers` | 2.4 | <1 % |
| `ReflectionAnalyzers` | 2.4 | <1 % |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | 2.1 | <1 % |
| `CSharpFunctionalExtensions.Analyzers` | 0.2 | <1 % |
| `Excubo.Analyzers.DependencyInjectionValidation` | 0.1 | <1 % |
| **the thirteen packages together** | **424.4** | **71 %** |

The rest is the SDK's own AOT analyzer (`ILLink.RoslynAnalyzer`, 141.3 s —
the second largest single cost in the tree, and it is **not** one of the
packages this repository references), the source generators and TUnit.

**The largest single analyzer cost in this tree is the AOT analyzer, not
one of the nine packages the issue asked about.** It is pulled in by
`IsAotCompatible=true` on six `src/` projects, and it is the analyzer that
decides whether the shipped code is trim-safe. It is reported here because
the measurement is the measurement; it is not touched by this change,
because it is a declaration about AOT compatibility and not a package
scope.

#### The severity table does not switch the cost off

This is the fact that decides the shape of the whole thing, and it is
measured, not assumed.

The compiler's report attributes **45.5 seconds of the build** to
`Microsoft.VisualStudio.Threading.Analyzers.VSTHRD010MainThreadUsageAnalyzer`
— and `dotnet_diagnostic.VSTHRD010.severity = none` in `.editorconfig`.
The rule is switched off and the analyzer runs anyway, in all 62 `src/` and
`apps/` projects: Roslyn runs the analyzer and drops the diagnostic
afterwards, and the work is already paid.

So `severity = none` is a way of **not hearing** a rule, not a way of
**not running** it. A rule that is off still costs its analyzer's time, and
the only lever that removes the cost is the **scope** — which projects
reference the package at all.

That is also why the `.editorconfig` table and the scope table are two
different things, and why only the second one is a performance lever.

#### Where the findings are

The other half of the measurement is where the findings are, because a
family that produces nothing in a tree cannot lose anything by not running
there. Read out of the build log of the same run, deduplicated by
file/line/column **per project and per diagnostic id** — the counting unit
of this table, which is why its Reflection row reads 48 while
[§Reflection rules](#reflection-rules-reflectionanalyzers-031) below counts
the same family differently (that section counts the rule's own instances,
not the distinct sites):

| family | findings | under `tests/` | under `src/` + `apps/` |
|--------|----------|----------------|----------------------|
| `SonarAnalyzer.CSharp` | 324 | **324** | 0 |
| `ReflectionAnalyzers` | 48 | **48** | 0 |
| `Roslynator.Analyzers` | 3 | **3** | 0 |
| `DependencyInjection.Lifetime.Analyzers` | 2 | **2** | 0 |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | 4 | 4 | 0 |
| `Microsoft.CodeAnalysis.NetAnalyzers`, `Meziantou.Analyzer`, `AsyncFixer`, `CSharpFunctionalExtensions.Analyzers`, `Microsoft.VisualStudio.Threading.Analyzers`, `Excubo…` | 0 | 0 | 0 |

**Every analyzer finding in the tree is under `tests/`.** The product tree
is analyzer-clean — and it is clean *because* the analyzers gate it
(`TreatWarningsAsErrors=true` turns a warning there into a build error),
so "0 findings" in the product tree means "the gate has never fired", not
"the analyzer is silent".

#### The narrowing

| family | was | is | why |
|--------|-----|-----|-----|
| `SonarAnalyzer.CSharp` | everywhere | `tests/` | 204.1 s, the most expensive family in the tree, and all 324 of its findings are under `tests/`. The product tree has produced none since the licence decision (#2030 measured the same split a month earlier). |
| `DependencyInjection.Lifetime.Analyzers` | everywhere | `src/` + `apps/` | The DI rules are the **composition root's** gate: DI003, DI013, DI015, DI017 and DI019 are errors, and the registrations they are about live in `src/Harbor.Hosting/Modules` and the two `apps/` roots. A test project has no registration of its own to be wrong about. |
| `AsyncFixer` | everywhere | `src/` + `apps/` | All five of its rules are at `suggestion`, which `TreatWarningsAsErrors` does not promote: the package **cannot fail a build in any tree**, and it produced no finding in any of them. It is kept where the shipped code is. |

#### Two families that looked narrowable and are not

Both of them look narrowable by the rule above, and both of them turned out
to be coupled to something that reads them from the build. Narrowing them
was measured, checked, and **not** taken.

| family | why not | what it costs |
|--------|---------|---------------|
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | `tests/Harbor.Architecture.Tests/BannedApiProbe.cs` runs the **real** RS0030 analyzer in-process against a synthetic violation, and it finds the analyzer through the package path NuGet publishes for this package (the csproj's `PackageReference Update` + `GeneratePathProperty` pair). Narrowing the package out of the test tree would turn that `Update` into a no-op and move the control from "the analyzer the build uses" to "the newest version in the NuGet cache". | 5.2 s |
| `CSharpFunctionalExtensions.Analyzers` | The positive control (`analyzers/CfeControl`, deliberately outside `tests/` and outside `Harbor.slnx`) inherits this root file and needs the analyzer there — it exists so that a test can compile a known-bad snippet and require CFE0001 to fire. | 0.2 s |

Nothing else moved. `Roslynator.Analyzers`,
`Microsoft.CodeAnalysis.NetAnalyzers`, `Meziantou.Analyzer`,
`ReflectionAnalyzers`, `DotNetProjectFile.Analyzers`,
`Microsoft.CodeAnalysis.PublicApiAnalyzers` and
`Microsoft.VisualStudio.Threading.Analyzers` keep the scope they had, and
the reasons are in the table above: either the family fires in the product
tree, or it is the base layer, or its cost is below the noise of the
measurement.

#### What the narrowing bought

**The analyzer work of the whole build.** The compiler's report on the
narrowed tree, against the same report on the all-on tree, normalized by the
analyzer families that did not move (the runner that measured the second one
was between 1.3x and 1.4x slower):

| | all-on | narrowed | |
|---|---|---|---|
| `SonarAnalyzer.CSharp` | 204.1 s | 118–127 s | **−77…−86 s** |
| `DependencyInjection.Lifetime.Analyzers` | 75.8 s | 34–37 s | **−39…−42 s** |
| `AsyncFixer` | 33.9 s | 6.7–7.1 s | **−27 s** |
| the three together | 313.9 s | 160–171 s | **−143…−154 s** |
| **every analyzer in the build** | **594.4 s** | **459–491 s** | **−103…−135 s (−17…−23 %)** |

**The wall time.** The `build` job's `Compile` step. The first pair is the
controlled one: the SAME base tree, the same job, the same image, the same
day, and the two runs differ by this change and nothing else.

| tree | base | run | compile |
|------|------|-----|---------|
| **narrowed (this change)** | 766b397f | 39527276212 | **253 662 ms** |
| all-on | 766b397f | 39526886179 (dev) | 420 920 ms |
| **narrowed (this change)** | 9b0eebd4 | 39520876419 | **199 693 ms** |
| **narrowed (this change)** | 9b0eebd4 | 39518815858 | **210 525 ms** |
| all-on | 9b0eebd4 | 39515287115 (dev) | 415 007 ms |

On the current base that is **−167 258 ms (−39.7 %)**; on the base this
branch started on, −204…−215 s (−49…−52 %). The all-on compile of the SAME
job ranges over **262–417 s** across six dev runs — a 1.6x spread for one
job on one image, which is why the wall figure is a measurement and not a
controlled A/B, and why the claim above rests on the analyzer report.

**What the paired arms could and could not resolve, said plainly.** The
harness builds the same tree twice in one job — the baseline and its own arm
— so the runner's speed cancels out of the difference. That was not enough
on its own, and the control arms said so: with the compiler server left
running between the two builds, the null arms (the baseline against itself)
came out 55.6 s, 63.8 s and 65.8 s apart, with the compiler's own CPU
falling from 455/522/585 s to 256/300/298 s between the halves of the same
job — the shared compiler keeps the analyzer assemblies loaded and
JIT-warmed. Shutting it down (`dotnet build-server shutdown`) brought the
null arms to −16.9 s, −7.9 s and +1.6 s, but the second build in a job is
still systematically slower (the compiler's own CPU is 8–24 % higher in it),
and the spread of the arm deltas is ±20 s. The arms' raw deltas do not
follow the analyzer time at all — the largest raw delta is `no-projectfile`
at −24.1 s (a 2.4 s family) and `no-roslynator` at +13.0 s (a 7.4 s
family), while `no-sonar` (a 204 s family) reads −21.6 s. **A paired
wall-clock measurement on this runner cannot resolve an effect of this
size.** The compiler's own analyzer report can, and it is what the numbers
above are read from.

#### What the narrowing costs, stated plainly

**Narrowing Sonar to `tests/` removes Sonar's 447 preventive rules from the
product tree, and with them the six rules that #2030 demoted under `tests/`
"because they are noise in a test tree and are worth something in a product
tree"** (S5443, S1215, S1313, S5332, S3878, S3220). With the family running
only under `tests/`, those six are now off in the whole tree. The
`.editorconfig` path section that carries them stays — the gate
`AnalyzerSeverityScopeRules` holds its shape, and removing it is another
change — but it no longer buys the product tree anything. The product tree produced none of them, so no finding is
lost — but a rule that would have caught a new defect in product code will
not catch it any more. That is a change to the build contract, and it is
written down as one. It is one row in
[`tools/analyzer-scope.json`](../tools/analyzer-scope.json), so it is one
line to revert.

The alternative — narrowing Sonar to the product tree instead — would have
saved less and would have thrown away all 324 findings, which are the
reason the package is worth its cost in the first place.

#### The gate

[`tools/check-analyzer-scope.py`](../tools/check-analyzer-scope.py) reads
[`tools/analyzer-scope.json`](../tools/analyzer-scope.json) — the one
source of truth for where every family runs — and fails when the tree says
anything else:

| rule | what it catches |
|------|-----------------|
| `SCOPE-DRIFT` | the trees a family runs in differ from the trees the table declares |
| `DECLARATION-SITE` | a family is declared in a file the table does not name |
| `NO-CONDITION` | a family is declared without the switch that turns it off |
| `UNDECLARED-ANALYZER` | a package that looks like an analyzer is referenced and is not in the table |
| `MISSING-PIN` | a family in the table has no `<PackageVersion>` |
| `ORPHAN-SWITCH` | a scope file switches a property no family owns |
| `STALE-ROW` | a family in the table is not declared anywhere |

Its `--self-test` plants every shape of drift and asserts the gate names
it, and the false-positive controls are in it too: a package that is not an
analyzer is not reported, and a comment that names an analyzer package is
not a declaration.

Without the gate nothing would complain when a family was declared in the
root props file again: the build stays green, the findings stay the same,
and only the wall time grows.

### Checked and rejected candidates

A row is a verdict only if it names a reason. "Rejected" on its own is a
shrug, and a shrug cannot be re-checked when the package is republished.

`Version checked` is the version the verdict was made against, and the verdict
is a verdict **on that version**. A new release reopens it.

**Checked on 2026-10-20** against `dev@a9f77508`. The version, the publish
date and the licence come from the NuGet registry (`licenseExpression`, the
nuspec's `licenseUrl`, or nothing at all where the package declares neither);
the last push, the archived flag and the licence file come from the GitHub
API.

| Package | Version checked | Licence | Verdict | Reason |
|---------|-----------------|---------|---------|--------|
| `Microsoft.CodeAnalysis.NetAnalyzers` | 10.0.402 | MIT | ADOPT, solution-wide | The base layer every other package is judged against. 40 of its rules carry a Harbor severity of their own (4 `none`, 33 `suggestion`, 3 `error`); the rest keep the severity they ship with. The 10.0.302 → 10.0.402 move adds zero ids (329 `CA####` ids in both builds) and changes no severity (#1897). The duplication the SDK's own bundled copy might have caused is not there: the package ships `buildTransitive/DisableNETAnalyzersForNuGetPackage.props`, which sets `EnableNETAnalyzers=false`, and the SDK adds its bundled `Microsoft.CodeAnalysis.NetAnalyzers.dll` under `Condition="$(EnableNETAnalyzers)"` in `Microsoft.NET.Sdk.Analyzers.targets` (read from the SDK on disk, release/10.0.4xx). The package therefore REPLACES the SDK's copy rather than adding to it, which is why a `-p:ReportAnalyzer=true` run is not needed to settle it. What the pin does decide is the rule set: the SDK's bundled copy follows the SDK, this one does not, so the pin is what freezes the CA rules at 10.0.402 until it is moved by hand. |
| `Roslynator.Analyzers` | 5.0.1 | Apache-2.0 | ADOPT, solution-wide | 284 RCS ids and no severity rows at all: the 4.15.0 to 5.0.1 gap introduced zero new ids and removed twenty obsolete implementations (#1851). |
| `SonarAnalyzer.CSharp` | 10.29.0.143774 | source-available, not SPDX (a `LICENSE` file in the package) | ADOPT, `tests/` only — see [§Analyzer scope](#analyzer-scope-where-each-family-runs-and-what-it-costs) | 447 rule ids ship in the package. 23 are configured in `.editorconfig` (22 `none`, 1 `suggestion`), 4 more are NoWarn'd in `Directory.Build.props`, and 6 more are off under `tests/` only; each carries its own reason. Measured on `dev@086ef777` (CI run 39472959027, the Release build of `Harbor.slnx`): 476 sites in 40 rules, every one of them under `tests/`, and 0 under `src/` or `apps/`. The licence is the "SONAR Source-Available License v1.0", not LGPL: its grant covers "any Non-competitive Purpose", and the three carve-outs (SonarQube's own functionality, competition with it, and AI engaging with the Program or the data it provides) do not reach Harbor's product, because the package is build-time (`PrivateAssets=all`, `ExcludeAssets="runtime"`), so no Sonar code or data reaches a binary and the Harbor runtime never consumes a diagnostic. The one reading that does reach Harbor is the dev workflow, where an AI agent reads the analyzer's diagnostics in the build log; that is the owner's call and it is stated as the residual in [`docs/DEVELOPMENT.md §Analyzer licences`](./DEVELOPMENT.md#analyzer-licences). The pin stays at 10.29.0.143774: 10.35.0.4138 is the current release and was measured (10 new rule ids, none removed, the licence file byte-identical, every configured id still present), and taking it needs the #1851 baseline, which is a build this change cannot run. |
| `Meziantou.Analyzer` | 3.0.315 | MIT | ADOPT, solution-wide | 207 MA rows. The 3.0.123 to 3.0.315 gap added 31 ids, each baselined at the severity the package ships it with (#1851). |
| `AsyncFixer` | 2.1.0 | Apache-2.0 | ADOPT, `src/` + `apps/` — see [§Analyzer scope](#analyzer-scope-where-each-family-runs-and-what-it-costs) | Five rules, AsyncFixer01 to AsyncFixer05, all at `suggestion`. |
| `ReflectionAnalyzers` | 0.3.1 | MIT | ADOPT, solution-wide | 46 REFL ids and no severity rows: nine rules fire 216 times and every one of them is in `tests/`, where `TreatWarningsAsErrors` is off (#1905). The dynamic-code ban is not this package, it is RS0030 (#1844). |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 5.6.0 | MIT | ADOPT, solution-wide | RS0030 over `BannedSymbols.txt` and `BannedSymbols.DynamicCode.txt`. Since #1844 the compiler enforces the dynamic-code ban that a hand-written regex used to (#626). |
| `DependencyInjection.Lifetime.Analyzers` | 3.7.8 | MIT | ADOPT, `src/` + `apps/` — see [§Analyzer scope](#analyzer-scope-where-each-family-runs-and-what-it-costs) | 40 DI rows (37 tree-wide, 3 path-scoped), one Harbor severity per rule. DI003, DI015, DI017 and DI019 are errors. The 2.18.24 → 3.7.8 move added eleven ids and removed none; each new id is baselined at the severity the package ships it with, and DI033's one finding was written off with a reason rather than fixed (#1897). |
| `CSharpFunctionalExtensions.Analyzers` | 1.3.0 | none declared in the nuspec | ADOPT, solution-wide | CFE0001 ("check IsSuccess or IsFailure") is the mechanical guard for the §ROP-001 crash class. CFE0002 is Info and stays off. 1.4.1 exists and adds no id, but its rewritten walker crashes the compiler with AD0001 on five production projects — under `TreatWarningsAsErrors` they then fail to compile and emit no CFE0001 at all, so the guard would go blind exactly where the crash class lives. The pin stays at 1.3.0 (`docs/ROP-API-INVENTORY.md` §5.1). |
| `DotNetProjectFile.Analyzers` | 1.19.1 | MIT | ADOPT, solution-wide | 193 Proj rows in [`/Directory.Build.globalconfig`](../Directory.Build.globalconfig). Three rules are errors, one is `none`, two stay `suggestion` because the lock-file question is the owner's (#1851, #1857). |
| `Excubo.Analyzers.DependencyInjectionValidation` | 1.0.33 | a `LICENSE` file, no SPDX expression | ADOPT, apps-only | EDI01 to EDI04 plus ADP0001, all demoted to `suggestion` because the analyzer's flow analysis only follows the immediate method body. The real coverage is the DI registration tests (§2). |
| `Microsoft.VisualStudio.Threading.Analyzers` | 18.12.1 | MIT | ADOPT with scope, `src/` and `apps/` only (#1852) | 29 rules, one of them on: VSTHRD110 at `suggestion`. The other 28 are `none` with a reason each, and four of them ship as `error` by default, so landing the package without the demotions would have reddened the tree on the day it arrived. |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | 5.6.0 | MIT | ADOPT, staged (PR #1944, issue #1845) | Replaces 1212 lines of python public-API gate with the compiler, on the two frozen projects only. The pull request is open, so this row records the decision, not the merge. |
| `TheExceptionator.Analyzers` | 1.3.33 | Apache-2.0 | REJECT the package, ADOPT the findings (#1848) | The two findings were real and both are fixed, and the rule that finds them is now a house rule (`InnerExceptionRules.cs`). The package itself is not consumable here: its nuspec declares its Roslyn dependencies with `exclude="Build,Analyzers"`, and every version of the Roslyn packages carries EXACT version constraints (`[3.3.1, 3.3.1]`), so against this repository's central Roslyn pin (5.6.0) the restore fails with NU1608 on every product project, and the same edges leave the compile and runtime assets of `Microsoft.CodeAnalysis.CSharp` in the closure of every project that references the package. The nine analyzer packages this repository already uses declare NO dependencies at all, because the Roslyn assemblies come from the compiler. |
| `NetArchTest.Rules` | 1.3.2 | MIT | IN the tree, the replacement is open | The layer gate runs on it, and its last release was 2021-05-23. The replacement is tracked in #1841 (ArchUnitNET) and #1864 (aweXpect.Reflection). |
| `aweXpect.Reflection` | 3.1.0 | MIT | NOT-ADOPT the package, the capability is a house rule (#1864, #1879; the pilot merged as `8c9528cf`, PR #1959) | The pilot is merged, and every capability it was bought for is in-tree. The effective surface and the dead public members are measured by house rules (`EffectiveInterfaceSurface.cs`, `FatInterfaceShapeRatchet.cs`, `DeadPublicMemberRule.cs`), and the layer dependency rules already run on plain reflection with no NetArchTest and no IL reading (`LayerDependencyTests.cs`), so `NetArchTest.Rules` is a convenience there and not a requirement. The package is a runtime assertion library with no analyzer in it, so its rules would run at test time, and its test-framework adapter is resolved against the loaded assemblies, which on .NET 8 or later means an explicit registration. The capability it was bought for is one `BindingFlags` flag and one walk over `Type.GetInterfaces()`. |
| `ErrorProne.NET.CoreAnalyzers` | 0.9.0-beta.4 | MIT (a `licenseUrl`, no SPDX expression) | REJECT (#1849) | Re-measured on `dev@8fb857e7`; the verdict stands and the reason is sharper. The package's own EPC12 gives 23 sites on this tree, and every one of them is the boundary conversion `docs/ANTIPATTERNS.md` §16 prescribes (`Result.Failure(ex.Message)`), so they are false positives here. The two findings the audit recorded are EX003's, not EPC12's: EPC12 only fires on a catch of `Exception`, `AggregateException`, `TypeLoadException` or `TargetInvocationException`, and both sites catch `JsonException`. The two sites are real, and they are fixed, with a house rule (`InnerExceptionRules.cs`) guarding them. The unique rules give 0 (EPC32, EPC19, EPC16, EPC17, EPC26, EPC30, EPC23, EPC11), and EPC27 gives 4 false positives on the `async void` handlers `docs/ANTIPATTERNS.md` §13 blesses. |
| `IDisposableAnalyzers` | 4.0.8 | MIT | REJECT the package, ADOPT the findings (#1854) | The package is dead (last push 2024-06-19), but IDISP014 (one `HttpClient` per call) is not covered by CA1001 or CA1063. The two sites it named were fixed by hand and a house rule now guards them (`HttpClientReuseRules.cs`). |
| `SmartAnalyzers.MultithreadingAnalyzer` | 1.1.31 | MIT | REJECT | Last release 2019-09-17. `lock`, `Monitor` and `Interlocked` are covered by Sonar and AsyncFixer already. |
| `SmartAnalyzers.ExceptionAnalyzer` | 1.0.10 | MIT | REJECT | Last release 2019-05-21, and it overlaps Meziantou, Sonar and CA. |
| `DotNetAnalyzers.DocumentationAnalyzers` | 1.0.0-beta.59 | MIT | REJECT | Last push 2022-12-08 and still beta. `tools/check-xml-doc-coverage.py` covers the same ground and can state a suppression reason, which the analyzer cannot. |
| `ClosedTypeHierarchyDiagnosticSuppressor` | 1.3.0 (a GitHub release; there is no nuget package) | MIT | REJECT | A suppressor of another diagnostic, not a check, and #1810 (the enum completeness gap) needs a check. A nuget search for the id returns 0 hits. |
| `OneOfDiagnosticSuppressor` | no release, no nuget package | MIT | REJECT | The same reason, and there is nothing to install: a nuget search for the id returns 0 hits. |
| `ClrHeapAllocationAnalyzer` | 3.0.0 | Apache-2.0 (a `licenseUrl` to the repository's LICENSE) | REJECT | The repository (Microsoft/RoslynClrHeapAllocationAnalyzer) is archived, last push 2022-05-26. The display-class finding it would have caught (#1529) needs a house analyzer, not this one. |
| `NetFabric.Hyperlinq.Analyzer` | 2.3.0 | MIT | REJECT | No release for three years, and it analyses the Hyperlinq library, which this repository does not use. |
| `SuccinctLinq.Analyzers` | 0.2.0 | MIT | REJECT | 0.x, one author, released days before the check. LINQ style is not this repository's problem. |
| `StyleCopAnalyzers` | 1.3.133-beta | MIT | REJECT | The stable line is not there: it is beta, opinionated, and it would land as a wave of findings. If it is ever wanted it starts at `severity = none` for the whole family. |
| `Agoda.Analyzers` | 1.1.203 | Apache-2.0 | REJECT | Opinionated, and the rules it shares with us are already covered by NetAnalyzers, Sonar and Roslynator. |
| `Menees.Analyzers` | 4.0.2 | MIT | REJECT | A StyleCop port, so the same reasons. |
| `Blowin.CleanCode` | 2.6.0 | none declared in the nuspec | REJECT | Opinionated, and the licence is not declared anywhere a licence check could read it. |
| `csharp-refactor` | 0.1.22 | Unlicense | REJECT | 0.x, two stars, and "apply the fixes in bulk" is not a thing this repository wants. |
| `SharpSource` | 1.33.4 | MIT | REJECT | Opinionated, and it overlaps Sonar on the same rules. |
| `Gu.Analyzers` | 2.0.3 | MIT | REJECT | No release for two years. |
| `SecurityCodeScan` | 3.5.4 | LGPL-3.0 | REJECT | Decided on activity and duplication, not on the licence: a build-time analyzer with `PrivateAssets=all` does not reach the product binary. Two years without a release, and it duplicates the Sonar S-rules. |
| `Wintellect.Analyzers` | 1.0.6 | a `licenseUrl` to the repository's LICENSE, no SPDX expression | REJECT | Last release 2016-11-25. Ten years. |
| `Selectorlyzer.Analyzers` | 0.0.5-beta | MIT (the nuspec's `licenseExpression` is empty) | REJECT as a substrate (#1846) | The package has three diagnostic ids in total (SEL0001 to SEL0003), so one rule cannot be switched off without its neighbours, and the matcher is syntactic (`SimpleBaseType` matched by name), so there is no semantic model to move the architecture rules onto. |
| `TngTech.ArchUnitNET` | 0.13.4 | Apache-2.0 | REJECT at 0.13.4 (#1841, #1870, #1871) | There is no assembly-level primitive (upstream #130), an empty forbidden set makes a negative rule vacuously true, and 0.13.4 loses every async dependency in a Release build, which is the configuration the architecture gate runs in. |
| `TngTech.ArchUnitNET.TUnit` | 0.13.4 | Apache-2.0 | REJECT with the core package | The adapter's whole TUnit surface is one exception type, so it was never the risk. Upstream stopped supporting it on 2025-09-24 (#1873). |
| `Testably.Architecture.Rules` | 0.14.0 | MIT | REJECT (#1863) | The repository is archived and its README opens with `# DISCONTINUED!`. |
| `Mockolate` | 3.6.0 | MIT | NOT-ADOPT (#1876) | AOT-clean, so the "reflection means not AOT" refusal does not apply, but its doubles are emitted into every test project (about 19 000 generated lines for our 27 interfaces, against our generator's 1 900, once) and it returns `default(Result<T>)`, where `Result.IsSuccess` is then true. |
| `Testably.Abstractions` | 10.4.0 | MIT | NOT-ADOPT for production (#1876) | `TimeProvider` is already the product clock seam, and the file ports are already narrow `Result<T>` interfaces without exceptions. A second `IFileSystem` would throw that contract away. |
| `aweXpect` | 3.4.0 | MIT | NOT-ADOPT as an assertion replacement (#1877) | TUnit's assertions are already in the tree; a second assertion vocabulary in 172 test files is an extra, not a saving. |
| `aweXpect.Snapshots` | 0.6.0 | MIT | NOT-ADOPT (#1772) | The golden-frame work has its own plan, and the package does not cancel it. |
| `aweXpect.Snapshots.PublicApi` | 0.6.0 | MIT | NOT-ADOPT, the third candidate (#1872) | `Microsoft.CodeAnalysis.PublicApiAnalyzers` covers the public-API freeze at the compiler level instead. |
| `aweXpect.Migration` | 0.7.0 | MIT | NOT-ADOPT (#1877) | It migrates Moq and NSubstitute assertions, and this repository has none of either. |
| `aweXpect.Logging` | 0.2.0 | MIT | WATCH (#1886) | It would pull `Microsoft.Extensions.Diagnostics.Testing` 10.11.0, which asks for `Microsoft.Extensions.Logging` 10.0.13 while this repository pins 10.0.10, and it does not cancel the shared `ILogger` double work. |
| `aweXpect.Json` | 2.1.0 | MIT | NOT-ADOPT (#1886) | The JSON layer is closed by source generators, and the assertions it would replace mostly check our own types rather than arbitrary JSON. |
| `jscpd` | 5.4.1 | MIT | ADOPT as a SECOND detector (#1847) | `tools/check-test-dup.py` stays the gate for duplicated test bodies, and jscpd runs beside it over `src/` + `apps/` + `tests/` (`.jscpd.json`, `tools/check-dup-jscpd.py`). It was REJECTED as a replacement, and the measurement says why: its own `--fail-on-new-clones` is keyed on a content hash, so editing a duplicated body reads as new duplication — on 13 real commits of `dev` it was red on 1 (3 with the `renamed` configuration), and 3 of the 4 "new clones" it reported on commit `8bde0944` were clones that already existed before it. The aggregate rule (the tree's duplicated lines must not exceed the baseline's) has the property the fingerprint rule lacks: the same edit leaves the total unchanged. As a second detector it finds what the python gate structurally cannot see — duplication that is not a method body, and duplication in the product code, which no gate covered. |
| `Philips.CodeAnalysis.DuplicateCodeAnalyzer` | 2.0.0 | MIT | REJECT as a gate (#1847) | It compares inside one compilation, so it cannot see the cross-project duplication that is half of the problem; it has no baseline concept, and its cost is paid on every build of every project. |
| `PMD CPD` | 7.28.0 | LGPL-2.1 | REJECT (#1847) | It needs a JRE in CI, where this repository has neither node nor java. It also has no baseline that compares "only what is new", which is the one thing a duplication gate in this repository has to do: the aggregate rule the jscpd gate uses would be its `minimum-tokens` count and a committed snapshot of it, and the tool is 30 years older than the engine that was measured against it (#1847). |
| `Microsoft.CodeAnalysis.DuplicateCodeAnalyzer` | no such package | none | REJECT | The NuGet flat-container index for that id answers `BlobNotFound`. There is nothing to evaluate. |
| `ReferenceTrimmer` | 3.5.13 | MIT | ADOPT as a one-off report, not as a gate (#1855) | The manual sweep of unused references already produced two wrong answers (#910, #957) and left 15 candidates unclassified. A permanent gate would be red on packages that are genuinely needed transitively, which is the "train people to ignore the rule" outcome the repository already forbids for RS0030 in tests. |
| `DocumentFormat.OpenXml` | 3.5.1 | MIT | NOT A CANDIDATE | The OpenXML SDK, not an analyzer: it ships no diagnostic ids, so there is nothing to enable and nothing to baseline, and nothing in this repository reads or writes OOXML. |

#### What changed since the 2026-10-20 audit

The audit that produced the first version of this table recorded a snapshot.
Eight of its rows have moved since, and the table above carries the current
state:

| Package | The audit said | It is now | Why |
|---------|----------------|----------|-----|
| `Microsoft.CodeAnalysis.NetAnalyzers` | 10.0.302 | 10.0.402 | A servicing patch of the same minor, moved with the SDK it ships beside (#1897). Zero new ids — 329 `CA####` ids in both builds — and no severity changed, so the verdict is unchanged and only the version moved. |
| `DependencyInjection.Lifetime.Analyzers` | 2.18.24 | 3.7.8 | The one move that adds rules: eleven ids, none removed (#1897). Each new id is baselined at the severity the package ships it with, and the one finding DI033 had is written off with a reason (the instances are pre-built on purpose; the container does not dispose what it did not create). |
| `DotNetProjectFile.Analyzers` | REJECT for now, it covers `.csproj` hygiene rather than our invariants | ADOPT, three rules as errors | The package is in the tree at 1.19.1 with Proj0037, Proj0040 and Proj0041 as errors (#1851, #1857). |
| `Microsoft.VisualStudio.Threading.Analyzers` | not rejected, resolved separately in #1852 | ADOPT with scope | #1852 merged. The package is in the tree with one rule on, and the gate that actually decides "is this an unobserved Task" is now `HARCH003`, a house analyzer, not the package. |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | a candidate with an open decision | ADOPT, staged | #1845 is decided and PR #1944 is open. The python public-API gate it replaces is still in the tree until that pull request merges. |
| `IDisposableAnalyzers` | REJECT, it duplicates what CA1001 and CA1063 already say | REJECT the package, ADOPT the findings | IDISP014 is not covered by CA1001 or CA1063. The package is still dead; the two `HttpClient` sites it named were fixed by hand, a house rule now guards them (`HttpClientReuseRules.cs`), and both landed (#1854). |
| `ErrorProne.NET.CoreAnalyzers` | REJECT, measured | REJECT, measured | The verdict stands, but the version the audit recorded (`0.1.5898.41180`) is not a version this package ever published. The measured version is `0.9.0-beta.4`, and it is the one the verdict applies to (#1849). The two product findings it named are fixed, and a house rule guards them; the package itself stays out of the tree. |
| `TheExceptionator.Analyzers` | not rejected, resolved separately in #1848 | REJECT the package, ADOPT the findings | #1848 merged. The two lost inner exceptions are fixed, and the rule that finds them is a house rule (`InnerExceptionRules.cs`) rather than the package's EX003 and EX021. The package is not in the tree, and the reason is in the row above: its dependency edges break the restore. |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | already connected, not a candidate | ADOPT, and it now carries the dynamic-code ban | Since #1844 the second ban file, `BannedSymbols.DynamicCode.txt`, is enforced by RS0030 as well, so the package does more than the "What NOT to do" list. |

Re-check this table when a package releases a new version, when Roslyn moves a
major version, or once a year, whichever comes first. A verdict with a
version in it is a verdict on that version.

### Reflection rules (ReflectionAnalyzers 0.3.1)

46 diagnostics, `REFL001`–`REFL046`, all member-by-member correctness rules:
binding flags, overloads, `nameof`, generic type arguments, `InvokeMember`
targets. **None of them is about dynamic code** — the shipped analyzer
assembly contains no `Assembly.Load`, `AssemblyLoadContext`, `Reflection.Emit`,
`TypeBuilder` or `DynamicMethod` rule. The ban on the dynamic-code family in
`src/` + `apps/` is held by the compiler: `RS0030` over
[`BannedSymbols.DynamicCode.txt`](../BannedSymbols.DynamicCode.txt) (see
[the dynamic-code ban](#the-dynamic-code-ban-bannedsymbolsdynamiccodetxt)
below).

Measured on this tree (#1905, CI build log of a green run): nine rules fire,
216 instances, and every one of them is in `tests/`:

| ID | What it says | Instances |
|----|--------------|-----------|
| REFL008 | Specify binding flags | 84 |
| REFL039 | Prefer `typeof(T)` over `instance.GetType` | 30 |
| REFL016 | Use `nameof` | 30 |
| REFL029 | Specify the parameter types | 24 |
| REFL003 | The member does not exist | 24 |
| REFL005 | The binding flags match nothing | 6 |
| REFL006 | The binding flags can be more precise | 6 |
| REFL009 | The referenced member is not known to exist | 6 |
| REFL037 | The type does not exist | 6 |

`src/` and `apps/` are silent: the zero-warning bar
(`TreatWarningsAsErrors=true`) would have turned a hit there into a build
error, and the build is green. `tests/` sets
`TreatWarningsAsErrors=false` (`tests/Directory.Build.props`), so those 216
warnings are warnings nobody reads.

> The two `.editorconfig` lines that used to configure `REL0001` / `REL0002`
> configured ids that do not exist in the package — they were dead
> configuration, and the real rules have always run at their default
> severity. See #1905.
### Version baseline, and what a version move may change

The families in the table above are the ones a version move has been
measured against.
Every count is read out of the shipped packages — the ids come out of the
analyzer assemblies' string heaps, the titles and default severities out of
the README each package ships — not out of a changelog summary.

| Family | Pinned | Decision |
|--------|--------|----------|
| `Roslynator.Analyzers` | 5.0.1 | **No severity rows.** The 4.15.0 → 5.0.1 gap introduces **zero** new diagnostic ids: both packages ship exactly 284 `RCS####` ids, read out of the analyzer assemblies. Roslynator 5.0.0 removes the *implementations* of twenty obsolete rules (RCS0014, RCS0022, RCS0038, RCS0043, RCS0047, RCS1008–RCS1010, RCS1012, RCS1035, RCS1036, RCS1038/RCS1040/RCS1041/RCS1066/RCS1072/RCS1091/RCS1106, RCS1176/RCS1177, RCS1237) and points at successor rules that already ship in 4.15.0, so the move can only shrink the finding surface. 5.0.0 also adds the `roslyn5.0` analyzer flavour, which is the one the .NET 10 SDK (Roslyn 5.x) loads; 4.15.0 only shipped roslyn3.8/roslyn4.7. |
| `Meziantou.Analyzer` | 3.0.315 | **31 new rows, one per new rule.** The 3.0.123 → 3.0.315 gap adds `MA0213`–`MA0243` and changes **no** severity on the 209 rules that already existed — both facts read out of the `configuration/default.editorconfig` the package ships, which lists every rule with its default severity. Each new rule is baselined to the severity the analyzer ships it with, so the move cannot turn the zero-warning build red on the day it lands. The three that ship as `warning` (MA0220, MA0240, MA0241) read the `BannedSyntaxes.txt` file, which this repository does not have, so they have no input; they are advisory here so that adding that file later cannot break the build either. |
| `DependencyInjection.Lifetime.Analyzers` | 3.7.8 | **11 new rows, one per new rule.** The 2.18.24 → 3.7.8 gap adds **eleven** ids and removes **none**: 2.18.24 ships 26 (`DI001`–`DI022`, `DI024`–`DI027`; `DI023` was unused), 3.7.8 ships 37 (`DI001`–`DI037`, no gap). The gap spans 41 published versions, and the ids arrive in steps: 3.0.0 adds `DI028`/`DI029`/`DI030`, 3.5.20 adds `DI023`/`DI031`–`DI035`, 3.6.0 adds `DI036`, 3.7.0 adds `DI037`, and 3.7.8 is the `DI013` behaviour fix (closed implementation compatibility is now CLR assignability, so an implicit numeric conversion or a user-defined operator can no longer hide a registration the container cannot bind). Each new id is baselined to the severity the package ships it with; the per-id decision and the reason are in §DI lifetime rules below. |
| `Microsoft.CodeAnalysis.NetAnalyzers` | 10.0.402 | **No severity rows.** The 10.0.302 → 10.0.402 gap is a servicing patch of the same minor and adds **zero** ids: both analyzer assemblies ship the same **329** `CA####` ids, and the C#-specific satellite the same 5 — read out of the shipped assemblies. The main assembly is the same **length** (2 561 320 bytes) but **not byte-identical**: 2 099 499 bytes differ (metadata offsets shift between builds), so the "byte-identical" claim the original audit recorded does not hold for the version actually taken. The rule set is what the build cares about, and it did not move. |
| `Microsoft.CodeAnalysis.CSharp` | 5.9.0 | **No severity rows — this is a compiler, not an analyzer.** The delta is measured on the compiler and on the analyzer it drags in: **1987 → 2002** distinct `CS####` ids, **17 added** (`CS9378`–`CS9394`) and **2 removed** (`CS9367`, `CS9368`). Every added id is an *error* for a language feature the tree does not use (shebang, `requires unsafe`, closed type declarations, unions, the `safe` modifier, labelled `break`/`continue`), so no new *warning* can fire. The transitive analyzer `Microsoft.CodeAnalysis.Analyzers` moves 5.3.0 → 5.9.0-1.26328.17 with **52** `RS####` ids in both — zero added, zero removed. Public API: six members removed (compiler-generated backing fields and three `TextLine` members) and one signature family changed (`SyntaxFactory.BreakStatement`/`ContinueStatement` gained a label parameter); the tree uses none of them, and no member became obsolete between the two versions. The pin now matches the compiler the build runs on: `global.json` asks for SDK 10.0.402, and the 10.0.4xx SDK branch pins `MicrosoftCodeAnalysisCSharpPackageVersion` = 5.9.0. |
| `CSharpFunctionalExtensions.Analyzers` | 1.3.0 | **No move, and the reason is measured.** 1.4.1 exists and adds **zero** ids — 1.3.0 and 1.4.1 ship the same two (`CFE0001`, `CFE0002`) and the same three public types. What 1.4.x changes is the rule body: `CFE0001`'s title reads "IsSuccess or **IsFailure**" where 1.3.0 reads "IsError", the message appends the file/line/char of the offending access, and the walker was rewritten into a `ResultValueWalker`. **That rewrite crashes the compiler**: 1.4.1 reports `AD0001 … System.InvalidOperationException` on five production projects, and under this repository's `TreatWarningsAsErrors=true` those projects then fail to compile — a project that does not compile emits no `CFE0001` at all, so the §ROP-001 guard would go blind exactly where the crash class lives (`docs/ROP-API-INVENTORY.md` §5.1 has the measurement). There is no intermediate version that dodges the rewrite: `ResultValueWalker` is already in 1.4.0's assembly too. The pin stays at 1.3.0, and the guard keeps firing on the known shapes (209 sites, 35 shipped + 174 under `tests/`). |

**The rule for the next move is the one these two moves established:** a
version bump lands with a **baseline**, not with a finding. Every id the new
version adds gets a row with a written reason, at the severity the new version
ships it with, and it is promoted one rule at a time afterwards — never the
other way round, because `TreatWarningsAsErrors=true` turns a pre-existing
finding into a red build the moment the version moves.

One hole is known and is not widened by this table: `MA0184`–`MA0212` exist in
3.0.123 as well and are still not in `.editorconfig`. Nine of them ship
`warning` (the EventSource and JsonSerializer rules) and none of them fires
here.

### DI lifetime rules (DependencyInjection.Lifetime.Analyzers 3.7.8)

37 diagnostics covering captive deps, lifetime mismatches, circular DI,
scope leaks, `BuildServiceProvider` misuse, HttpClient lifetime, and
un-awaited work that outlives its scope.

| ID | Title | Default | Harbor | Rationale |
|----|-------|---------|--------|-----------|
| DI001 | Service scope not disposed | Warning | Warning | Real bug — keep visible. |
| DI002 | Scoped service escapes scope | Warning | Warning | Real bug. |
| **DI003** | **Captive dependency** | Warning | **Error** | Production-only stale state — block. |
| DI004 | Service used after scope disposed | Warning | Warning | Real bug. |
| DI005 | Use `CreateAsyncScope` in async methods | Warning | Warning | Performance + correctness. |
| DI006 | Static `IServiceProvider` cache | Warning | Warning | Memory leak. Relaxed to Suggestion for the Avalonia desktop root only — see §Path-scoped severity overrides. |
| DI007 | Service locator anti-pattern | Info | Suggestion | Design smell, not a bug. |
| DI008 | Disposable transient service | Warning | Warning | Leak. |
| DI009 | Open generic captive dependency | Warning | Warning | Real bug. |
| DI010 | Constructor over-injection | Info | Suggestion | Design smell. |
| DI011 | `IServiceProvider` injection | Info | Suggestion | Intentional in HostBuilder.cs. |
| DI012 | Conditional registration misuse | Info | Suggestion | Code smell. |
| **DI013** | **Implementation type mismatch** | Error | **Error** | Default; restated for visibility. 3.7.8 also fixes the rule's own compatibility test (closed implementation compatibility is CLR assignability now), so an implicit conversion can no longer hide a registration the container cannot bind. |
| DI014 | Root provider not disposed | Warning | Warning | Leak. |
| **DI015** | **Unresolvable dependency** | Warning | **Error** | Build-time signal for missing deps. |
| DI016 | `BuildServiceProvider` misuse | Warning | Suggestion | Existing pattern in HostBuilder.cs — refactor tracked separately. |
| **DI017** | **Circular dependency** | Warning | **Error** | Build-time signal for DI cycles. |
| DI018 | Non-instantiable implementation type | Warning | Warning | Real bug. |
| **DI019** | **Scoped service resolved from root** | Warning | **Error** | Captive dependency at resolve site. |
| DI020 | Middleware captures scoped service | Warning | Warning | Real bug. |
| DI021 | Non-thread-safe service shared across handlers | Warning | Warning | Concurrency bug. |
| DI022 | Service instance reused across handlers | Info | Suggestion | Design smell. |
| DI023 | Fire-and-forget background work captures a scope | Warning | Warning | New in 3.0.0. Needs a `using` scope inside the same method — the tree creates **no** service scope at all (`CreateScope`/`CreateAsyncScope` appear nowhere), so the rule has no input. |
| DI024 | Hosted service scope outside loop | Warning | Warning | Real bug. |
| DI025 | Event subscription without unsubscribe | Warning | Warning | Memory leak. |
| DI026 | Event subscription on scoped publisher | Info | Suggestion | Design smell. |
| DI027 | Rx subscription without dispose | Warning | Warning | Memory leak. |
| DI028 | Discarded callback registration on a longer-lived source | Warning | Warning | New in 3.0.0. The tree registers no callback on `ApplicationStopping`, `IOptionsMonitor.OnChange`, `ChangeToken.OnChange` or `IChangeToken`; every `CreateLinkedTokenSource` in the tree is disposed on the path that owns it (`DefaultAgent`, `IdeJsonRpcServer`, `IdeSessionBridge`, `BackgroundTaskRegistry`), and the remaining `CancellationToken.Register` sites take a method parameter, which the rule excludes. |
| DI029 | HttpClient lifetime misuse | Warning | Warning | New in 3.0.0. The tree's two long-lived clients (`WebFetchTool.SharedClient`, `McpHttpClientFactory.Create`) both set `PooledConnectionLifetime`, which is the rule's own documented exemption; the discovered-provider clients come from `IHttpClientFactory` since #1854; the two raw `new HttpClient` sites that remain (`ProviderFactories`' desktop Ollama client, `ConfigRegistration`'s desktop model catalog) are locals in a static method — neither a container registration nor a static field — and the socket-exhaustion tier needs `IHttpClientFactory` in the same compilation, which only `Harbor.Hosting` has, where neither type is a registered implementation. |
| DI030 | Unbounded singleton or static cache | Info | Suggestion | New in 3.0.0. Every private collection field in the tree is either size-checked, removed from, or owned by a type the container never creates (`PluginReloadService`, `ThemeDirectoryWatcher`, `BackgroundTaskRegistry`). Design smell, not a bug — Info by the package's own choice. |
| DI031 | Shared implementation registered under several service types | Info | Suggestion | New in 3.5.20. No implementation is registered under two service types on one collection: the two implementations that appear twice (`SystemDirectoryLister`, `ProcessGitQuery`) are registered in two *different* assemblies, each under one service type. |
| DI032 | Service implements only `IAsyncDisposable` | Warning | Warning | New in 3.5.20. Every `IAsyncDisposable`-only type in the tree (`McpRegistry`, `LspClient`, `PtyProcess`, `EventBroadcaster`, …) is constructed directly and never registered as a plain type registration, and `McpRegistry` reaches the container through an opaque factory, which the rule leaves quiet. |
| DI033 | Container will not dispose a pre-built instance | Info | Suggestion | New in 3.5.20. **Written off with a reason, not fixed.** Three sites hand the container an instance it did not create — `TelemetryModule`'s `EventBusQueueAgeReporter` and `TelemetryTextExporter`, and `RegistryFacade`'s `McpRegistry` — and the container does not dispose an implementation instance (Microsoft's own guideline: "the developer is responsible for disposing the services"). They are pre-built on purpose: their inputs (the eagerly-composed event bus, the export options, the transport factories) are not container services. The consequence is bounded — the poll loop, the export's file handle and the MCP registry's children live until the process exits — and closing it properly needs a shutdown seam the composition root does not have (a startup resolution or a hosted service), which is a composition-root change, not a package-bump one. The rule stays at `suggestion`, so the finding is visible in the IDE and invisible in the build log. |
| DI034 | `HttpContext` used in fire-and-forget background work | Warning | Warning | New in 3.5.20. ASP.NET Core only; the tree has no `HttpContext` and no ASP.NET dependency. |
| DI035 | Non-thread-safe service shared across a fan-out | Warning | Warning | New in 3.5.20. Needs a `DbContext`/ADO.NET type shared across a `Task.WhenAll` projection; the tree's `WhenAll` sites are task-array and stdout/stderr drains, and the SQLite layer never fans out over a connection. |
| DI036 | Registration added after the provider was built | Warning | Warning | New in 3.6.0. The one build-then-register site is `HostBuilder.Build` (bootstrap `BuildServiceProvider()` at line 35, registrations after it), and the rule leaves it quiet: the collection is handed on (`ConfigureLogging(builder, …)`), the host builder is passed as an argument, and a later `builder.Build()` builds the same collection again. |
| DI037 | Un-awaited task escapes the scope that created it | Warning | Warning | New in 3.7.0. Needs a `using` scope in the same method — the tree creates no service scope at all. |

Full docs: <https://georgepwall1991.github.io/DependencyInjection.Lifetime.Analyzers/rules/>

> **The `Harbor` column is the tree-wide value.** One path overrides it — see
> §Path-scoped severity overrides below. So "DI006 → Warning" is the correct
> answer for `src/` and for `apps/Harbor.App.Cli/`, and **not** for
> `apps/Harbor.App.Avalonia/`, where a static `IServiceProvider` is a
> suggestion. If you are reading the table to decide whether a diagnostic is
> visible in your build, that exception is the thing to check first.

#### The eleven ids 2.18.24 did not have

The move to 3.7.8 adds eleven ids and removes none. Each one gets a row at the
severity the package ships it with, and the reasons above are the answers to
"what would this rule find here?":

* **DI023 and DI037** need a `using` service scope in the method that starts
  the work. `CreateScope()` and `CreateAsyncScope()` appear nowhere in the
  tree, so both rules have no input at all — not "no finding today", but no
  shape to find.
* **DI029, DI034, DI035** are the three platform-shaped rules: HttpClient
  lifetime (exempted here by `PooledConnectionLifetime`), `HttpContext` in
  fire-and-forget work (no ASP.NET in the tree), and a shared non-thread-safe
  service across a `Task.WhenAll` fan-out (no `DbContext`, no shared ADO.NET
  connection).
* **DI028** is the third member of the subscription-leak family: no callback
  is registered on a longer-lived source without keeping the registration.
* **DI030, DI031, DI033** ship as `Info` and are kept at `suggestion` — the
  same treatment the other `Info` rules in this family already get. DI033 had
  one finding: three registrations hand the container an instance it did not
  create, and the container does not dispose such an instance. It is written
  off with a reason rather than fixed, because the instances are pre-built on
  purpose and closing the gap properly needs a shutdown seam the composition
  root does not have.
* **DI032 and DI036** are the two `Warning` rules whose subject the tree does
  not have: no registered implementation implements `IAsyncDisposable` without
  `IDisposable`, and no registration lands after the provider is built.

**What was measured, and how.** The ids come out of the shipped analyzer
assemblies (the `DI###` strings in the assembly's string heap); the titles and
the default severities come out of the README the package ships, which lists
every rule with both. The "finds nothing here" column above was established
by reading the tree against each rule's own *Guardrails* section — the
package documents, per rule, which shapes it deliberately stays quiet about,
and the shapes this repository has are those.

### Path-scoped severity overrides

Every severity above is set by `[*.{cs,csx}]` in `.editorconfig` and applies to
the whole tree. Two sections scope a diagnostic's severity to a path instead,
and they are these two:

```ini
[apps/Harbor.App.Avalonia/**.cs]
dotnet_diagnostic.DI006.severity = suggestion   # static IServiceProvider cache
dotnet_diagnostic.DI008.severity = suggestion   # disposable transient service
dotnet_diagnostic.DI014.severity = suggestion   # root provider not disposed

[tests/**.cs]
dotnet_diagnostic.S5443.severity = none          # hardcoded temp path is the test's own fixture data
dotnet_diagnostic.S1215.severity = none          # GC.Collect is the setup of the allocation and finalization tests
dotnet_diagnostic.S1313.severity = none          # hardcoded IP addresses are the test's own data
dotnet_diagnostic.S5332.severity = none          # http is the fixture: an .invalid host or a local test server
dotnet_diagnostic.S3878.severity = none          # explicit array for a params overload, by convention
dotnet_diagnostic.S3220.severity = none          # overload shape, not a defect
```

**Why the Avalonia block:** an Avalonia composition root has to hand its container to the XAML
object graph, which cannot take constructor parameters, so the static root
provider is the idiom there. The CLI root resolves through
`Harbor.App.Cli/Hosting/HostBuilder.Build` instead and is deliberately **not**
in this list. The asymmetry is observed rather than assumed: #837 planted a
static container in the CLI root and the build failed with `error DI006` before
a single test ran, in the same session that found the Avalonia root's
equivalent compiling clean.

**Why the test block:** it is the measurement, not a preference. On
`dev@086ef777` (CI run 39472959027, the Release build of `Harbor.slnx`)
`SonarAnalyzer.CSharp` reported 476 sites in 40 rules, and every one of them
is under `tests/`: 0 under `src/`, 0 under `apps/`. The six rules above are
the ones whose findings are noise in a test tree and are worth something in a
product tree, so they stay on everywhere else. S5443 and S1313 want a
hardcoded path or address to be a defect, and in a test the hardcoded value is
the fixture; S1215 wants GC.Collect gone, and in the allocation and
finalization tests it is the setup; S5332 wants https, and the http URL is
the fixture, an `.invalid` host or a local server the test starts itself;
S3878 and S3220 are call style.
The 116 sites that remain under `tests/` are the ones worth reading.

**The consequence worth memorising:** the effective severity of a DI rule is a
function of *(rule, path)*, not of the rule alone. `App.Services` is the one
static `IServiceProvider` in `src/` + `apps/`; it is a `suggestion` by written
policy, not a rule that failed to fire. Anyone reading only the table above
will get this backwards, and did (#838).

> **Scope of that one, as of #779.** `App.Services` is the desktop composition
> root's *handover channel* and nothing else: `App.axaml.cs` builds `MainViewModel`,
> the theme services and `MainWindow` from it. It used to be read twelve more times
> by XAML view code-behinds — a reach-through no DI rule models, because those views
> read a global rather than declaring one. They now resolve through
> `IViewModelLocator`, obtained by `ShellLocator.Of(this)` walking up the logical
> tree to the `MainWindow` the root builds.
>
> That is not visible to any DI rule, so it is pinned by a source scan instead:
> `ServiceLocatorBoundaryRules.DesktopProduct_DoesNotReachTheAmbientContainerOutsideTheCompositionRoot`
> names `App.axaml.cs` + `Program.cs` as the only two files in `src/` + `apps/`
> allowed to say `App.Services`. The scan exists because the reflection sweep could
> not reach `apps/` (not a reference of the test project) and does not read static
> *properties* — two independent gaps, either of which alone would have left the
> rule unable to fail.

**Why each row exists, and what was removed at #865.** One sentence over a block
of rows is only auditable if it names them, so each row above carries its own
reason in `.editorconfig` and
`DiSeverityDemotionRules.PathScopedSection_NamesEveryRuleItSets` fails the build
when one does not. The block used to be **four** rules deep under a single
sentence — "relax DI rules for process-lifetime singletons" — that named three of
them. The fourth, **DI003 (captive dependency)**, was covered by neither the
sentence nor the parenthetical trying to enumerate the rest, and it is a
lifetime-*graph* rule about a singleton retaining a scoped or transient service,
which is not what "process-lifetime singletons" means.

DI003 is not in the list above any more. It was not removed because a strict
build proved a captive dependency absent — no build was run for this either — but
because reading the source settles it: `AddScoped` occurs **0** times in
`apps/Harbor.App.Avalonia`, and 0 times repo-wide under `src/` + `apps/`, so
there is no scoped service for a singleton to retain. The single transient
(`OnboardingViewModel`, the only `AddTransient` in the app) is resolved from the
root provider inside `App.ShowOnboardingThenMain` and captured by no constructor.
DI003 therefore has no site to hide here, and it stays at the tree-wide `error`
that `apps/Harbor.App.Cli` — the same class of composition root — is held to.
The breadth is still the whole app tree rather than the app root's own files;
that is unchanged and unremarkable now that the remaining three rows are each
about the root's own handover.

Three sibling blocks used to sit here for `apps/Harbor.App.{Wpf,Maui,Blazor}`.
They were removed in #838: none of those directories exists — those roots live
under `contrib/`, which is not in `Harbor.slnx` and is not built by CI — so the
blocks were inert configuration that read like live policy.
`AnalyzerSeverityScopeRules.PathScopedSections_ResolveToRealPaths` in
`tests/Harbor.Architecture.Tests/` fails if such a block reappears, and
`DiSeverityOverrides_AreScopedToExactlyTheDeclaredRoot` pins the Avalonia path
as the entire DI surface. The `tests/` block above is not part of that
inventory, because it relaxes no DI rule: it is a Sonar block, and the DI
inventory is a statement about the DI family.

### Threading rules (Microsoft.VisualStudio.Threading.Analyzers 18.12.1)

The Visual Studio SDK's threading/async analyzer family. 29 rules ship in the
package — `VSTHRD001`–`VSTHRD004`, `VSTHRD010`–`VSTHRD013`,
`VSTHRD100`–`VSTHRD117`, `VSTHRD200`–`VSTHRD202` (`VSTHRD201` is in the
package; the upstream index documents 28 of the 29). MIT licensed,
`developmentDependency` only, and it ships **no `lib/` folder and declares no
package dependencies** — it contributes analyzer assemblies and nothing else,
so nothing from it reaches a product binary or a consumer's dependency graph.

**Scope: `src/` and `apps/` only.** It is referenced from
`src/Directory.Build.props` and `apps/Directory.Build.props`, not from the
root file, because the root file is inherited by `tests/` too — and the test
tree has its own async culture (fixtures that block on purpose, 174 catch-all
handlers) and already lifts `TreatWarningsAsErrors`.

**Exactly one rule is on:**

| ID | Title | Harbor | Why |
|----|-------|--------|-----|
| **VSTHRD110** | Observe result of async calls | **suggestion** | The one rule this repository asks for. `suggestion` and not `error` because both sites it flags are places where the fault is already handled inside the callee: it is a guard on the future, not a fix of a present defect, and `error` would mean a red build on two places there is nothing to fix. |

**Every other rule is `none`, with a written reason** in `.editorconfig`.
That is not housekeeping — the package's defaults are not this repository's
defaults:

- **Four rules are `Error` by default** (`VSTHRD004`, `VSTHRD011`,
  `VSTHRD107`, `VSTHRD109`). Adding the package without demoting them turns
  existing code into a red build on the day it lands.
- **A large part of the family is written against `JoinableTaskFactory`**,
  which Harbor does not reference and does not want to: `VSTHRD004`,
  `VSTHRD011`, `VSTHRD012`, `VSTHRD013`, `VSTHRD105`–`VSTHRD109`,
  `VSTHRD115`, `VSTHRD201`.
- **`VSTHRD100` (async void)** would fire on the four Avalonia event
  handlers, which `docs/ANTIPATTERNS.md` §13 blesses.
- **`VSTHRD103`** duplicates `CS4014`, which the compiler already reports as
  an error here.
- **`VSTHRD116`/`VSTHRD117`** (`ThreadStatic`) would fire on the tree's eight
  `[ThreadStatic]` sites.

**The gate that actually decides "is this an unobserved Task?" is not the
package's.** It is `HARCH003`, a Roslyn analyzer in
`tests/Harbor.Architecture.Tests/Roslyn/UnobservedTaskAnalyzers.cs`, which
resolves the TYPE of the discarded expression instead of matching a call's
spelling — so it sees a `Task`-returning method whose name says nothing about
being asynchronous, and a type *derived* from `Task`, and it does not flag a
method named `…Async` that returns `int`. The pattern matcher it replaced
(`AvaloniaFireAndForgetRules.cs`) survives as the non-gating **oracle** the
differential compares against; see that file's header. Issue #1852.

### Public API freeze (`Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0)

The public-API freeze of #428 is a **compile-time** gate. The package is
referenced by exactly two projects — `Harbor.Abstractions` and
`Harbor.Abstractions.Contracts`, the two assemblies #428 freezes — with
`PrivateAssets=all`, so the RS00xx diagnostics exist in no other project in
the tree. There is no CI job for the freeze itself: the compiler runs it
while those two assemblies build, in the ordinary `build` job.

The baseline is the analyzer's own format, next to the code it describes:

| File | What it holds |
|---|---|
| `src/Harbor.Abstractions/PublicAPI.Shipped.txt` | the frozen surface |
| `src/Harbor.Abstractions/PublicAPI.Unshipped.txt` | the pending changes |
| `src/Harbor.Abstractions.Contracts/PublicAPI.Shipped.txt` | the frozen surface |
| `src/Harbor.Abstractions.Contracts/PublicAPI.Unshipped.txt` | the pending changes |

Both are picked up as analyzer additional files by the package's own
`buildTransitive` targets; nothing lists them in a `.csproj`. The first line
of each is `#nullable enable` — without it the analyzer cannot track
nullability at all (RS0037).

| Rule | What it says | Severity | Where it is configured | Why |
|---|---|---|---|---|
| RS0016 | a public symbol is not in the declared API | error | `.editorconfig` | the gate's core: an added member fails the build until the baseline names it |
| RS0017 | a declared API symbol is not in the code | error | `<WarningsAsErrors>` | the other half: a removed or renamed member fails the build until the baseline says so |
| RS0022 | a constructor makes a non-inheritable base inheritable | error | `.editorconfig` | it exposes the base's `protected` members |
| RS0024 | the public API files are invalid | error | `<WarningsAsErrors>` | a malformed baseline is not a baseline |
| RS0025 | a symbol appears twice in the public API files | error | `<WarningsAsErrors>` | a duplicate row is a row nobody reviewed twice |
| RS0026 | do not add several public overloads with optional parameters | error | `.editorconfig` | breaks compatibility when the API is extended |
| RS0027 | an API with optional parameters must have the most parameters among its overloads | error | `.editorconfig` | same back-compat family |
| RS0036 | a declared API symbol is missing its nullability annotations | error | `.editorconfig` | nullability is part of the signature a consumer compiles against |
| RS0037 | the API files must carry `#nullable enable` | error | `.editorconfig` | without it the analyzer tracks nothing about nullability |
| RS0041 | a public member uses an oblivious reference type | none | `.editorconfig` | off until the tree is fully annotated, per the analyzer's own guidance |
| RS0048 | an API file is missing or is not an analyzer additional file | error | `<WarningsAsErrors>` | without it the analyzer would silently track nothing |
| RS0050 | a symbol is marked removed but still exists in the code | error | `<WarningsAsErrors>` | the removal marker and the code must agree |

**Why two places, and not one.** Roslyn applies a per-rule `.editorconfig`
severity to a diagnostic **only when the diagnostic's location has a syntax
tree** (`AnalyzerDriver.GetFilteredDiagnostic` tests
`diagnostic.Location.SourceTree`). Six of the twelve rules are reported at a
symbol and are configured in `.editorconfig` as usual. The other five —
RS0017, RS0024, RS0025 and RS0050, which are reported **inside
`PublicAPI.*.txt`**, and RS0048, which is reported at **no location at
all** — cannot be reached that way: a `.editorconfig` row for them would
read like policy and change nothing. They are configured in the two frozen
projects' `.csproj` files with `<WarningsAsErrors>`, which the compiler
applies to a diagnostic **by its id, wherever it is reported**.

This was found by the non-vacuity job, not by reading the documentation: with
only the `.editorconfig` rows, the planted removal produced
`warning RS0017` and the build succeeded. The same job would have missed it
if it had been written to accept a warning.

**Why `severity = error` and not the global warnings-as-errors.** The
analyzer package's own `buildTransitive` props appends every RS id to
`WarningsNotAsErrors` when `CodeAnalysisTreatWarningsAsErrors` is false, so
relying on `TreatWarningsAsErrors=true` to promote them would make the gate
depend on a property **inside a package** rather than on a decision recorded
in this repository. The two mechanisms above do not go through that
promotion, so the gate stays an error even if the warnings-as-errors policy
is relaxed. The non-vacuity job proves it: it builds a throwaway project
**outside** the repository (so `Directory.Build.props` does not apply and
`TreatWarningsAsErrors` is **off**) with this repository's `.editorconfig`
and the frozen projects' `<WarningsAsErrors>` list copied in, and the planted
violation fails as `error RS0016`.

**What the analyzer cannot do.** It sees one tree. The old gate's rule was
about a diff — "commit the regenerated baseline in the SAME PR as the API
change" — and the reviewer's half of it was about a *commit*. That half
survived the migration as
[`tools/check-public-api-resign.py`](../tools/check-public-api-resign.py):
a commit that edits the frozen baseline must edit the assembly's sources
with it, and a commit that changes a public declaration must re-sign the
baseline with it. Its `--self-test` runs in the `docs.yml` `selftest` job;
its non-vacuity companion for the compile half is
[`tools/public-api-nonvacuity.sh`](../tools/public-api-nonvacuity.sh), run by
the `ci.yml` `public-api` job.

#### The parity contract (#1845)

The two python gates this replaced — `tools/check-public-api.py` (607 lines,
`Harbor.Abstractions.Contracts`) and `tools/check-public-api-abstractions.py`
(605 lines, `Harbor.Abstractions`) — were the same program with a different
scope. Their four rules were: a surface member with no baseline row fails
(ADDED), a baseline row with no surface member fails (REMOVED), a missing
baseline file fails (BASELINE-MISSING), and a scan that saw fewer members
than the floor fails (FLOOR). This table is the contract: every row says
what the python gate did and what covers it now.

| What the python gate checked | What covers it now |
|---|---|
| a public type or member with no baseline row → fail | RS0016, at compile time |
| a baseline row with no public member → fail (removal, re-sign) | RS0017, at compile time |
| a missing baseline file → fail | RS0048, at compile time |
| a malformed or duplicated baseline row → fail | RS0024, RS0025 |
| a scan that stopped matching → fail | RS0017 (every shipped row would be reported missing) + the row floor in `check-public-api-resign.py` |
| a `public` member of an `internal` type is not API | the analyzer computes effective accessibility, so it agrees |
| moves and re-wraps must not fail the gate | the analyzer compares symbols, not text |
| `const` values and enum values are part of the surface | the analyzer includes constant values in the API name |
| an explicit interface implementation is one member | the analyzer knows it is one member and renders it |
| `ref` / `out` / `in` / `params` are part of the signature | the analyzer renders them in the parameter list |
| a default parameter value is part of the signature | the analyzer renders it, and reasons about it (RS0026, RS0027) |
| generic arity is part of the signature | the analyzer renders `Foo<T>` and `Foo<T1, T2>` as different members |
| an `init` accessor is part of the signature | the analyzer knows accessors from properties; the text scan dropped the accessor list entirely |
| `readonly struct` / `record struct` are distinguishable | the analyzer knows the type kind |
| — (nothing checked this) | nullability: `string` → `string?` is a signature change (RS0036, RS0037) |
| — (nothing checked this) | a constructor that exposes the `protected` members of a non-inheritable base (RS0022) |
| — (nothing checked this) | overloads with optional parameters (RS0026, RS0027) |
| — (nothing checked this) | a `*REMOVED*` marker that disagrees with the code (RS0050) |
| — (not a python rule) | the re-signing pairing per COMMIT (`tools/check-public-api-resign.py`) |
| XML-doc coverage | unchanged: `tools/check-xml-doc-coverage.py`, which the analyzer has no rule for |

The floor's MEANING changed, and the change is the point of stating it.
Before: "the python scanner saw at least N members in the tree" — a ratchet
against a matcher that had silently stopped matching. After: "the frozen
baseline file still holds at least N rows" — a ratchet against a baseline
that was truncated or rewritten. The blindness the old floor guarded against
is now guarded by RS0017 itself. The two numbers are not comparable and were
never meant to be: the analyzer's rows include implicit constructors and
property accessors, which the text scan never counted because they have no
`public` keyword in the source.

### Exception rules (house gate, not a package)

The rule that a new exception thrown inside a catch must carry the caught
exception, and the rule that a custom exception type must be able to carry one
at all, are enforced by
[`tests/Harbor.Architecture.Tests/InnerExceptionRules.cs`](../tests/Harbor.Architecture.Tests/InnerExceptionRules.cs).
They are house rules rather than an analyzer package's rules, and the reason is
in the candidate table above: the package that shipped them
(`TheExceptionator.Analyzers`, rules EX003 and EX021) is not consumable here.

| Rule | What it catches |
|------|-----------------|
| `lost-inner-exception` | a `throw new X(…)` inside a catch that names the exception it caught, where the new exception does not receive that exception as an argument |
| `exception-without-inner-constructor` | a custom exception type that declares neither `(string message)` nor `(string message, Exception innerException)` |

**The two findings, and what was done about them.** Both were the same defect:
the message survived, the cause did not.

| Site | Before | After |
|------|--------|-------|
| `src/Harbor.PolicyConfig/Configuration/ConfigStore.cs` | `throw new InvalidDataException($"config.json is corrupt: {je.Message}")` | the same message, with `je` as the inner exception: the stack trace and the JSON position of the malformed key survive |
| `src/Harbor.Ipc.Client/Ide/IdeSessionBridge.cs` | `throw new IdeRpcException(IdeRpcException.InvalidParams, $"Malformed params: {ex.Message}")` | the same message and code, with `ex` as the inner exception |

Both fixes keep the text a user or an IDE sees: `ResultErrors.Message` reads
`ex.Message`, and the JSON-RPC error writer reads `ex.Message`.

**The control.** The rule's planted controls are the load-bearing part. The
offender (a catch that throws without the caught exception, in both the
interpolated-message spelling and the plain one) must be reported **exactly
once**, and the near-misses must be reported **not at all**: the throw that
carries the caught exception, the bare rethrow, the throw outside a catch, the
catch that names no variable, and the exception type that declares both
constructors. The runner asserts the count and not only the presence, because
"reports everything" and "reports nothing" are the same broken detector.

**What the rule does not cover, stated rather than left for a reader to
discover.** The first rule is syntactic: it sees a throw statement, an
enclosing catch clause and the argument list of the creation, and nothing else.
It does not see whether the exception that is passed is the one the catch
named, so `throw new X(ex.InnerException)` is reported (correctly: that is the
old exception's cause, not the one the catch named, and throwing it directly
loses the stack trace). The second rule is syntactic too: it reads the base
list and the constructor declarations, so a type that reaches `Exception`
through a base this rule cannot see is out of scope. The tree has none: all
four exception types under `src/` + `apps/` derive from `System.Exception`
directly.

### Public API freeze (`Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0)

The public-API freeze of #428 is a **compile-time** gate. The package is
referenced by exactly two projects — `Harbor.Abstractions` and
`Harbor.Abstractions.Contracts`, the two assemblies #428 freezes — with
`PrivateAssets=all`, so the RS00xx diagnostics exist in no other project in
the tree. There is no CI job for the freeze itself: the compiler runs it
while those two assemblies build, in the ordinary `build` job.

The baseline is the analyzer's own format, next to the code it describes:

| File | What it holds |
|---|---|
| `src/Harbor.Abstractions/PublicAPI.Shipped.txt` | the frozen surface |
| `src/Harbor.Abstractions/PublicAPI.Unshipped.txt` | the pending changes |
| `src/Harbor.Abstractions.Contracts/PublicAPI.Shipped.txt` | the frozen surface |
| `src/Harbor.Abstractions.Contracts/PublicAPI.Unshipped.txt` | the pending changes |

Both are picked up as analyzer additional files by the package's own
`buildTransitive` targets; nothing lists them in a `.csproj`. The first line
of each is `#nullable enable` — without it the analyzer cannot track
nullability at all (RS0037).

| Rule | What it says | Severity | Where it is configured | Why |
|---|---|---|---|---|
| RS0016 | a public symbol is not in the declared API | error | `.editorconfig` | the gate's core: an added member fails the build until the baseline names it |
| RS0017 | a declared API symbol is not in the code | error | `<WarningsAsErrors>` | the other half: a removed or renamed member fails the build until the baseline says so |
| RS0022 | a constructor makes a non-inheritable base inheritable | error | `.editorconfig` | it exposes the base's `protected` members |
| RS0024 | the public API files are invalid | error | `<WarningsAsErrors>` | a malformed baseline is not a baseline |
| RS0025 | a symbol appears twice in the public API files | error | `<WarningsAsErrors>` | a duplicate row is a row nobody reviewed twice |
| RS0026 | do not add several public overloads with optional parameters | error | `.editorconfig` | breaks compatibility when the API is extended |
| RS0027 | an API with optional parameters must have the most parameters among its overloads | error | `.editorconfig` | same back-compat family |
| RS0036 | a declared API symbol is missing its nullability annotations | error | `.editorconfig` | nullability is part of the signature a consumer compiles against |
| RS0037 | the API files must carry `#nullable enable` | error | `.editorconfig` | without it the analyzer tracks nothing about nullability |
| RS0041 | a public member uses an oblivious reference type | none | `.editorconfig` | off until the tree is fully annotated, per the analyzer's own guidance |
| RS0048 | an API file is missing or is not an analyzer additional file | error | `<WarningsAsErrors>` | without it the analyzer would silently track nothing |
| RS0050 | a symbol is marked removed but still exists in the code | error | `<WarningsAsErrors>` | the removal marker and the code must agree |

**Why two places, and not one.** Roslyn applies a per-rule `.editorconfig`
severity to a diagnostic **only when the diagnostic's location has a syntax
tree** (`AnalyzerDriver.GetFilteredDiagnostic` tests
`diagnostic.Location.SourceTree`). Six of the twelve rules are reported at a
symbol and are configured in `.editorconfig` as usual. The other five —
RS0017, RS0024, RS0025 and RS0050, which are reported **inside
`PublicAPI.*.txt`**, and RS0048, which is reported at **no location at
all** — cannot be reached that way: a `.editorconfig` row for them would
read like policy and change nothing. They are configured in the two frozen
projects' `.csproj` files with `<WarningsAsErrors>`, which the compiler
applies to a diagnostic **by its id, wherever it is reported**.

This was found by the non-vacuity job, not by reading the documentation: with
only the `.editorconfig` rows, the planted removal produced
`warning RS0017` and the build succeeded. The same job would have missed it
if it had been written to accept a warning.

**Why `severity = error` and not the global warnings-as-errors.** The
analyzer package's own `buildTransitive` props appends every RS id to
`WarningsNotAsErrors` when `CodeAnalysisTreatWarningsAsErrors` is false, so
relying on `TreatWarningsAsErrors=true` to promote them would make the gate
depend on a property **inside a package** rather than on a decision recorded
in this repository. The two mechanisms above do not go through that
promotion, so the gate stays an error even if the warnings-as-errors policy
is relaxed. The non-vacuity job proves it: it builds a throwaway project
**outside** the repository (so `Directory.Build.props` does not apply and
`TreatWarningsAsErrors` is **off**) with this repository's `.editorconfig`
and the frozen projects' `<WarningsAsErrors>` list copied in, and the planted
violation fails as `error RS0016`.

**What the analyzer cannot do.** It sees one tree. The old gate's rule was
about a diff — "commit the regenerated baseline in the SAME PR as the API
change" — and the reviewer's half of it was about a *commit*. That half
survived the migration as
[`tools/check-public-api-resign.py`](../tools/check-public-api-resign.py):
a commit that edits the frozen baseline must edit the assembly's sources
with it, and a commit that changes a public declaration must re-sign the
baseline with it. Its `--self-test` runs in the `docs.yml` `selftest` job;
its non-vacuity companion for the compile half is
[`tools/public-api-nonvacuity.sh`](../tools/public-api-nonvacuity.sh), run by
the `ci.yml` `public-api` job.

#### The parity contract (#1845)

The two python gates this replaced — `tools/check-public-api.py` (607 lines,
`Harbor.Abstractions.Contracts`) and `tools/check-public-api-abstractions.py`
(605 lines, `Harbor.Abstractions`) — were the same program with a different
scope. Their four rules were: a surface member with no baseline row fails
(ADDED), a baseline row with no surface member fails (REMOVED), a missing
baseline file fails (BASELINE-MISSING), and a scan that saw fewer members
than the floor fails (FLOOR). This table is the contract: every row says
what the python gate did and what covers it now.

| What the python gate checked | What covers it now |
|---|---|
| a public type or member with no baseline row → fail | RS0016, at compile time |
| a baseline row with no public member → fail (removal, re-sign) | RS0017, at compile time |
| a missing baseline file → fail | RS0048, at compile time |
| a malformed or duplicated baseline row → fail | RS0024, RS0025 |
| a scan that stopped matching → fail | RS0017 (every shipped row would be reported missing) + the row floor in `check-public-api-resign.py` |
| a `public` member of an `internal` type is not API | the analyzer computes effective accessibility, so it agrees |
| moves and re-wraps must not fail the gate | the analyzer compares symbols, not text |
| `const` values and enum values are part of the surface | the analyzer includes constant values in the API name |
| an explicit interface implementation is one member | the analyzer knows it is one member and renders it |
| `ref` / `out` / `in` / `params` are part of the signature | the analyzer renders them in the parameter list |
| a default parameter value is part of the signature | the analyzer renders it, and reasons about it (RS0026, RS0027) |
| generic arity is part of the signature | the analyzer renders `Foo<T>` and `Foo<T1, T2>` as different members |
| an `init` accessor is part of the signature | the analyzer knows accessors from properties; the text scan dropped the accessor list entirely |
| `readonly struct` / `record struct` are distinguishable | the analyzer knows the type kind |
| — (nothing checked this) | nullability: `string` → `string?` is a signature change (RS0036, RS0037) |
| — (nothing checked this) | a constructor that exposes the `protected` members of a non-inheritable base (RS0022) |
| — (nothing checked this) | overloads with optional parameters (RS0026, RS0027) |
| — (nothing checked this) | a `*REMOVED*` marker that disagrees with the code (RS0050) |
| — (not a python rule) | the re-signing pairing per COMMIT (`tools/check-public-api-resign.py`) |
| XML-doc coverage | unchanged: `tools/check-xml-doc-coverage.py`, which the analyzer has no rule for |

The floor's MEANING changed, and the change is the point of stating it.
Before: "the python scanner saw at least N members in the tree" — a ratchet
against a matcher that had silently stopped matching. After: "the frozen
baseline file still holds at least N rows" — a ratchet against a baseline
that was truncated or rewritten. The blindness the old floor guarded against
is now guarded by RS0017 itself. The two numbers are not comparable and were
never meant to be: the analyzer's rows include implicit constructors and
property accessors, which the text scan never counted because they have no
`public` keyword in the source.

### Banned APIs (`BannedSymbols.txt`, `BannedSymbols.DynamicCode.txt`)

[`BannedSymbols.txt`](../BannedSymbols.txt) is the RS0030 rule set — the
mechanical expression of the "What NOT to do" list in CLAUDE.md. It is wired in
`Directory.Build.props` via `<AdditionalFiles Include="BannedSymbols.txt" />`.

> **The filename is load-bearing.** The analyzer reads files named
> `BannedSymbols.txt` or `BannedSymbols.*.txt` — nothing else. The rules sat
> dead under the old name `BannedApi.txt` until ROP-D Z2, which both renamed the
> file and added the `AdditionalFiles` entry — two independent reasons for it to
> be invisible. If you rename it, the whole rule set silently stops firing.

What it bans, and why each one is a hard rule rather than a style preference:

| Rule | Reason |
|---|---|
| `Newtonsoft.Json.*`, `DataContractJsonSerializer`, `JavaScriptSerializer` | `System.Text.Json` only — one serializer, and it is AOT-compatible. |
| `XmlSerializer` | Reflection-based; breaks NativeAOT. Use `System.Text.Json` or source-gen. |
| `Microsoft.Win32.Registry` | Windows-only; the harness is cross-platform. |
| `BlockingCollection<T>.Add` | Unbounded enqueue under a lock — use `TryAdd`. |

Scope and enforcement:

- **Errors** everywhere except `tests/` and `samples/` — `TreatWarningsAsErrors`
  is on globally, so a new banned call fails the build.
- **Silenced** under any project path containing `tests` or `samples`
  (`<NoWarn>$(NoWarn);RS0030</NoWarn>` in `Directory.Build.props`). The stated
  reason is that benchmark `GlobalSetup`/`IterationSetup`, Avalonia headless
  marshaling helpers and IPC fixtures block on async setup by design, and
  failing CI on those would train people to ignore the rule. Be aware this is
  `NoWarn`, not a severity downgrade: nothing about the rule is visible in test
  builds, so review is the only backstop there.

**Adding an exemption.** A legacy call site that cannot be fixed yet carries
`#pragma warning disable RS0030 // <reason>` and is catalogued by class in
`BannedSymbols.txt` under "ACCEPTED LEGACY EXEMPTIONS (ROP-D Z2 catalogue)".
If you add a pragma, add the catalog entry in the same commit — an uncatalogued
pragma is indistinguishable from a suppression someone forgot to remove.
`AGENTS.md` §"What NOT to do" forbids blanket `#pragma warning disable`; a
single-rule, commented, catalogued exemption is the only accepted form.

### The dynamic-code ban (`BannedSymbols.DynamicCode.txt`)

[`BannedSymbols.DynamicCode.txt`](../BannedSymbols.DynamicCode.txt) is the second
RS0030 rule set: the dynamic-code convention of
[#626](https://github.com/refusedguy/Harbor-Harness/issues/626) — no shipped
product file outside `src/Harbor.Plugins.*` loads an assembly or emits IL at run
time. It used to be a hand-written regex over the source text
(`tests/Harbor.Architecture.Tests/ReflectionConventionRule.cs`, plus a comment
stripper and ten planted controls); since
[#1844](https://github.com/refusedguy/Harbor-Harness/issues/1844) the compiler
enforces it. The migration added **no package** — the analyzer was already
referenced solution-wide.

| Rule | Reason |
|---|---|
| `Assembly.Load` / `LoadFrom` / `LoadFile` (all seven public overloads) | A run-time-loaded assembly is in no `<ProjectReference>` and invisible to NativeAOT. |
| `System.Runtime.Loader.AssemblyLoadContext` | The load side of the same door. |
| `System.Reflection.Emit` (namespace, plus `TypeBuilder` and `DynamicMethod`) | The manufacture-code side: IL the compiler never saw. |

Two things about this file that are easy to get wrong:

- **An ID with no parameter list matches only the parameterless overload.**
  `M:System.Reflection.Assembly.Load` on its own bans nothing, because every
  `Assembly.Load` overload takes an argument and Roslyn's ID resolver requires
  the parameter count to match exactly. Every overload is listed with its
  parameter list, and `The_Ban_File_Covers_Every_Public_Assembly_Load_Overload`
  fails when the list stops covering the framework's surface.
- **`AppDomain.DefineDynamicAssembly` is deliberately absent.** It is not public
  API in .NET 10, so the compiler rejects the call before any analyzer sees it;
  a ban entry for it would resolve to no symbol and enforce nothing. Its live
  spelling, `System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly`, is in
  the banned namespace.

**The plugin exception is expressed by the tool.** The ban file is **not added**
to the `src/Harbor.Plugins.*` projects' `<AdditionalFiles>`, so the compiler
never sees the rule there:

```xml
<AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.DynamicCode.txt"
                 Condition="!$(MSBuildProjectName.StartsWith('$(HarborDynamicCodeBanExemptProjectNamePrefix)'))" />
```

That is the absence of a file — something a tool enforces — rather than a
baseline a scanner subtracts, which a reader has to believe. The prefix lives in
one property (`HarborDynamicCodeBanExemptProjectNamePrefix`) so the wiring and
the architecture test read the same fact.

The dynamic-code rule has no pragma form: its exemption is the plugin family,
and it is expressed by leaving the ban file out of that family's projects.

### Excubo DI validation rules (Excubo.Analyzers.DependencyInjectionValidation 1.0.33)

Validates `[Exposes(typeof(T))]` attribute declarations on composition root
methods. The attribute is in `Excubo.Analyzers.DependencyInjection` namespace
(from the `Excubo.Analyzers.Annotations` package).

| ID | Title | Default | Harbor |
|----|-------|---------|--------|
| EDI01 | Too many service extensions | Warning | Suggestion |
| EDI02 | Missing service extension | Warning | Suggestion |
| EDI03 | Incomplete service extension | Warning | Suggestion |
| EDI04 | Missing dependency | Warning | Suggestion |
| ADP0001 | Analyzer exception | Warning | Suggestion |

All demoted to Suggestion because the analyzer's flow analysis only follows
the immediate method body — when registration is split across private helpers
(as in `HostBuilder.RegisterCore` / `RegisterRegistries` / etc.), it emits
false positives. **Real coverage is the DI tests** (see section 2 below).

Available attributes from `Excubo.Analyzers.DependencyInjection`:

| Attribute | Purpose |
|-----------|---------|
| `[Exposes(typeof(T))]` | Declares that the method adds T to the DI container. |
| `[Injects(typeof(T))]` | Declares that the method resolves T from the container. |
| `[As(typeof(T))]` | Like Exposes but for `As<T>` semantic (rarely used). |
| `[IgnoreDependency(typeof(T))]` | Suppress EDI04 for the given dep type. |
| `[DependencyInjectionPoint]` | Marks a method as a DI entry point (single attribute per method). |

### Project-file rules (`DotNetProjectFile.Analyzers` 1.19.1)

This analyzer is the odd one out in the table above: it does not look at C# at
all. It reads the **MSBuild project files** — every `.csproj`, the nearest
`Directory.Build.props`, the nearest `Directory.Packages.props`, `global.json`
and the `.resx` files — and reports on them. The package ships 193 rules
(`Proj0001`–`Proj6014`).

**Three of them are adopted, and they are errors:**

| ID | Title | Why it is an error |
|----|-------|--------------------|
| **Proj0037** | Exclude runtime when all assets are private | A `PrivateAssets=all` package has no runtime assembly; without `ExcludeAssets=runtime` the IDE offers a type the program cannot load. Fixed at 26 `PackageReference` sites, one `ExcludeAssets` line each, plus one scoped `NoWarn` (see below). |
| **Proj0040** | Run NuGet security audits on transitive dependencies too | The SDK default is `direct` **below net10.0**; NuGet 7.0 (the .NET 10 SDK) made `all` the default for a project that targets `net10.0` or higher, so on this tree the SDK default is already `all` and the pin is what keeps it there. Harbor pins transitive versions on purpose (`CentralPackageTransitivePinningEnabled`), so a transitive advisory is Harbor's subject. Fixed by pinning `NuGetAuditMode=all` in `Directory.Build.props`. |
| **Proj0041** | NuGet audit level must be at least moderate | The SDK default is `low`, which reports EVERY advisory: NU1901 (low), NU1902 (moderate), NU1903 (high), NU1904 (critical). The pin to `moderate` keeps NU1902/NU1903/NU1904 live and drops NU1901. That is a deliberate narrowing, because under `TreatWarningsAsErrors=true` a low-severity advisory in a transitive package this repository does not control would break every build the day it is published, and the severities this repository has actually had to act on are the moderate and high ones (the MessagePack and ImageSharp advisories, whose per-project `NU1902;NU1903` suppressions #1911 removed). Fixed by pinning `NuGetAuditLevel=moderate` in `Directory.Build.props`. |

**The SDK default the two pins are written against, and where it changed.**
NuGet 7.0 (the .NET 10 SDK) changed the default of `NuGetAuditMode`: it is
`all` for a project that targets `net10.0` or higher, and `direct` below
that. Harbor targets `net10.0`, so the effective default on this repository
is `all` and the pin of `all` is not what turns the audit on. It is what
makes the gate a property of the repository instead of a property of the SDK.
The rule's own documentation still carries the pre-NuGet-7.0 wording ("by
default … only direct dependencies are taken into consideration"), which is
where the earlier version of this table got the default wrong.

**What Proj0041 actually enforces.** The rule's title says the level "must be
at least moderate", and its shipped implementation tests
`level > Moderate`, so it reports `high` and `critical`, the two values that
narrow the coverage below the moderate floor, and it does NOT report a
missing or `low` level. `moderate` and `low` both pass it. The pin to
`moderate` is the floor the rule names, and the one value that keeps the
advisories this repository has had to act on inside the gate.

**One rule is demoted because it contradicts this repository:**

| ID | Title | Severity | Why |
|----|-------|----------|-----|
| Proj0039 | Treat all warnings as errors is a bad practice | none | The zero-warning gate (`TreatWarningsAsErrors=true` in `Directory.Build.props`) is load-bearing here. Rejected with a reason, not silently. |

**Two rules stay visible because the decision is the owner's, not the build's:**

| ID | Title | Severity | Why |
|----|-------|----------|-----|
| Proj0043 | Use lock files | suggestion | **Decided on 2026-10-20 (#1857): not adopted, and the tree stays all-or-nothing.** The measured reason is in `docs/CENTRAL_PACKAGE_MANAGEMENT.md` §14: the restore is already reproducible (196 packages in the graph, 0 of the 68 version ranges floating or open at the bottom), so a lock file would add a detector rather than a fix, at the cost of one file per project that every dependabot PR has to reconcile. The rule stays visible so the question is not forgotten. |
| Proj0044 | Enable `RestoreLockedMode` when `ContinuousIntegrationBuild` is on | suggestion | Follows Proj0043; both wait on the same decision. `RestoreLockedMode` without lock files is a restore that fails, which is why `tools/check-test-packages.py` reports that state (`RESTORE-LOCKED-WITHOUT-FILES`) rather than letting it reach CI. |

**The gate that keeps the lock-file decision honest** is
`tools/check-test-packages.py` (the `test-packages` CI job). It reports four
states the tree must never be in: a lock file nobody regenerates
(`RESTORE-INPUTS-UNENFORCED`), the property set with no lock file
(`RESTORE-INPUTS-UNWRITTEN`), locked mode without lock files
(`RESTORE-LOCKED-WITHOUT-FILES`), and a scope where some projects carry a
lock file and others do not (`RESTORE-INPUTS-PARTIAL`). Its `--self-test`
carries a fixture for each, so the gate is known to bite.

**Everything else is off, family by family, with the reason written next to it**
in [`/Directory.Build.globalconfig`](../Directory.Build.globalconfig).

#### Why the severity table is not in `.editorconfig`

A diagnostic reported on a project file reaches the compiler as an **additional
file**, and Roslyn resolves a diagnostic's severity in this order
(`CSharpDiagnosticFilter.GetDiagnosticReport` in `dotnet/roslyn`):

1. command line (`/nowarn`, `/warnaserror`)
2. editor config, **syntax tree** level — needs `diagnostic.Location.SourceTree`
3. global analyzer config, **compilation** level

An additional-file diagnostic has no source tree, so step 2 never applies to it.
A `dotnet_diagnostic.ProjXXXX.severity` line in `.editorconfig` is therefore
**ignored** for these rules, and so is the category-scoped bulk form
(`dotnet_analyzer_diagnostic.category-<X>.severity`), which is gated on the same
thing. A **global analyzer config** is the only per-rule mechanism that reaches
them — which is what the package's own documentation points at.

The file is added to `EditorConfigFiles` in `Directory.Build.props` — the
item the C# task passes to the compiler as `/analyzerconfig:` — rather than
left to the SDK's discovery, because the SDK discovers a `.globalconfig` by
walking up from the **compiled sources**: a project that compiles no source
file of its own (a meta-package like `src/Harbor.Ui.Framework`, a test
project whose sources moved into `Harbor.TestKit`) discovers nothing at all,
and every rule in the table falls back to its shipped default, which is
`Warning` — promoted to an error by `TreatWarningsAsErrors`.

It is named `Directory.Build.globalconfig` rather than `.globalconfig` so the
SDK's discovery does not add the same file a second time: two copies of one
file make Roslyn report a duplicate-key warning for every key in it, and that
warning is an error here. `global_level = 100` puts it above anything a
package ships (the dotnet-project-file-analyzers package's own global config
sits at `-1`).

The file sits at the repository root and is the only place in this repository
where a diagnostic severity is written down outside `.editorconfig`.

#### The two places Proj0037 is scoped instead of fixed

`src/Harbor.CodeGen/Harbor.CodeGen.csproj` and
`tests/Harbor.TestKit.Generators/Harbor.TestKit.Generators.csproj` reference
`Microsoft.CodeAnalysis.CSharp` with `PrivateAssets=all`, and the rule asks for
`ExcludeAssets=runtime` there. It does not hold: both generators are compiled
against the pinned Roslyn while the compiler that loads them is the SDK's own,
and the local copy of the Roslyn runtime is what lets a generator load against
the version it was compiled against. The finding is scoped at those two
elements with `NoWarn="Proj0037"` and a comment, rather than silenced
globally. The canonical fix (exclude the runtime asset) lands when the
repository's Roslyn pin and the SDK's Roslyn are aligned — and as of #1897
they are: the pin is 5.9.0 and the SDK 10.0.402 ships Roslyn 5.9.0. The fix
is not taken here, because changing how a source generator is loaded is its
own change with its own verification.

#### `ExcludeAssets` has to be an attribute

Proj0037 reads the **attribute** form —
`reference.Element.Attribute("ExcludeAssets")` — and ignores the child element
`<ExcludeAssets>runtime</ExcludeAssets>` that NuGet also accepts. The first
run of this rule reported 10 sites in `Directory.Build.props` that carried the
child element, one per project that compiled. NuGet reads both forms as the
same item metadata, so the attribute form is the one that satisfies the rule
and it is what the tree now carries.

---

## 2. DI registration tests

Each composition root has a dedicated test project under
`tests/Harbor.App.*.Tests/` that builds the host and asserts every
`[Exposes(typeof(T))]`-declared service is resolvable.

> Since sprint-2, `Harbor.App.{Wpf,Maui,Blazor}.Tests` live in `contrib/tests/`
> (build via `contrib/Contrib.slnx`); Cli/Avalonia test projects remain in `tests/`.

| Test project | Composition root | TFM |
|--------------|------------------|-----|
| `Harbor.App.Cli.Tests` | `Harbor.App.Cli.Hosting.HostBuilder.Build` | `net10.0` |
| `Harbor.App.Avalonia.Tests` | `Harbor.App.Avalonia.AppHost.BuildAsync` | `net10.0` |
| `Harbor.App.Wpf.Tests` | `Harbor.App.Wpf.App.BuildHostInternal` | `net10.0-windows10.0.19041` |
| `Harbor.App.Blazor.Tests` | `Harbor.App.Blazor.Program.BuildApp` | `net10.0` |
| `Harbor.App.Maui.Tests` | `Harbor.App.Maui.MauiProgram.CreateMauiApp` | `net10.0-windows` (MAUI workload) |

Each project has:

- **Per-service `[Test]` methods** — one assertion per registered interface
  / class. A failure pinpoints exactly which registration broke.
- **`Build_AllDeclaredServices_Resolvable` aggregate** — resolves the full
  list in one test. Useful as a single signal for CI dashboards.
- **Lifetime / singleton sharing test (CLI)** — `Build_Singletons_AreSharedInstances`
  verifies that resolving `IEventBus`, `IToolRegistry`, `IProviderRegistry`
  twice returns the same instance.

### How to extend

When adding a new service registration to any composition root:

1. Add `builder.Services.AddSingleton<IFoo, Foo>();` in the `Register*` method.
2. Add `[Exposes(typeof(IFoo))]` to the composition root's `Build` method
   (keep the attribute list in sync with the actual registrations).
3. Add a `[Test] public async Task Build_Registers_IFoo()` to the matching
   `tests/Harbor.App.*.Tests/*DiTests.cs` file (Wpf/Maui/Blazor: under
   `contrib/tests/`).
4. Add `typeof(IFoo)` to the `required` array in the aggregate test
   (`Build_AllDeclaredServices_Resolvable`).

The DI tests will fail at PR time if a registration is accidentally removed.

---

## 3. Architecture tests (replaced `dotnet-arch-analyzer`, ROP-D Z2)

The optional external `dotnet-arch` tool and its `dotnetarch.json` config were
**removed** in ROP-D: the config had rotted (it still listed the deleted
Harbor.Domain, per-tool Harbor.Tools.* projects that were merged into
Harbor.Tools.Builtin, contrib TUI renderers, and Wpf/Maui apps — while missing
every Ipc.*, Ui.Framework.* and Desktop.* project), and it duplicated rules
that are already enforced mechanically.

Canonical enforcement now lives in `tests/Harbor.Architecture.Tests/`
(reflection-based + NetArchTest + the ROP-D full-project matrix in
`FullLayerMatrixTests.cs`). Every `src/` project is either on that matrix or
listed in `OutOfScopeAssemblies` with a reason; two further folders
(`Providers.Shared`, `Storage.Shared`) produce no assembly at all and are listed
in `SharedSourceFolders` instead, which `SharedSourceLinkRules` holds against the
real csproj link items in both directions. It runs
in the `test` job's `core` shard and again inside the Release build itself
(`HarborArchitectureGate`) — no extra tool install, single source of truth. See
docs/ARCHITECTURE_LAYERS.md §5 for the rule catalogue.

---

## 4. ReferenceTrimmer — the unused-reference gate

**ReferenceTrimmer 3.5.13** (MIT) is a build-time analyzer that asks Roslyn
which assemblies the compiled code actually uses, and reports the declared
references that none of them reach:

| Rule | Meaning |
|------|---------|
| `RT0000` | documentation generation is off, so the answer is less accurate |
| `RT0001` | a bare `<Reference>` the compiler never uses |
| `RT0002` | a `<ProjectReference>` the compiler never uses |
| `RT0003` | a `<PackageReference>` the compiler never uses |

It is **off by default and on in exactly one place**: the `reference-trimmer`
job in `.github/workflows/ci.yml` sets the `HarborReferenceTrimmer` property,
which is what makes `Directory.Build.props` add the `<PackageReference>`. The
ordinary build is untouched.

It is a separate job rather than a build step for three reasons: the analysis
costs a semantic pass over every compilation; its findings are warnings, and
this repository sets `TreatWarningsAsErrors=true`, so the package would break
the build the day it was added; and the tool sees source only, so it cannot
see reflection, DI registration by convention or a plugin loaded by name.

The gate is [`tools/check-reference-trimmer.py`](../tools/check-reference-trimmer.py)
and its ratchet is
[`tools/reference-trimmer-baseline.txt`](../tools/reference-trimmer-baseline.txt):
a finding the baseline does not carry is red, and a row the tree no longer
produces is red too, so the baseline can only shrink.

The measured run, the 60 findings it produced, the six families of false
positive and the two dead references it closed are in
[docs/REFERENCE-TRIMMER.md](./REFERENCE-TRIMMER.md).

---

### Stored container in an instance field — not a defect (#760)

A container kept in an **instance** field is not the same defect as one in a
**static** field, and no DI rule treats it as one. That is the analyzer's own
position, not an oversight: DI006's README offers `private readonly
IServiceProvider _provider;` as the "Better pattern" that replaces a static
provider cache. The static form is what DI006 owns, and tree-wide DI006 is
`warning`, which `TreatWarningsAsErrors` (set in `Directory.Build.props`)
promotes to a build error. **One path is exempt** — the Avalonia desktop root,
where DI006 is `suggestion` by written policy, so `App.Services` compiles clean
there; see §Path-scoped severity overrides. Read as a blanket rule this sentence
is what sent #838 looking for a hole in the rule that was never there.

The reason the instance form is fine is that the danger is not the field, it is
the **owner**. A singleton that retains a container and hands out a *scoped*
service from it is a captive dependency — the scoped instance then lives as long
as the singleton. That class is enforced, at the strictest level, by the rules
that can see the lifetime graph rather than the field shape:

- **DI003** (captive dependency) → `error`
- **DI019** (scoped service resolved from root provider) → `error`
- DI002 / DI004 / DI001 → `warning`

No new analyzer axis is needed for that, and none is added. (The #555 freeze
was lifted on 2026-10-20: opening an extension axis is additive rather than
forbidden — but it has to be declared in `PluginAxisRegistry` and wired, and an
analyzer axis is its own decision, taken in its own issue.) The one container
shape a reflection sweep *can* own is a stored
`IServiceScope` — unlike a container, a concrete scope has no innocent owner,
because whoever holds it also owns its disposal. That is
`ServiceLocatorBoundaryRules.SrcAssemblies_StoreNoServiceScope`, swept over the
whole `src/` tree. `IServiceScopeFactory` is deliberately excluded: a scope
factory on a singleton is the correct per-unit-of-work idiom, and both DI011 and
DI019 list it as a sanctioned exception.

Measured on `dev`: `src/` stores a container in exactly two types
(`ViewModelLocator`, the named locator abstraction; `HarborIpcServer`, an IPC
host's own bootstrap constructor, which resolves what it needs and retains
nothing) and stores **no** service scope in any type. That inventory is asserted
by `StoredLocatorInventory_IsExactlyTheDeclaredBaseline`, so the previously
undetected surface is a number in a test rather than an assumption.

## CI integration

### `dotnet build`

All analyzers run as part of `dotnet build` (they're PackageReferences with
`PrivateAssets="all"`, so they ship with the project's compilation). The
`build` job runs `dotnet build Harbor.slnx -c Release`, and
`TreatWarningsAsErrors` is set in `Directory.Build.props` — **not** by a
`--warnaserror` flag on the command line, which CI deliberately does not pass
(a global property would override `tests/Directory.Build.props`, which turns
that switch off on purpose). So `warning` is a build error in `src/`, in
`apps/`, and in `tools/`; `suggestion` is not promoted and does not appear in
the build log at all. The effective severity of a DI rule, then, is a function
of *(rule, path)* — see §Path-scoped severity overrides.

| Severity | DI rules | Effect on the build |
|----------|----------|---------------------|
| `error` | DI003, DI013, DI015, DI017, DI019 | build error (also an error at `suggestion`-less defaults) |
| `warning` | DI001, DI002, DI004, DI005, DI006, DI008, DI009, DI014, DI018, DI020, DI021, DI024, DI025, DI027 | visible, and promoted to an error by `TreatWarningsAsErrors` |
| `suggestion` | DI007, DI010, DI011, DI012, DI016, DI022, DI026 | not in the build log; IDE-surfaced at most, hidden by default in most IDEs |
| `suggestion`, one path only | DI006, DI008, DI014 under `apps/Harbor.App.Avalonia/` | as above, in the Avalonia desktop root |

- Excubo EDI rules and `ADP0001` → `suggestion` throughout.
- `.editorconfig` is the source of truth for all of the above. This table
  summarises it; it does not define it. Two guards keep the two in step:
  `AnalyzerSeverityScopeRules.DiSeverityOverrides_AreScopedToExactlyTheDeclaredRoot`
  and `...AnalyzerDoc_NamesEveryPathScopedDiOverride`.

### Tests

The DI test projects (`Harbor.App.Cli.Tests`, `Harbor.App.Avalonia.Tests`) run
in the `test` job's shard matrix, one project at a time, as plain executables:

```bash
dotnet run --project tests/Harbor.App.Cli.Tests -c Release --no-build
```

**Not** `dotnet test`, which no CI job runs — so its behaviour here is
unverified rather than known. The reason recorded for a long time in `ci.yml`
and `docs/DEVELOPMENT.md` ("discovers ZERO tests … the MTP host exits 5 with a
silent discovery error") was **wrong**: exit 5 is the MTP *invalid command-line
arguments* code, and a run that genuinely discovers no tests exits **8**, so
exit 5 was never evidence of zero discovery. `global.json` selects the
Microsoft.Testing.Platform runner, which rejects the VSTest-era options
(`--logger`, `--filter`) the old commands passed; `CHANGELOG.md` (sprint
*ci-cd-maturity*) records that deleting one `--logger` turned
`renderer-perf-gate.yml` green. No project references `Microsoft.NET.Test.Sdk`
any more — #1893 removed it from all 16 test projects and from the central pin,
because TUnit documents it as stopping discovery. See
[CONTRIBUTING.md §Why not `dotnet test`](../CONTRIBUTING.md#why-not-dotnet-test).
The same assemblies run green via direct host execution.
`tests/Harbor.Architecture.Tests` additionally re-runs inside the Release build
itself, via the `HarborArchitectureGate` target in `Directory.Build.props`.

### WPF / MAUI / Blazor

The `Harbor.App.{Wpf,Maui,Blazor}` composition roots and their test projects
live under `contrib/`, which is **unmaintained and not compiled by CI**: they
are not in `Harbor.slnx`, and `contrib/Contrib.slnx` is not built by any
workflow. So there is nothing to exclude from a Linux test list and no
`maui-windows` workload for CI to install — the two projects that do run are
`Harbor.App.Cli.Tests` and `Harbor.App.Avalonia.Tests`, both plain `net10.0`.

(This section used to instruct readers to exclude `Harbor.App.Wpf.Tests` from
`dotnet test` and warned about MAUI workloads. Both instructions were aimed at
a CI list those projects were never in. The same fossil sat in `.editorconfig`,
which carried DI relaxations for all three paths — see §Path-scoped severity
overrides. #838.)

---

## How to fix common violations

### `DI003 Captive dependency`

A Singleton depends on a Scoped/Transient service. The Singleton captures
the transient instance for its entire lifetime, defeating the per-request
semantics you intended.

```csharp
// BAD — Singleton holds a Scoped service.
services.AddSingleton<IFoo>(sp => new Foo(sp.GetRequiredService<IScopedBar>()));
services.AddScoped<IScopedBar, Bar>();

// FIX 1 — make IFoo Scoped too.
services.AddScoped<IFoo>(sp => new Foo(sp.GetRequiredService<IScopedBar>()));

// FIX 2 — inject IServiceProvider and resolve per-call (use sparingly).
services.AddSingleton<IFoo>(sp => new Foo(sp));
class Foo { Foo(IServiceProvider sp) { _sp = sp; } void Run() { _sp.GetRequiredService<IScopedBar>(); } }
```

### `DI015 Unresolvable dependency`

A registered service has a constructor parameter that isn't registered.

```csharp
services.AddSingleton<IFoo, Foo>();
// Foo ctor takes IBar but IBar isn't registered → DI015.
// FIX: register IBar.
services.AddSingleton<IBar, Bar>();
services.AddSingleton<IFoo, Foo>();
```

### `DI017 Circular dependency`

`A` depends on `B` and `B` depends on `A`. The DI container can't construct
either one.

```csharp
services.AddSingleton<A>();  // A ctor takes B
services.AddSingleton<B>();  // B ctor takes A — DI017.

// FIX: refactor to break the cycle. Usually one side should depend on an
// abstraction and use an event/callback instead of the concrete type.
```

### `DI019 Scoped service resolved from root`

```csharp
var sp = services.BuildServiceProvider();
var scoped = sp.GetRequiredService<IScopedFoo>();  // DI019 — scoped resolved from root.

// FIX: create a scope first.
using var scope = sp.CreateScope();
var scoped = scope.ServiceProvider.GetRequiredService<IScopedFoo>();
```

### `EDI02 Missing service extension` / `EDI03 Incomplete service extension`

The Excubo analyzer couldn't match `[Exposes(typeof(T))]` to a
`services.AddXxx<T>()` call in the same method body. Often a false positive
when registration is split across private helpers (as in Harbor's
`HostBuilder`). To silence:

1. Move the `services.AddXxx<T>()` call into the same method as the
   `[Exposes]` attribute, OR
2. Demote to suggestion (already done in `.editorconfig`), OR
3. Suppress inline with `#pragma warning disable EDI02`.

---

## Adding a new analyzer

The order of these steps is the point. A new analyzer is landed **with a
baseline**, never with a finding, because `TreatWarningsAsErrors=true` turns
every pre-existing finding into a red build on the day the package lands.

1. Pin the version in `Directory.Packages.props`:
   ```xml
   <PackageVersion Include="NewAnalyzer" Version="x.y.z" />
   ```
2. Add the `PackageReference` (with `PrivateAssets="all"`) either to
   `Directory.Build.props` (solution-wide) or the relevant
   `apps/Directory.Build.props` / `tests/Directory.Build.props`.
3. Build with `-p:ReportAnalyzer=true` and write down **every** diagnostic id
   the package ships, with the severity it ships it with. Do not enable
   anything yet: the package's defaults are not this repository's defaults.
   Four of the 29 `VSTHRD` rules ship as `error` by default, and three of the
   `MA` rules ship as `warning` reading a `BannedSyntaxes.txt` this repository
   does not have.
4. Put every id into the severity table at the severity the package ships it
   with, each with a written reason (see [below](#where-the-severity-table-lives)).
   This is the baseline, and it is what keeps the build green while the
   findings are still there.
5. Fix or baseline each finding. A rule may not be raised to `error` on this
   tree until its findings are gone, so every finding is either fixed in code
   or carries a row that says why it is tolerated.
6. Promote one rule at a time: fix the finding, raise that rule's severity,
   and update this document's table for it in the same commit.
7. Measure the `Compile` step before and after. It was 372.5 s on the
   4-vCPU runner of #1801, and it is 69 % of the `build` job. A move that
   grows it by more than 10 % does not stay.
8. Run `dotnet build` and confirm 0 errors.

### Where the severity table lives

| File | What goes in it | Why it is there |
|------|-----------------|-----------------|
| `.editorconfig`, section `[*.{cs,csx}]` | every diagnostic reported on a C# source tree, tree-wide | Roslyn resolves the severity of a source-tree diagnostic from the editor config. |
| `.editorconfig`, a path section such as `[apps/Harbor.App.Avalonia/**.cs]` or `[tests/**.cs]` | the same, but only for one directory | Two such sections exist today: the Avalonia DI block and the Sonar block under `tests/`. `AnalyzerSeverityScopeRules` pins the set of paths and fails if a path-scoped section addresses a directory that is not in the checkout. |
| [`/Directory.Build.globalconfig`](../Directory.Build.globalconfig) | every diagnostic reported on an additional file, which today means the project-file rules | An additional-file diagnostic has no `SourceTree`, and Roslyn applies an editor-config severity only to a diagnostic that has one. A global analyzer config is the only per-rule mechanism that reaches them. |

A severity written anywhere else reaches nothing. That is not a style rule:
the two `.editorconfig` lines that used to configure `REL0001` and `REL0002`
configured ids that do not exist in the package, so they configured nothing at
all, and the rules they were meant to silence had always been running.

### The row format

```ini
dotnet_diagnostic.<ID>.severity = <none|suggestion|warning|error>   # <the reason>
```

`none` is off. `suggestion` is visible in the IDE and is not promoted.
`warning` is promoted to an error by `TreatWarningsAsErrors=true`. `error` is
always an error.

The reason is part of the row, not a comment above it. A comment above a row
is invisible to every tool that reads the table, so the reason is lost the
moment the next person reformats the file.

### What a baseline row looks like

Rules that are off are off **by family and by named reason**, not by a blanket
switch. A family that has a real finding in this tree gets its own row with
its own reason. The family block states the reason once, in prose, and the
rows under it carry the rule's own short reason:

```ini
# --- Meziantou 3.0.315 (new rules, MA0213-MA0243) ---
# The 3.0.123 -> 3.0.315 gap adds these 31 rules and changes no severity on the
# 209 that already existed. Each new rule is baselined to the severity the
# analyzer ships it with, so the version move cannot turn the zero-warning
# build red on the day it lands.
dotnet_diagnostic.MA0213.severity = none # Simplify negated boolean expression (ships none by default; not adopted yet)
dotnet_diagnostic.MA0220.severity = suggestion # The configured regular expression is not valid (ships warning; reads BannedSyntaxes.txt, which this repo does not have)
```

Three things are in that row: what the rule says, the severity the package
ships it with, and why it is not adopted. The third is the one that turns the
row into a decision rather than a transcription.

The reason has to satisfy the shape the repository uses everywhere a row
grants a permission. `tests/Harbor.Architecture.Tests/ExemptionReason.cs` is
the one place that asks whether a tolerated row states why it is tolerated,
and it asks three questions: the reason is not blank, it is not the tracking
issue URL again (a URL says where the debt is tracked, not why it is
tolerated, so it belongs in its own column), and it is at least 40 characters
long. `later`, `TODO`, `see above` and `n/a` all pass a blank check and all
say nothing.

**What is machine-checked, and what is not.** The path-scoped rows are
checked: `DiSeverityDemotionRules.PathScopedSection_NamesEveryRuleItSets`
fails the build when the comment that governs a row does not name that row's
id, and `AnalyzerSeverityScopeRules` fails when a path-scoped row addresses a
path that is not in the checkout or is not named in this document. The
tree-wide rows in `[*.{cs,csx}]` are not checked for a reason by any tool
today. They carry one anyway, because the family convention above is what
makes the table readable, and a table that only a machine can grade is a table
nobody maintains.

### How the baseline is lowered

One rule at a time, and in the same commit as the fix:

1. Fix the finding in the code.
2. Raise that rule's severity in the severity table, or delete its
   `severity = none` row if the rule is now good enough to run at the
   severity the package ships it with.
3. Update this document's table for that rule, if this document states one.
4. Delete the "not adopted yet" wording from the row's reason, so the reason
   describes what the row is now.

A baseline row is not a permanent exemption. It is a debt with a reason
attached, and the reason is what tells the next person whether the debt is
still the one that was written down.

This is the rule the two version moves established. The section *Version
baseline, and what a version move may change* records it: a version bump lands
with a baseline, not with a finding.

### Who owns the table

| What | Owner |
|------|-------|
| The severity table itself | `.editorconfig` for the source-tree rules, [`/Directory.Build.globalconfig`](../Directory.Build.globalconfig) for the project-file rules. Both are the single source of truth, and both are the only places in this repository where a diagnostic severity is written down. |
| The decision to adopt a rule | Whoever opens the pull request, and the owner when a family stays off for a product reason rather than a technical one. Proj0043 and Proj0044 stay at `suggestion` because the lock-file question is an owner decision, not a build rule. |
| The enforcement | `tests/Harbor.Architecture.Tests/`: `AnalyzerSeverityScopeRules.cs` (a path-scoped row resolves to a real path, is named in this document, and the set of scoped paths is pinned), `DiSeverityDemotionRules.cs` (a path-scoped row names its own rule), and `ExemptionReason.cs` (the reason shape). |
| The record | This document. Every analyzer family this repository runs has a section here, and every path-scoped demotion is named here. |

---

## Real bugs caught by the DI test suite

The CLI DI tests (`tests/Harbor.App.Cli.Tests/HostBuilderDiTests.cs`) caught
two real ordering bugs in `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` on
first run. Both were undiagnosed before the DI tests existed because the
production CLI happens to not exercise the failing paths until first
interactive use.

### Bug 1 — `IAgentRegistry` resolved before registration

```csharp
// RegisterRegistries (before fix):
var agentRegistry = CreateAgentRegistry(config);          // local var only
var toolRegistry  = CreateToolRegistry(tempSp, mcpRegistry);
//   ↑ inside this method: sp.GetRequiredService<IAgentRegistry>()
//     throws because IAgentRegistry is registered on line ~252,
//     AFTER CreateToolRegistry returns.
```

**Fix:** pass `agentRegistry` as a parameter to `CreateToolRegistry`
instead of resolving it from `tempSp`. The DI registration still happens
later — that's fine because the registry is the same object instance.

### Bug 2 — `IHttpClientFactory` resolved before `RegisterHttpClients`

```csharp
// HostBuilder.Build (before fix):
RegisterCore(builder);
RegisterRegistries(builder, harborDir);   // CreateProviderRegistry needs IHttpClientFactory
RegisterStorage(builder, ...);
RegisterTui(builder);
RegisterHttpClients(builder);              // ← too late
```

**Fix:** reorder so `RegisterHttpClients(builder)` runs **before**
`RegisterRegistries`. Named HTTP clients (`anthropic`, `openai`, `ollama`,
`providers`, `default`) are now registered in time for the eager
`ProviderRegistry` construction.

### Takeaway

These bugs would have shipped without the DI test — the build succeeded
because the failing line is inside a method only invoked at app startup,
and the analyzer (DI015 Unresolvable dependency) doesn't follow
`BuildServiceProvider()` + `GetRequiredService()` call chains through
private helpers. The runtime DI test is the only safety net for this class
of bug, which is why every composition root has a sibling `*.Tests`
project that builds the host end-to-end.

---

## Analyzer warnings in test code

The DI tests themselves intentionally trigger a few analyzer warnings
because the test fixture's design pattern is unusual. These are
suppressed locally with comments — production code must still pass
clean.

| Rule | Where | Why it's OK |
|------|-------|-------------|
| `DI006` Static `IServiceProvider` cache | `HostBuilderDiTests.cs` (file-level `#pragma warning disable DI006`) | The whole point of the fixture is to cache the built host and resolve services from it across many `[Test]` methods. No production Singleton captures the test's ServiceProvider, so there's no captive-dependency risk. |
| `TUnitAssertions0005` Assert.That with constant | `Build_ResolvingRequiredServices_DoesNotThrow` originally had `Assert.That(true).IsTrue()` as a trailing assertion. | Removed — TUnit treats a `[Test]` method that returns without throwing as Passed. |

If you add new test code that triggers DI006 in the same fixture,
don't re-suppress — restructure so the new code doesn't need a static
provider cache. The file-level pragma is scoped to this one fixture
on purpose.

---

## TUnit's own analyzers (`TUnit.Analyzers`, `TUnit.Assertions.Analyzers`)

TUnit 1.75 ships two analyzer assemblies inside its own NuGet packages —
`TUnit.Analyzers` in `TUnit.Core` and `TUnit.Assertions.Analyzers` in
`TUnit.Assertions` — and NuGet hands both to the compiler as analyzers
automatically. They only ever run on test projects: the TUnit reference
comes from `Directory.Build.targets`, which adds it to every project with
`IsTestProject=true`, and nothing under `src/` or `apps/` asks for it.

### The severity table

The rules are on with TUnit's own default severities; the promotion is
`tests/Directory.Build.props`, and it is the only place the policy is
written down.

| Rule | Severity | Enforced? | What it reports |
|------|----------|-----------|-----------------|
| `TUnitAssertions0005` | Warning (TUnit's default) | **error** for every test project | `Assert.That(...)` whose first argument is a compile-time constant |
| `TUnitAssertions0015` | Warning (TUnit's default) | **error** for every test project | `IsEqualTo(true)` / `IsEqualTo(false)` where `IsTrue()` / `IsFalse()` belong |
| `TUnit0055` | Warning | warning — the fix is #1790's `ParallelLimiter` work | `Console.SetOut` / `Console.SetError` inside a test (a real race on the global `Console`) |
| `TUnit0015` | Warning | warning — the CTS/`[Timeout]` boilerplate is its own ticket | a test with a `CancellationToken` parameter and no `[Timeout]` |
| `TUnit0023` | Warning | warning | a disposable that is not released in a cleanup method |

The promotion is `WarningsAsErrors`, not `TreatWarningsAsErrors`: the test
tree keeps `TreatWarningsAsErrors=false` on purpose (RS0030 and CFE0001
are documented false positives there), and the compiler applies a
specific warning id next to the general setting — the specific one wins.

### The two shapes of `TUnitAssertions0005`

A constant first argument is one of two things, and they get opposite
verdicts.

* **A literal** (`Assert.That(true).IsTrue()`) — a **defect**. The
  assertion cannot fail, so the test proves nothing. Fixed by writing the
  assertion the test actually means (`Assert.That(() => action()).ThrowsNothing()`,
  `Assert.That(vm.Status).IsEqualTo("running")`,
  `Assert.That(() => backend.AppendLines(-1)).Throws<ArgumentOutOfRangeException>()`).
* **A production constant compared against a number the test owns**
  (`Assert.That(ContextUsage.WarnThreshold).IsEqualTo(0.50)`) —
  **legitimate**: the constant IS the subject. The constant is inlined at
  the call site, so a change to it recompiles the assertion to a different
  number and the test fails. It is a change detector, not a tautology.

The legitimate ones are waived at the call site, with a reason, the way
the RS0030 exemptions are:

```csharp
#pragma warning disable TUnitAssertions0005 // the threshold constant is the subject: the test pins its numeric value
await Assert.That(ContextUsage.WarnThreshold).IsEqualTo(0.50);
#pragma warning restore TUnitAssertions0005
```

A blanket suppression — a file-wide pragma, a `NoWarn` in a `.csproj`, an
`.editorconfig` entry — is not accepted here: each waiver covers its own
site and says why the constant is the subject.

### The control

`tools/check-tunit-analyzer-gate.py` builds `tests/TUnitAnalyzerCanary` —
a project deliberately kept out of `Harbor.slnx`, because its planted
offender is an error by design — and asserts that the planted offender is
reported as an error and that the planted *waived* offender is not. If the
analyzer stopped shipping, if the promotion stopped applying, or if a
waiver grew past its own site, that check goes red. The gate is proved,
not assumed.
