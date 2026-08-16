# Analyzers

Harbor runs three layers of static analysis + DI validation:

1. **Roslyn analyzers** — pinned in `Directory.Packages.props`, wired in
   `Directory.Build.props` (solution-wide) and `apps/Directory.Build.props`
   (apps-only). Severities configured in `.editorconfig`.
2. **DI registration tests** — per-composition-root test projects under
   `tests/Harbor.App.*.Tests/` that build the host and assert every
   expected service is resolvable.
3. **`dotnet-arch-analyzer`** — optional namespace-level layer validation
   via `dotnetarch.json` (circular deps + layer violations).

---

## 1. Roslyn analyzers

| Package | Scope | Rule IDs |
|---------|-------|----------|
| `Roslynator.Analyzers` | solution-wide | RCS0xxx |
| `SonarAnalyzer.CSharp` | solution-wide | S0xxx |
| `Microsoft.CodeAnalysis.NetAnalyzers` | solution-wide | CA0xxx |
| `AsyncFixer` | solution-wide | AsyncFixer01–05 |
| `ReflectionAnalyzers` | solution-wide | REL0001–0002 |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | solution-wide | RS0030 (config in `BannedApi.txt`) |
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
| DI006 | Static `IServiceProvider` cache | Warning | Warning | Memory leak. |
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

| Test project | Composition root | TFM |
|--------------|------------------|-----|
| `Harbor.App.Cli.Tests` | `Harbor.Cli.Hosting.HostBuilder.Build` | `net10.0` |
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
   `tests/Harbor.App.*.Tests/*DiTests.cs` file.
4. Add `typeof(IFoo)` to the `required` array in the aggregate test
   (`Build_AllDeclaredServices_Resolvable`).

The DI tests will fail at PR time if a registration is accidentally removed.

---

## 3. `dotnet-arch-analyzer`

Optional namespace-level architecture validation. The config lives in
`dotnetarch.json` at the repo root.

### Install

```bash
dotnet tool install -g dotnet-arch
```

### Run

```bash
dotnet-arch analyze --config dotnetarch.json --solution Harbor.slnx
```

### Layers

| Layer | Assemblies | Depends on |
|-------|------------|------------|
| **Domain** | `Harbor.Abstractions` | (none) |
| **Application** | `Harbor.Core`, `Harbor.Application`, `Harbor.Registries`, `Harbor.Plugins.Abstractions`, `Harbor.Scripting.Abstractions` | Domain |
| **Infrastructure** | `Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tools.*`, `Harbor.Plugins.Runtime`, `Harbor.Plugins.Hosting`, `Harbor.Plugins.Compilation`, `Harbor.Plugins.Instantiation`, `Harbor.Plugins.Registration`, `Harbor.Plugins.Storage`, `Harbor.Scripting.*` | Domain, Application |
| **Presentation** | `Harbor.Tui.*`, `Harbor.Desktop.*`, `Harbor.Ui.Framework` | Domain, Application |
| **CompositionRoot** | `Harbor.App.*` | Domain, Application, Infrastructure, Presentation |

### Rules

- `arch/circular-dependency` → error
- `arch/layer-violation` → error

Mechanical enforcement of the layering rules documented in
`docs/ARCHITECTURE_LAYERS.md` and exercised by
`tests/Harbor.Architecture.Tests/`.

---

## CI integration

### `dotnet build`

All analyzers run as part of `dotnet build` (they're PackageReferences with
`PrivateAssets="all"`, so they ship with the project's compilation). The
build fails on any error-severity diagnostic. Currently:

- DI003, DI013, DI015, DI017, DI019 → error
- All other DI rules → warning (visible but non-blocking)
- All Excubo EDI rules → suggestion (visible but non-blocking)

### `dotnet test`

The DI test projects run as part of the regular `dotnet test` step. They
appear as separate test projects in the TUnit results.

### WPF / MAUI on Linux CI

- `Harbor.App.Wpf.Tests` targets `net10.0-windows10.0.19041` — won't restore
  on Linux. Exclude with `dotnet test --filter 'FullyQualifiedName!~Harbor.App.Wpf.Tests'`
  or by not listing the project in CI's test project list.
- `Harbor.App.Maui.Tests` requires the `maui-windows` / `maui-maccatalyst`
  workloads. Same exclusion pattern.

The CLI, Avalonia, and Blazor DI tests run on Linux CI without any special
workloads.

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
