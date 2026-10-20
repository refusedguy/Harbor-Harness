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

---

## 1. Roslyn analyzers

| Package | Scope | Rule IDs |
|---------|-------|----------|
| `Roslynator.Analyzers` | solution-wide | RCS0xxx |
| `SonarAnalyzer.CSharp` | solution-wide | S0xxx |
| `Microsoft.CodeAnalysis.NetAnalyzers` | solution-wide | CA0xxx |
| `AsyncFixer` | solution-wide | AsyncFixer01–05 |
| `ReflectionAnalyzers` | solution-wide | REFL001–REFL046 |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | solution-wide | RS0030 (config in [`BannedSymbols.txt`](../BannedSymbols.txt)) |
| **`Microsoft.CodeAnalysis.PublicApiAnalyzers`** | **two frozen assemblies** | **RS0016, RS0017, RS0022, RS0024–RS0027, RS0036, RS0037, RS0041, RS0048, RS0050** |
| `Meziantou.Analyzer` | solution-wide | MA0xxx |
| **`DotNetProjectFile.Analyzers`** | **solution-wide** | **Proj0xxx** (severity table in [`/Directory.Build.globalconfig`](../Directory.Build.globalconfig), not here — see §Project-file rules) |
| **`DependencyInjection.Lifetime.Analyzers`** | **solution-wide** | **DI001–DI027** |
| **`Excubo.Analyzers.DependencyInjectionValidation`** | **apps-only** | **EDI01–EDI04, ADP0001** |
| **`Microsoft.VisualStudio.Threading.Analyzers`** | **src/ + apps/ only** | **VSTHRD001–VSTHRD202 (one enabled)** |

The table above is the set of analyzers this repository runs. It is not the
set of analyzers that were looked at. The candidates that were checked and not
taken are in [§Checked and rejected candidates](#checked-and-rejected-candidates),
so that the next person does not spend a day of NuGet registry reads and
GitHub API calls rediscovering that a package was abandoned in 2019.

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
| `Microsoft.CodeAnalysis.NetAnalyzers` | 10.0.302 | MIT | ADOPT, solution-wide | The base layer every other package is judged against. 40 of its rules carry a Harbor severity of their own (4 `none`, 33 `suggestion`, 3 `error`); the rest keep the severity they ship with. |
| `Roslynator.Analyzers` | 5.0.1 | Apache-2.0 | ADOPT, solution-wide | 284 RCS ids and no severity rows at all: the 4.15.0 to 5.0.1 gap introduced zero new ids and removed twenty obsolete implementations (#1851). |
| `SonarAnalyzer.CSharp` | 10.29.0.143774 | source-available, not SPDX (a `LICENSE` file in the package) | ADOPT, solution-wide | 24 S rules are demoted or switched off, each with a reason. The licence is not LGPL: it is the "SONAR Source-Available License v1.0", whose AI-use clause is open in #1858. |
| `Meziantou.Analyzer` | 3.0.315 | MIT | ADOPT, solution-wide | 207 MA rows. The 3.0.123 to 3.0.315 gap added 31 ids, each baselined at the severity the package ships it with (#1851). |
| `AsyncFixer` | 2.1.0 | Apache-2.0 | ADOPT, solution-wide | Five rules, AsyncFixer01 to AsyncFixer05, all at `suggestion`. |
| `ReflectionAnalyzers` | 0.3.1 | MIT | ADOPT, solution-wide | 46 REFL ids and no severity rows: nine rules fire 216 times and every one of them is in `tests/`, where `TreatWarningsAsErrors` is off (#1905). The dynamic-code ban is not this package, it is RS0030 (#1844). |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 5.6.0 | MIT | ADOPT, solution-wide | RS0030 over `BannedSymbols.txt` and `BannedSymbols.DynamicCode.txt`. Since #1844 the compiler enforces the dynamic-code ban that a hand-written regex used to (#626). |
| `DependencyInjection.Lifetime.Analyzers` | 2.18.24 | MIT | ADOPT, solution-wide | 29 DI rows, one Harbor severity per rule. DI003, DI015, DI017 and DI019 are errors. |
| `CSharpFunctionalExtensions.Analyzers` | 1.3.0 | none declared in the nuspec | ADOPT, solution-wide | CFE0001 ("check IsSuccess or IsFailure") is the mechanical guard for the §ROP-001 crash class. CFE0002 is Info and stays off. |
| `DotNetProjectFile.Analyzers` | 1.19.1 | MIT | ADOPT, solution-wide | 193 Proj rows in [`/Directory.Build.globalconfig`](../Directory.Build.globalconfig). Three rules are errors, one is `none`, two stay `suggestion` because the lock-file question is the owner's (#1851, #1857). |
| `Excubo.Analyzers.DependencyInjectionValidation` | 1.0.33 | a `LICENSE` file, no SPDX expression | ADOPT, apps-only | EDI01 to EDI04 plus ADP0001, all demoted to `suggestion` because the analyzer's flow analysis only follows the immediate method body. The real coverage is the DI registration tests (§2). |
| `Microsoft.VisualStudio.Threading.Analyzers` | 18.12.1 | MIT | ADOPT with scope, `src/` and `apps/` only (#1852) | 29 rules, one of them on: VSTHRD110 at `suggestion`. The other 28 are `none` with a reason each, and four of them ship as `error` by default, so landing the package without the demotions would have reddened the tree on the day it arrived. |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | 5.6.0 | MIT | ADOPT, staged (PR #1944, issue #1845) | Replaces 1212 lines of python public-API gate with the compiler, on the two frozen projects only. The pull request is open, so this row records the decision, not the merge. |
| `TheExceptionator.Analyzers` | 1.3.33 | Apache-2.0 | ADOPT with scope (issue #1848, open) | EX003 (a lost inner exception), EX021 (an exception type with no `(message, inner)` constructor) and EX011 (an empty catch) are the findings worth taking; the rest overlaps CA and Sonar. |
| `NetArchTest.Rules` | 1.3.2 | MIT | IN the tree, the replacement is open | The layer gate runs on it, and its last release was 2021-05-23. The replacement is tracked in #1841 (ArchUnitNET) and #1864 (aweXpect.Reflection). |
| `aweXpect.Reflection` | 3.1.0 | MIT | SPIKE, the live pilot (#1864, #1879) | The successor `Testably.Architecture.Rules` names, with no transitive Newtonsoft and no TUnit pin. It covers exactly one of our tasks: the shape of an interface and the dead surface. |
| `ErrorProne.NET.CoreAnalyzers` | 0.9.0-beta.4 | MIT (a `licenseUrl`, no SPDX expression) | REJECT (#1849) | Measured on this tree: 2 product findings, both duplicating EX003. The unique rules give 0 (EPC32, EPC19, EPC16, EPC17, EPC26, EPC30, EPC23, EPC11), and EPC27 gives 4 false positives on the `async void` handlers `docs/ANTIPATTERNS.md` §13 blesses. |
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
| `jscpd` | 5.4.1 | MIT | REJECT (#1847) | `tools/check-test-dup.py` is the gate and it landed. jscpd compares token windows, which would change the detection semantics, and the baseline with them. |
| `Philips.CodeAnalysis.DuplicateCodeAnalyzer` | 2.0.0 | MIT | REJECT as a gate (#1847) | It compares inside one compilation, so it cannot see the cross-project duplication that is half of the problem; it has no baseline concept, and its cost is paid on every build of every project. |
| `PMD CPD` | 7.28.0 | LGPL-2.1 | REJECT (#1847) | It needs a JRE in CI, where this repository has neither node nor java, and it is weaker than jscpd on the "fail on new clones only" part. |
| `Microsoft.CodeAnalysis.DuplicateCodeAnalyzer` | no such package | none | REJECT | The NuGet flat-container index for that id answers `BlobNotFound`. There is nothing to evaluate. |
| `ReferenceTrimmer` | 3.5.13 | MIT | ADOPT as a one-off report, not as a gate (#1855) | The manual sweep of unused references already produced two wrong answers (#910, #957) and left 15 candidates unclassified. A permanent gate would be red on packages that are genuinely needed transitively, which is the "train people to ignore the rule" outcome the repository already forbids for RS0030 in tests. |
| `DocumentFormat.OpenXml` | 3.5.1 | MIT | NOT A CANDIDATE | The OpenXML SDK, not an analyzer: it ships no diagnostic ids, so there is nothing to enable and nothing to baseline, and nothing in this repository reads or writes OOXML. |

#### What changed since the 2026-10-20 audit

The audit that produced the first version of this table recorded a snapshot.
Seven of its rows have moved since, and the table above carries the current
state:

| Package | The audit said | It is now | Why |
|---------|----------------|----------|-----|
| `DotNetProjectFile.Analyzers` | REJECT for now, it covers `.csproj` hygiene rather than our invariants | ADOPT, three rules as errors | The package is in the tree at 1.19.1 with Proj0037, Proj0040 and Proj0041 as errors (#1851, #1857). |
| `Microsoft.VisualStudio.Threading.Analyzers` | not rejected, resolved separately in #1852 | ADOPT with scope | #1852 merged. The package is in the tree with one rule on, and the gate that actually decides "is this an unobserved Task" is now `HARCH003`, a house analyzer, not the package. |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | a candidate with an open decision | ADOPT, staged | #1845 is decided and PR #1944 is open. The python public-API gate it replaces is still in the tree until that pull request merges. |
| `IDisposableAnalyzers` | REJECT, it duplicates what CA1001 and CA1063 already say | REJECT the package, ADOPT the findings | IDISP014 is not covered by CA1001 or CA1063. The package is still dead; the two `HttpClient` sites it named were fixed by hand, a house rule now guards them (`HttpClientReuseRules.cs`), and both landed (#1854). |
| `ErrorProne.NET.CoreAnalyzers` | REJECT, measured | REJECT, measured | The verdict stands, but the version the audit recorded (`0.1.5898.41180`) is not a version this package ever published. The measured version is `0.9.0-beta.4`, and it is the one the verdict applies to (#1849). |
| `TheExceptionator.Analyzers` | not rejected, resolved separately in #1848 | ADOPT with scope, still open | #1848 has not merged, so the package is not in the tree. The scope is the three rules the table names. |
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

The two families in the table above are the ones a version move has been
measured against.
Both are read out of the shipped packages, not out of a changelog summary.

| Family | Pinned | Decision |
|--------|--------|----------|
| `Roslynator.Analyzers` | 5.0.1 | **No severity rows.** The 4.15.0 → 5.0.1 gap introduces **zero** new diagnostic ids: both packages ship exactly 284 `RCS####` ids, read out of the analyzer assemblies. Roslynator 5.0.0 removes the *implementations* of twenty obsolete rules (RCS0014, RCS0022, RCS0038, RCS0043, RCS0047, RCS1008–RCS1010, RCS1012, RCS1035, RCS1036, RCS1038/RCS1040/RCS1041/RCS1066/RCS1072/RCS1091/RCS1106, RCS1176/RCS1177, RCS1237) and points at successor rules that already ship in 4.15.0, so the move can only shrink the finding surface. 5.0.0 also adds the `roslyn5.0` analyzer flavour, which is the one the .NET 10 SDK (Roslyn 5.x) loads; 4.15.0 only shipped roslyn3.8/roslyn4.7. |
| `Meziantou.Analyzer` | 3.0.315 | **31 new rows, one per new rule.** The 3.0.123 → 3.0.315 gap adds `MA0213`–`MA0243` and changes **no** severity on the 209 rules that already existed — both facts read out of the `configuration/default.editorconfig` the package ships, which lists every rule with its default severity. Each new rule is baselined to the severity the analyzer ships it with, so the move cannot turn the zero-warning build red on the day it lands. The three that ship as `warning` (MA0220, MA0240, MA0241) read the `BannedSyntaxes.txt` file, which this repository does not have, so they have no input; they are advisory here so that adding that file later cannot break the build either. |

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

### DI lifetime rules (DependencyInjection.Lifetime.Analyzers 2.18.24)

26 diagnostics covering captive deps, lifetime mismatches, circular DI,
scope leaks, and `BuildServiceProvider` misuse.

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
| **DI013** | **Implementation type mismatch** | Error | **Error** | Default; restated for visibility. |
| DI014 | Root provider not disposed | Warning | Warning | Leak. |
| **DI015** | **Unresolvable dependency** | Warning | **Error** | Build-time signal for missing deps. |
| DI016 | `BuildServiceProvider` misuse | Warning | Suggestion | Existing pattern in HostBuilder.cs — refactor tracked separately. |
| **DI017** | **Circular dependency** | Warning | **Error** | Build-time signal for DI cycles. |
| DI018 | Non-instantiable implementation type | Warning | Warning | Real bug. |
| **DI019** | **Scoped service resolved from root** | Warning | **Error** | Captive dependency at resolve site. |
| DI020 | Middleware captures scoped service | Warning | Warning | Real bug. |
| DI021 | Non-thread-safe service shared across handlers | Warning | Warning | Concurrency bug. |
| DI022 | Service instance reused across handlers | Info | Suggestion | Design smell. |
| DI024 | Hosted service scope outside loop | Warning | Warning | Real bug. |
| DI025 | Event subscription without unsubscribe | Warning | Warning | Memory leak. |
| DI026 | Event subscription on scoped publisher | Info | Suggestion | Design smell. |
| DI027 | Rx subscription without dispose | Warning | Warning | Memory leak. |

Full docs: <https://georgepwall1991.github.io/DependencyInjection.Lifetime.Analyzers/rules/>

> **The `Harbor` column is the tree-wide value.** One path overrides it — see
> §Path-scoped severity overrides below. So "DI006 → Warning" is the correct
> answer for `src/` and for `apps/Harbor.App.Cli/`, and **not** for
> `apps/Harbor.App.Avalonia/`, where a static `IServiceProvider` is a
> suggestion. If you are reading the table to decide whether a diagnostic is
> visible in your build, that exception is the thing to check first.

### Path-scoped severity overrides

Every severity above is set by `[*.{cs,csx}]` in `.editorconfig` and applies to
the whole tree. There is exactly **one** section that scopes a diagnostic's
severity to a path instead, and it is this one:

```ini
[apps/Harbor.App.Avalonia/**.cs]
dotnet_diagnostic.DI006.severity = suggestion   # static IServiceProvider cache
dotnet_diagnostic.DI008.severity = suggestion   # disposable transient service
dotnet_diagnostic.DI014.severity = suggestion   # root provider not disposed
```

**Why:** an Avalonia composition root has to hand its container to the XAML
object graph, which cannot take constructor parameters, so the static root
provider is the idiom there. The CLI root resolves through
`Harbor.App.Cli/Hosting/HostBuilder.Build` instead and is deliberately **not**
in this list. The asymmetry is observed rather than assumed: #837 planted a
static container in the CLI root and the build failed with `error DI006` before
a single test ran, in the same session that found the Avalonia root's
equivalent compiling clean.

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
`DiSeverityOverrides_AreScopedToExactlyTheDeclaredRoot` pins this one path as
the entire surface.

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
| **Proj0040** | Run NuGet security audits on transitive dependencies too | The SDK default is `direct`. Harbor pins transitive versions on purpose (`CentralPackageTransitivePinningEnabled`), so a transitive advisory is Harbor's subject. Fixed by pinning `NuGetAuditMode=all` in `Directory.Build.props`. |
| **Proj0041** | NuGet audit level must be at least moderate | The SDK default is `low`. Fixed by pinning `NuGetAuditLevel=moderate` in `Directory.Build.props`. |

**One rule is demoted because it contradicts this repository:**

| ID | Title | Severity | Why |
|----|-------|----------|-----|
| Proj0039 | Treat all warnings as errors is a bad practice | none | The zero-warning gate (`TreatWarningsAsErrors=true` in `Directory.Build.props`) is load-bearing here. Rejected with a reason, not silently. |

**Two rules stay visible because the decision is the owner's, not the build's:**

| ID | Title | Severity | Why |
|----|-------|----------|-----|
| Proj0043 | Use lock files | suggestion | 157 `PackageReference`s times a lock file is a large diff that has to be reconciled with dependabot first. Not adopted silently. |
| Proj0044 | Enable `RestoreLockedMode` when `ContinuousIntegrationBuild` is on | suggestion | Follows Proj0043; both wait on the same decision. |

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
against Roslyn 5.6.0 while the .NET 10 SDK ships an older Roslyn, and the
local copy of the Roslyn runtime is what lets a generator load against the
version it was compiled against. The finding is scoped at those two elements
with `NoWarn="Proj0037"` and a comment, rather than silenced globally. The
canonical fix (exclude the runtime asset) lands when the repository's Roslyn
pin and the SDK's Roslyn are aligned.

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
`renderer-perf-gate.yml` green. 11 of the 36 test projects also still reference
`Microsoft.NET.Test.Sdk`, which TUnit documents as stopping discovery. See
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
| `.editorconfig`, a path section such as `[apps/Harbor.App.Avalonia/**.cs]` | the same, but only for one directory | One such section exists today. `AnalyzerSeverityScopeRules` pins the set of paths and fails if a path-scoped section addresses a directory that is not in the checkout. |
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
