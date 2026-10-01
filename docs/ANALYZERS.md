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
| `ReflectionAnalyzers` | solution-wide | REL0001–0002 |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | solution-wide | RS0030 (config in [`BannedSymbols.txt`](../BannedSymbols.txt)) |
| `Meziantou.Analyzer` | solution-wide | MA0xxx |
| **`DependencyInjection.Lifetime.Analyzers`** | **solution-wide** | **DI001–DI027** |
| **`Excubo.Analyzers.DependencyInjectionValidation`** | **apps-only** | **EDI01–EDI04, ADP0001** |

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
dotnet_diagnostic.DI003.severity = suggestion   # captive dependency
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

**Known breadth, deliberately not fixed here:** the block is four rules deep and
its reason covers fewer than four. DI003 (captive dependency) is a
lifetime-graph rule about a singleton retaining a *scoped* service, which is
not what "process-lifetime singletons" means, and the demotion covers the whole
`apps/Harbor.App.Avalonia` tree rather than the app root alone. Narrowing it
needs one strict build to learn whether the desktop app has a real captive
dependency; #838 ran no build, so the breadth is recorded rather than guessed at.

Three sibling blocks used to sit here for `apps/Harbor.App.{Wpf,Maui,Blazor}`.
They were removed in #838: none of those directories exists — those roots live
under `contrib/`, which is not in `Harbor.slnx` and is not built by CI — so the
blocks were inert configuration that read like live policy.
`AnalyzerSeverityScopeRules.PathScopedSections_ResolveToRealPaths` in
`tests/Harbor.Architecture.Tests/` fails if such a block reappears, and
`DiSeverityOverrides_AreScopedToExactlyTheDeclaredRoot` pins this one path as
the entire surface.

### Banned APIs (`BannedSymbols.txt`)

[`BannedSymbols.txt`](../BannedSymbols.txt) is the RS0030 rule set — the
mechanical expression of the "What NOT to do" list in CLAUDE.md. It is wired in
`Directory.Build.props` via `<AdditionalFiles Include="BannedSymbols.txt" />`.

> **The filename is load-bearing.** The analyzer only reads files literally named
> `BannedSymbols.txt`. The rules sat dead under the old name `BannedApi.txt`
> until ROP-D Z2, which both renamed the file and added the `AdditionalFiles`
> entry — two independent reasons for it to be invisible. If you rename it, the
> whole rule set silently stops firing.

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

No new analyzer axis is needed for that, and none is added (feature freeze
issue #555). The one container shape a reflection sweep *can* own is a stored
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
| `suggestion`, one path only | DI003, DI006, DI008, DI014 under `apps/Harbor.App.Avalonia/` | as above, in the Avalonia desktop root |

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
