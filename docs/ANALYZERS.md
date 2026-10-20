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
| `Meziantou.Analyzer` | solution-wide | MA0xxx |
| **`DotNetProjectFile.Analyzers`** | **solution-wide** | **Proj0xxx** (severity table in [`/.globalconfig`](../.globalconfig), not here — see §Project-file rules) |
| **`DependencyInjection.Lifetime.Analyzers`** | **solution-wide** | **DI001–DI027** |
| **`Excubo.Analyzers.DependencyInjectionValidation`** | **apps-only** | **EDI01–EDI04, ADP0001** |

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
| **Proj0037** | Exclude runtime when all assets are private | A `PrivateAssets=all` package has no runtime assembly; without `ExcludeAssets=runtime` the IDE offers a type the program cannot load. Fixed at 18 `PackageReference` sites, one `ExcludeAssets` line each, plus one scoped `NoWarn` (see below). |
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
in [`/.globalconfig`](../.globalconfig).

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

The .NET SDK discovers a file named `.globalconfig` by walking up from the
compiled sources (`Microsoft.Managed.Core.targets`), so nothing has to be wired
for it to be picked up. The file sits at the repository root and is the only
place in this repository where a diagnostic severity is written down outside
`.editorconfig`.

#### The one place Proj0037 is scoped instead of fixed

`src/Harbor.CodeGen/Harbor.CodeGen.csproj` references
`Microsoft.CodeAnalysis.CSharp` with `PrivateAssets=all`, and the rule asks for
`ExcludeAssets=runtime` there. It does not hold: the generator is compiled
against Roslyn 5.6.0 while the .NET 10 SDK ships an older Roslyn, and the
local copy of the Roslyn runtime is what lets the generator load against the
version it was compiled against. The finding is scoped at that element with
`NoWarn="Proj0037"` and a comment, rather than silenced globally.

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

1. Pin the version in `Directory.Packages.props`:
   ```xml
   <PackageVersion Include="NewAnalyzer" Version="x.y.z" />
   ```
2. Add the `PackageReference` (with `PrivateAssets="all"`) either to
   `Directory.Build.props` (solution-wide) or the relevant
   `apps/Directory.Build.props` / `tests/Directory.Build.props`.
3. Configure severities in `.editorconfig`:
   ```ini
   dotnet_diagnostic.NEWRULE01.severity = error
   ```
4. Document the rule in this file (table + rationale).
5. Run `dotnet build` and confirm 0 errors (or document why a warning is OK).

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
