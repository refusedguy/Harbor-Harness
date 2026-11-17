# Central Package Management (CPM)

> **Task ID:** G (researcher-r4)
> **Purpose:** Document what Central Package Management is, why Harbor uses it, how it was migrated from per-csproj `PackageReference` declarations to the central `Directory.Packages.props` file, and how to add/remove/upgrade packages under CPM.
> **Output scope:** single document, lives at `docs/CENTRAL_PACKAGE_MANAGEMENT.md`.
> **Status:** ✅ **Already implemented** — `Directory.Packages.props` exists at the repo root, `ManagePackageVersionsCentrally=true` is set in `Directory.Build.props`, all .csproj files declare `<PackageReference Include="..." />` without a `Version` attribute. This doc is the reference guide.
> **Companion docs:**
> - `docs/DESKTOP_APP_PLAN.md` — master plan for 4 desktop apps (companion, written in same round)
> - `docs/FEATURE_RESEARCH.md` §11 — grok-build analysis (companion, written in same round)
> - `Directory.Packages.props` — the actual central package version file (repo root)
> - `Directory.Build.props` — common MSBuild properties (repo root)
> - `Directory.Build.targets` — common MSBuild targets (repo root)

---

## Table of Contents

1. [What is CPM?](#1-what-is-cpm)
2. [Why Harbor needs CPM](#2-why-harbor-needs-cpm)
3. [The 3 MSBuild files at the repo root](#3-the-3-msbuild-files-at-the-repo-root)
4. [Harbor's `Directory.Packages.props` — full annotated listing](#4-harbors-directorypackagesprops--full-annotated-listing)
5. [How to add a new package](#5-how-to-add-a-new-package)
6. [How to upgrade a package](#6-how-to-upgrade-a-package)
7. [How to remove a package](#7-how-to-remove-a-package)
8. [Migration plan — what was done to move Harbor to CPM](#8-migration-plan--what-was-done-to-move-harbor-to-cpm)
9. [CPM + transitive pinning — the full safety net](#9-cpm--transitive-pinning--the-full-safety-net)
10. [CPM vs Paket vs NuGet.exe — why CPM won](#10-cpm-vs-paket-vs-nugetexe--why-cpm-won)
11. [Common pitfalls](#11-common-pitfalls)
12. [Verifying CPM is working](#12-verifying-cpm-is-working)
13. [References](#13-references)
14. [The restore: the audit pin, the lock files, and the CI cache](#14-the-restore-the-audit-pin-the-lock-files-and-the-ci-cache)

---

## 1. What is CPM?

**Central Package Management** (CPM) is a NuGet feature introduced in NuGet 6.0 / .NET 6 SDK that lets you manage all NuGet package versions in a **single file** at the repository root (`Directory.Packages.props`) instead of scattering `Version="..."` attributes across every `.csproj` file.

### 1.1 Before CPM — the old way

Every `.csproj` declared its own package versions:

```xml
<!-- src/Harbor.Application/Harbor.Application.csproj -->
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0"/>
  <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0"/>
  <PackageReference Include="ZLinq" Version="1.5.6"/>
</ItemGroup>
```

```xml
<!-- apps/Harbor.App.Cli/Harbor.App.Cli.csproj -->
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0"/>
  <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0"/>
  <PackageReference Include="ZLinq" Version="1.5.6"/>  <!-- oops, this was upgraded to 1.6.0 in PR #1234 but not here -->
</ItemGroup>
```

**Problem:** If `Harbor.Application` upgraded `ZLinq` to 1.6.0 but `Harbor.Cli` still had `1.5.6`, the build would succeed but at runtime the two assemblies would load different versions of `ZLinq.dll`, leading to `MissingMethodException` or `FileLoadException`. CPM eliminates this entire class of bug.

### 1.2 After CPM — the new way

A single `Directory.Packages.props` at the repo root declares every package version once:

```xml
<!-- Directory.Packages.props (repo root) -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="10.0.13" />
    <PackageVersion Include="ZLinq" Version="1.5.6" />
    <!-- ...all other packages... -->
  </ItemGroup>
</Project>
```

And every `.csproj` declares the package **without** a `Version`:

```xml
<!-- src/Harbor.Application/Harbor.Application.csproj -->
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Logging"/>
  <PackageReference Include="ZLinq"/>
</ItemGroup>
```

NuGet resolves the version from `Directory.Packages.props`. If a package is referenced but not declared in the central file, the build fails with `NU1007` — making it impossible to silently drift.

### 1.3 The 3 key benefits

1. **Single source of truth** — every package version lives in one file. Upgrading `ZLinq` from 1.5.6 to 1.6.0 is a 1-line change in `Directory.Packages.props` instead of N changes across N `.csproj` files.
2. **No version drift** — it is impossible for two projects to consume different versions of the same package. The build fails fast if a `PackageReference` doesn't have a matching `PackageVersion`.
3. **Transitive pinning** — with `CentralPackageTransitivePinningEnabled=true`, even transitive dependencies (packages pulled in by other packages) are pinned to the versions declared in the central file. This prevents a vulnerable transitive from sneaking in via a deep dependency.

---

## 2. Why Harbor needs CPM

Harbor has **~80 `.csproj` files** across `src/`, `apps/`, and `tests/` (plus the contrib tree). Before CPM, package versions were declared per-csproj and we had documented drift:

| Package | Versions seen across the repo (before CPM) |
|---------|--------------------------------------------|
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.0` (most projects), `10.0.8` (Harbor.Tui.SpectreTui, Harbor.Tui.Termina, Harbor.Tui.RazorConsole, Harbor.Tui.Tests), `10.0.10` (none — was about to drift) |
| `Microsoft.Extensions.Configuration.Json` | `10.0.0` (most), `10.10.0` (apps/Harbor.App.Wpf) — already drifted! |
| `Markdig` | `0.38.0` (everywhere) — consistent, but pinned per-csproj |

The `10.0.0` vs `10.0.8` drift on `Microsoft.Extensions.Logging.Abstractions` was the most dangerous: transitive dependencies (e.g. `Terminal.Gui` → `Configuration.Binder 10.0.7`, `Termina` → `Hosting.Abstractions 10.0.8`) required >= 10.0.7 or 10.0.8 minimums, and the build would have started failing on a clean restore if anyone bumped `Terminal.Gui` or `Termina` to a newer version.

After CPM, all `Microsoft.Extensions.*` packages are pinned to **`10.0.13`** (the current servicing release of the 10.0 band), which satisfies every transitive minimum and keeps them all locked to the same version via `CentralPackageTransitivePinningEnabled`.

### 2.1 Quantified benefit

| Metric | Before CPM | After CPM |
|--------|-----------|-----------|
| Total `PackageReference` declarations | ~95 across 54 .csproj files | ~95 (unchanged — still need to declare what each project consumes) |
| Total `Version="..."` attributes | ~95 (one per PackageReference) | **0** (all in `Directory.Packages.props`) |
| Unique packages with version declarations | 47 | 41 (CPM revealed duplicates and consolidated) |
| Versions of `Microsoft.Extensions.Logging.Abstractions` in the repo | 2 (`10.0.0`, `10.0.8`) | **1** (`10.0.13`) |
| Effort to upgrade `ZLinq` from 1.5.6 → 1.6.0 | Touch every `.csproj` that references it (~5 files) | **1-line change** in `Directory.Packages.props` |
| Risk of version drift | High (already happened twice) | **Zero** (build fails if a `PackageReference` has no matching `PackageVersion`) |

---

## 3. The 3 MSBuild files at the repo root

Harbor has three MSBuild files at the repo root that work together. They are easy to confuse. Here's what each does:

### 3.1 `Directory.Build.props`

**Role:** Common MSBuild **properties** applied to every project, evaluated **BEFORE** the project file's own `<PropertyGroup>`s.

**Used for:**
- `<TargetFramework>net10.0</TargetFramework>` — every project targets net10.0
- `<LangVersion>latest</LangVersion>` — latest C# language features
- `<Nullable>enable</Nullable>` — nullable reference types
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` — strict warnings
- `<GenerateDocumentationFile>true</GenerateDocumentationFile>` — XML docs
- Versioning: `<Version>0.4.0-alpha</Version>` etc.
- Build features: `<Deterministic>true</Deterministic>`
- **NOT** `<PublishSingleFile>` any more (#2109): it used to be `true` here, repo-wide, and that one line is what made the SDK pull the ILLink pack into every project of the tree — 212.2 analyzer-seconds, 32 % of the build's analyzer time. It now lives in `apps/Harbor.App.Cli/Harbor.App.Cli.csproj`, the app that actually ships single-file. See [docs/ANALYZERS.md §The ILLink analyzer](./ANALYZERS.md#the-illink-analyzer).
- **CPM switch:** `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` ← this is what enables CPM
- **Analyzer `PackageReference`s:** analyzers (Roslynator, SonarAnalyzer, NetAnalyzers, AsyncFixer, ReflectionAnalyzers, BannedApiAnalyzers, Meziantou) are declared here because they apply to every project. Their versions come from `Directory.Packages.props` (CPM).

**Harbor's `Directory.Build.props`** (122 lines) — see the file at the repo root.

### 3.2 `Directory.Build.targets`

**Role:** Common MSBuild **targets** applied to every project, evaluated **AFTER** the project file's own targets.

**Used for:**
- Test-project detection: `<IsPackable>false</IsPackable>` for `*.Tests` projects
- TUnit package reference for test projects
- NativeAOT publish defaults (per-project opt-in via `<PublishAot>true</PublishAot>`)
- Embed provider JSON configs (when `<EmbedProviders>true</EmbedAot>`)
- Source generator output enablement

**Why `.targets` and not `.props`?** Because test-project detection depends on `$(MSBuildProjectName)` which is only available after the project file is parsed. `.props` runs before the project file; `.targets` runs after.

**Harbor's `Directory.Build.targets`** (48 lines) — see the file at the repo root.

### 3.3 `Directory.Packages.props`

**Role:** Central NuGet package version declarations. Applied to every project during NuGet restore.

**Used for:**
- `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` — enable CPM (also declared here as a fallback in case `Directory.Build.props` is overridden)
- `<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>` — pin transitive dependencies too
- `<PackageVersion Include="..." Version="..." />` entries — one per unique package

**Key difference from `Directory.Build.props`:** `Directory.Packages.props` is **only** evaluated during NuGet restore. It does not affect compilation or build properties. It only affects which NuGet package versions get restored.

Harbor's `Directory.Packages.props` now carries **80 `<PackageVersion>` entries**
— the original migration shipped 41; the desktop/UI sprint added the rest.

**One project is outside this file.** The NUKE build project (`build/_build.csproj`)
turns CPM off for itself and pins its own dependency chain inline, so its
versions live in that file, not here. See [§4.4](#44-the-one-project-outside-cpm-build_buildcsproj).

### 3.4 Evaluation order

When you run `dotnet build src/Harbor.Application/Harbor.Application.csproj`, MSBuild evaluates files in this order:

```
1. Directory.Build.props                 (common properties, BEFORE project)
2. src/Harbor.Application/Harbor.Application.csproj    (project-specific properties + items)
3. Directory.Build.targets               (common targets, AFTER project)
4. During restore:
   a. Directory.Packages.props           (CPM versions)
   b. Per-project PackageReference items (no Version — resolved from CPM)
   c. NuGet generates project.assets.json
```

### 3.5 Quick reference — which file to edit when

| You want to... | Edit this file |
|----------------|---------------|
| Add a new NuGet package to one project | The project's `.csproj` (add `<PackageReference Include="X"/>` — **no Version**) **AND** `Directory.Packages.props` (add `<PackageVersion Include="X" Version="..."/>`) |
| Upgrade a package version | `Directory.Packages.props` (change the `Version` attribute) — that's it |
| Upgrade a package the **NUKE build project** consumes | `build/_build.csproj` (change the `Version` attribute) — CPM is off for that project, so this file has no say; see [§4.4](#44-the-one-project-outside-cpm-build_buildcsproj) |
| Remove a package from one project | The project's `.csproj` (delete the `<PackageReference>`) — leave `Directory.Packages.props` alone (it's harmless to keep an unused `PackageVersion`) |
| Change a build property (target framework, nullable, etc.) | `Directory.Build.props` |
| Add a test-only setting | `Directory.Build.targets` (since it depends on `$(IsTestProject)`) |
| Add an analyzer | `Directory.Build.props` (the `<PackageReference>` for the analyzer) **AND** `Directory.Packages.props` (the `<PackageVersion>`) |

---

## 4. Harbor's `Directory.Packages.props` — full annotated listing

The full file lives at `Directory.Packages.props` (repo root). Below is an annotated walkthrough of its structure:

### 4.1 The header comment

The file opens with a 28-line comment explaining:
- What CPM is
- That every .csproj declares `<PackageReference Include="..." />` (no Version)
- That CPM is enabled by `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` in `Directory.Build.props`
- The grouping convention (Microsoft.Extensions.* → Microsoft.CodeAnalysis.* → ... → source generators)

### 4.2 The PropertyGroup

```xml
<PropertyGroup>
  <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
</PropertyGroup>
```

- `ManagePackageVersionsCentrally=true` — the master switch. When `true`, NuGet requires every `PackageReference` to resolve its version from a `PackageVersion` entry in this file (or a parent `Directory.Packages.props`).
- `CentralPackageTransitivePinningEnabled=true` — additionally pins transitive dependencies. If package A depends on package B v1.0, and you declare `B v2.0` in this file, NuGet will use `B v2.0` instead of `B v1.0`. Without this, NuGet would let the transitive `B v1.0` win.

### 4.3 The 80 `<PackageVersion>` entries

Grouped top-to-bottom, in the order the file declares them (the counts are the
file's own, recounted; the rows sum to the total at the bottom):

| Group | Count | Entries |
|-------|-------|---------|
| Microsoft.Extensions.* | 13 | `Logging.Abstractions`, `Logging`, `Logging.Console`, `DependencyInjection.Abstractions`, `DependencyInjection`, `Configuration.Abstractions`, `Configuration`, `Configuration.Json`, `Hosting.Abstractions`, `Hosting`, `Options`, `Http`, `FileSystemGlobbing` |
| Microsoft.CodeAnalysis.* | 4 | `Microsoft.CodeAnalysis.CSharp` (Roslyn), `Microsoft.CodeAnalysis.NetAnalyzers`, `Microsoft.CodeAnalysis.BannedApiAnalyzers`, `Microsoft.CodeAnalysis.PublicApiAnalyzers` |
| Microsoft.Data.* | 1 | `Microsoft.Data.Sqlite` |
| TUnit + NetArchTest + Mono.Cecil | 4 | `TUnit`, `TUnit.Assertions`, `NetArchTest.Rules`, `Mono.Cecil` |
| BenchmarkDotNet | 1 | `BenchmarkDotNet` |
| CommunityToolkit.* | 1 | `CommunityToolkit.Mvvm` |
| OpenTelemetry | 2 | `OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` |
| Spectre.* | 3 | `Spectre.Console`, `Spectre.Tui`, `Spectre.Tui.App` |
| Vendored submodule transitives | 1 | `SixLabors.ImageSharp` (lifted above the version the vendored `SharpConsoleUI` declares; see the comment in the file) |
| Alternative UI stacks | 14 | `Terminal.Gui`, `Termina`, `RazorConsole.Core`, `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Headless`, `Avalonia.Skia`, `Avalonia.Diagnostics`, `Avalonia.AvaloniaEdit`, `Markdig`, `AvalonEdit`, `Dirkster.AvalonDock` |
| .NET MAUI (workload-supplied) | 6 | `Microsoft.Maui.Controls`, `Microsoft.Maui.Controls.Xaml`, `Microsoft.Maui.Essentials`, `Microsoft.Maui.Core`, `Microsoft.Maui.Resizetizer`, `Microsoft.Maui.Controls.Build.Tasks` |
| Scripting engines | 1 | `Jint` |
| Roslyn analyzers | 8 | `Roslynator.Analyzers`, `SonarAnalyzer.CSharp`, `AsyncFixer`, `ReflectionAnalyzers`, `Meziantou.Analyzer`, `DotNetProjectFile.Analyzers`, `Microsoft.VisualStudio.Threading.Analyzers`, `CSharpFunctionalExtensions.Analyzers` |
| Performance / collections / serialization | 6 | `ZLinq`, `ZLinq.DropInGenerator`, `NonBlocking`, `MemoryPack`, `MemoryPack.Core`, `CSharpFunctionalExtensions` |
| MessagePack | 1 | `MessagePack` |
| SQLite native driver | 1 | `SQLitePCLRaw.lib.e_sqlite3` |
| CLI parsing (sourcegen, AOT-first) | 1 | `ConsoleAppFramework` |
| Source generators | 2 | `Termina.Generators`, `AutoCtor` |
| DI analyzers | 3 | `DependencyInjection.Lifetime.Analyzers`, `Excubo.Analyzers.DependencyInjectionValidation`, `Excubo.Analyzers.Annotations` |
| Serilog | 5 | `Serilog`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.File`, `Serilog.Sinks.Console`, `Serilog.Sinks.Async` |
| Diff | 1 | `DiffPlex` |
| Reference hygiene | 1 | `ReferenceTrimmer` (build-time only, #1853) |
| **Total (at migration time; now 80)** | **80** | |

### 4.4 The one project outside CPM: `build/_build.csproj`

`build/_build.csproj` is the NUKE build entry point. It is the only project in
the repo that is **not** governed by this file, and that is deliberate.

**Why.** Two files switch CPM off for it — `build/Directory.Build.props` and
`build/_build.csproj` both set:

```xml
<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
<CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
```

The copy in `build/_build.csproj` is the one that decides, and the reason is
the import order: the SDK imports `Directory.Build.props` first and
`Directory.Packages.props` second (`Microsoft.Common.props` imports
`NuGet.props`, which imports the central file). So the root file's
`ManagePackageVersionsCentrally=true` lands AFTER the local
`Directory.Build.props` set it to `false`, and only the project file's own
PropertyGroup — evaluated after both imports — survives.

The build project is a tool, not a shipped assembly. It wants a permissive
environment (analyzers off, `TreatWarningsAsErrors` off) so NUKE's generated
code compiles cleanly, and it pins its own dependency chain inline instead of
inheriting the solution-wide strictness.

**The rule that follows, and it is the whole point.** A version lives in
exactly one place. Because CPM is off for this project, the versions it
consumes are decided in `build/_build.csproj` and NOWHERE ELSE:

| Package | Version | Decided in |
|---|---|---|
| `Nuke.Common` | 10.1.0 | `build/_build.csproj` |
| `System.Security.Cryptography.Xml` | 10.0.13 | `build/_build.csproj` |
| `Microsoft.Build` | 18.0.2 | `build/_build.csproj` |
| `Microsoft.Build.Tasks.Core` | 18.0.2 | `build/_build.csproj` |
| `Microsoft.Build.Utilities.Core` | 18.0.2 | `build/_build.csproj` |
| `NuGet.Packaging` | 6.12.5 | `build/_build.csproj` |

There is deliberately **no** `Nuke.Common` entry in `Directory.Packages.props`.
It used to be there, and it was dead: with CPM off for the only project that
references NUKE, the entry never took part in any restore, while the comment
next to it claimed a version that was not the one the build used — two
contradictory answers to the same question in one file. #1896 removed it.

**Why the pins exist at all.** `CentralPackageTransitivePinningEnabled` is off
for this project too, so nothing lifts NUKE's transitive tree to a patched
version. Each pin holds a specific line:

- `System.Security.Cryptography.Xml 10.0.13` — NUKE's tree pulls 9.0.0, which
  is inside 11 advisory ranges. Removing the pin re-opens all 11.
- `Microsoft.Build* 18.0.2` — the floor `Nuke.ProjectModel 10.1.0` requires for
  `net10.0`. A pin below it is NU1605 ("Detected package downgrade"), an error
  rather than a warning.
- `NuGet.Packaging 6.12.5` — above the 6.12.1 NUKE asks for, and it closes
  GHSA-g4vj-cjjj-v7hg.

### 4.5 Note on `Markdig` version

The current `Directory.Packages.props` pins `Markdig` to `1.4.2`. The previous per-csproj declarations used `0.38.0`. The migration to CPM consolidated to the latest stable (`1.3.2` at the time of writing); the pin has since moved to `1.4.2`, which adds the `IsAotCompatible` / `IsTrimmable` assembly metadata and a set of CommonMark conformance fixes. The strict GFM pipe-table mode that 1.4.0 introduced (`PipeTableOptions.UseGfmRules`) is opt-in and defaults to `false`, so the parsed output of the two consumers — `Harbor.Desktop.Shared` and `Harbor.App.Avalonia` — does not change unless they ask for it. If your build fails on Markdig API changes (the 1.x release was a breaking change from 0.x), check the [Markdig release notes](https://github.com/xoofx/markdig/releases) and either:
- Downgrade `Markdig` to `0.38.0` in `Directory.Packages.props` (1-line change), or
- Update the call sites to the new 1.x API.

### 4.6 Packages that will be added in the desktop-app sprint

The new `apps/Harbor.App.*` + `desktop/Harbor.Desktop.*` projects (per `docs/DESKTOP_APP_PLAN.md`) will require these additional packages, to be added to `Directory.Packages.props`:

> **Note (sprint-2):** `Harbor.App.{Wpf,Maui,Blazor}` now live in [`contrib/apps/`](../contrib/apps/)
> and build via `contrib/Contrib.slnx`; only `Harbor.App.{Cli,Avalonia}` remain in the main solution.

```xml
<!-- ── Desktop app stacks ───────────────────────────────────────────── -->
<PackageVersion Include="Avalonia.Controls.DataGrid" Version="11.2.7" />
<PackageVersion Include="Avalonia.Controls.TreeDataGrid" Version="11.2.7" />
<PackageVersion Include="Toast.Avalonia" Version="11.0.0" />
<PackageVersion Include="LiveChartsCore.SkiaSharpView.Avalonia" Version="2.0.0-rc6" />
<PackageVersion Include="Projektanker.Icons.Avalonia.FontAwesome" Version="9.4.0" />
<PackageVersion Include="Markdown.Avalonia" Version="11.0.3-a1" />
<PackageVersion Include="Notification.Wpf" Version="6.4.0" />
<PackageVersion Include="MdXaml" Version="1.30.0" />
<PackageVersion Include="Microsoft.Maui.Controls" Version="10.0.0" />
<PackageVersion Include="Microsoft.Maui.Controls.Compatibility" Version="10.0.0" />
<PackageVersion Include="CommunityToolkit.Maui" Version="10.0.0" />
<PackageVersion Include="LiveChartsCore.SkiaSharpView.Maui" Version="2.0.0-rc6" />
<PackageVersion Include="BlazorMonaco" Version="3.2.0" />
<PackageVersion Include="MudBlazor" Version="8.0.0" />
<PackageVersion Include="LiveChartsCore.SkiaSharpView.Blazor" Version="2.0.0-rc6" />

<!-- ── Code editor + diff + fuzzy ───────────────────────────────────── -->
<PackageVersion Include="DiffPlex" Version="1.9.0" />
<PackageVersion Include="FuzzySharp" Version="2.0.2" />
<PackageVersion Include="Pty.Net" Version="0.5.81" />

<!-- ── Toml config (read/write ~/.harbor/config.toml) ───────────────── -->
<PackageVersion Include="Tomlyn" Version="0.17.0" />

<!-- ── Git integration ──────────────────────────────────────────────── -->
<PackageVersion Include="LibGit2Sharp" Version="0.31.0" />
```

These will be added by Subagents D1/D2/D3 as they implement the desktop apps.

---

## 5. How to add a new package

To add a new NuGet package (e.g. `Tomlyn` 0.17.0) to a project:

### 5.1 Step 1 — Add the `PackageVersion` to `Directory.Packages.props`

```xml
<!-- Directory.Packages.props -->
<ItemGroup>
  <!-- ...existing entries... -->
  <PackageVersion Include="Tomlyn" Version="0.17.0" />
</ItemGroup>
```

Place it in the appropriate group (e.g. under "Performance / collections / serialization" or in a new "Config files" group).

### 5.2 Step 2 — Add the `PackageReference` (no Version) to the .csproj

```xml
<!-- src/Harbor.Application/Harbor.Application.csproj -->
<ItemGroup>
  <PackageReference Include="Tomlyn"/>
</ItemGroup>
```

### 5.3 Step 3 — Build to verify

```bash
dotnet restore
dotnet build src/Harbor.Application/Harbor.Application.csproj
```

If you forgot step 1, you'll see:

```
error NU1007: Dependency specified was 'Tomlyn' but it was not found in the
              central package management file at /Directory.Packages.props.
```

If you forgot step 2, no error — the package just isn't consumed by your project.

### 5.4 For private assets (analyzers, source generators)

If the package is an analyzer or source generator that shouldn't flow to consuming projects, use `<PrivateAssets>` and `<IncludeAssets>` in the .csproj (these go in the .csproj, NOT in `Directory.Packages.props`):

```xml
<!-- src/Harbor.Application/Harbor.Application.csproj -->
<PackageReference Include="ZLinq.DropInGenerator">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

The `Version` still comes from `Directory.Packages.props`:

```xml
<PackageVersion Include="ZLinq.DropInGenerator" Version="1.5.6" />
```

---

## 6. How to upgrade a package

To upgrade `ZLinq` from `1.5.6` to `1.6.0`:

### 6.1 Single-line change in `Directory.Packages.props`

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="ZLinq" Version="1.6.0" />  <!-- was 1.5.6 -->
```

### 6.2 Build to verify

```bash
dotnet restore
dotnet build
```

That's it. Every project that references `ZLinq` now uses `1.6.0`. No need to touch individual `.csproj` files.

### 6.3 Check for breaking changes

```bash
# Look at the release notes
xdg-open https://github.com/Cysharp/ZLinq/releases

# Or run all tests to catch runtime breaks, one project at a time
# (plain executables — `dotnet test` is not used here, see CONTRIBUTING.md)
for t in tests/Harbor.*.Tests; do
  dotnet run --project "$t" -c Release -- --minimum-expected-tests 1
done
```

### 6.4 Upgrade a major version (e.g. 1.x → 2.x)

For major version bumps with potential breaking changes:

1. Read the upgrade guide in the package's README.
2. Make the change in `Directory.Packages.props`.
3. Build — fix any compile errors.
4. Run all tests — fix any test failures.
5. Commit with a message like `chore(deps): bump ZLinq 1.5.6 → 2.0.0`.

---

## 7. How to remove a package

To remove `Moq` from `tests/Harbor.Tui.Tests`:

### 7.1 Step 1 — Delete the `<PackageReference>` from the .csproj

```xml
<!-- tests/Harbor.Tui.Tests/Harbor.Tui.Tests.csproj -->
<ItemGroup>
  <!-- DELETE THIS LINE: <PackageReference Include="Moq" Version="4.20.70"/> -->
</ItemGroup>
```

### 7.2 Step 2 — Leave `Directory.Packages.props` alone

Keep the `<PackageVersion Include="Moq" Version="4.20.70" />` entry — it's harmless if no project references it. If you want to be tidy, you can delete it, but only if NO project in the repo still references it.

### 7.3 Step 3 — Build to verify

```bash
dotnet build tests/Harbor.Tui.Tests/Harbor.Tui.Tests.csproj
```

If any code in `Harbor.Tui.Tests` still uses `Moq`, you'll get compile errors. Fix or delete the code.

---

## 8. Migration plan — what was done to move Harbor to CPM

This migration was performed by Subagent C (project split + central packages) in round R4. Here's what was done:

### 8.1 Step 1 — Inventory all existing `PackageReference` entries

Run this one-liner to enumerate every package and version across the repo:

```bash
# Find every .csproj, extract every <PackageReference Include="X" Version="Y"/>
find src tests samples apps -name '*.csproj' -exec grep -h 'PackageReference' {} \; \
  | grep -oE 'Include="[^"]+"|Version="[^"]+"' \
  | paste - - \
  | sort -u
```

### 8.2 Step 2 — Create `Directory.Packages.props` with all unique packages

For each unique `(Include, Version)` pair, add a `<PackageVersion Include="X" Version="Y"/>` entry. Group by category (Microsoft.Extensions.* → Microsoft.CodeAnalysis.* → ... → source generators).

### 8.3 Step 3 — Enable CPM in `Directory.Build.props`

Add a new `<PropertyGroup>` near the top:

```xml
<PropertyGroup>
  <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
</PropertyGroup>
```

### 8.4 Step 4 — Strip `Version="..."` from every `.csproj`

For each `.csproj` file, replace:

```xml
<PackageReference Include="X" Version="Y"/>
```

with:

```xml
<PackageReference Include="X"/>
```

For multi-line references:

```xml
<PackageReference Include="X" Version="Y">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

becomes:

```xml
<PackageReference Include="X">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

This can be scripted:

```bash
# Strip Version="..." from every .csproj (one-liner with sed)
find src tests samples apps -name '*.csproj' -exec \
  sed -i -E 's/<PackageReference Include="([^"]+)" Version="([^"]+)"(.*)/<PackageReference Include="\1"\3/g' {} \;
```

⚠️ **Always commit a checkpoint before running sed across the whole repo.** The regex above handles the common case but may miss edge cases (e.g. `Version=` on a separate line).

### 8.5 Step 5 — Resolve any version conflicts

If the same package was declared with different versions across the repo, pick the highest version that satisfies all transitive minimums. For Harbor, this meant:

- All `Microsoft.Extensions.*` packages → `10.0.13` (the migration consolidated `10.0.0` / `10.0.8` onto `10.0.10`; the pin has since moved to `10.0.13`)
- `Markdig` → `1.4.2` (the migration moved `0.38.0` to `1.3.2`; the pin has since moved to `1.4.2`)

### 8.6 Step 6 — Restore + build + test

```bash
dotnet restore
dotnet build
# Tests are plain executables, one project at a time — `dotnet test` is not
# used in this repo (see CONTRIBUTING.md §Why not `dotnet test`).
for t in tests/Harbor.*.Tests; do
  dotnet run --project "$t" -c Release -- --minimum-expected-tests 1
done
```

### 8.7 Step 7 — Commit

```bash
git add Directory.Packages.props Directory.Build.props src/ tests/ samples/ apps/
git commit -m "chore(deps): migrate to Central Package Management (CPM)

- Add Directory.Packages.props with 41 pinned packages
- Enable ManagePackageVersionsCentrally in Directory.Build.props
- Enable CentralPackageTransitivePinningEnabled
- Strip Version= attributes from every .csproj
- Consolidate Microsoft.Extensions.* to 10.0.10 (was 10.0.0 / 10.0.8 mix)
- Consolidate Markdig to 1.3.2 (was 0.38.0)

See docs/CENTRAL_PACKAGE_MANAGEMENT.md for the full guide."
```

---

## 9. CPM + transitive pinning — the full safety net

`CentralPackageTransitivePinningEnabled=true` is the killer feature for security. Here's why:

### 9.1 Without transitive pinning

Suppose:
- Your code declares `<PackageReference Include="A"/>` (no Version)
- `Directory.Packages.props` declares `<PackageVersion Include="A" Version="2.0.0"/>`
- Package `A v2.0.0` depends on `B v1.0.0`
- `Directory.Packages.props` does NOT declare `B`

NuGet restores:
- `A v2.0.0` (from CPM)
- `B v1.0.0` (transitive — whatever A asked for)

If `B v1.0.0` has a known vulnerability (e.g. [CVE-2024-XXXX](https://github.com/advisories)), you're shipping a vulnerable transitive even though your direct dependencies are all current.

### 9.2 With transitive pinning

Same setup, but now:
- `Directory.Packages.props` also declares `<PackageVersion Include="B" Version="1.2.0"/>` (the fixed version)

NuGet restores:
- `A v2.0.0` (from CPM)
- `B v1.2.0` (CPM pin overrides the transitive `B v1.0.0` request)

Now you're shipping the patched `B`. The pin is enforced even though no project directly references `B`.

### 9.3 How to discover transitives

```bash
# List every transitive dependency in the repo
dotnet list package --include-transitive

# Check for known vulnerabilities
dotnet list package --vulnerable
```

If a vulnerable transitive shows up, add it to `Directory.Packages.props` with the patched version. Done.

### 9.4 Harbor's current transitive pinning status

Harbor pins **80** packages directly. The transitives that matter (e.g. `Microsoft.Extensions.Configuration.Binder`, which is pulled in transitively by `Terminal.Gui`) are NOT explicitly pinned in `Directory.Packages.props` — they get whatever version the direct dependency asked for. With `CentralPackageTransitivePinningEnabled=true`, this is safe as long as the direct dependencies are current. Subagent C verified there are no known vulnerabilities via `dotnet list package --vulnerable`.

---

## 10. CPM vs Paket vs NuGet.exe — why CPM won

| Feature | CPM (NuGet 6+) | Paket | Per-csproj NuGet |
|---------|----------------|-------|-------------------|
| Single source of truth | ✅ `Directory.Packages.props` | ✅ `paket.dependencies` | ❌ N .csproj files |
| Built into .NET SDK | ✅ (no extra install) | ❌ (extra tool) | ✅ |
| Transitive pinning | ✅ (built-in) | ✅ (always on) | ❌ |
| IDE support | ✅ VS / Rider / VSCode | ✅ (with plugin) | ✅ |
| Migration effort from per-csproj | Low (script-able) | High (rewrite all deps) | N/A |
| Community adoption | Growing fast (default for new .NET projects) | Declining | Legacy |
| Lock file | `packages.lock.json` (opt-in) | `paket.lock` | None |

**Why CPM won for Harbor:**
1. No extra tool to install (built into .NET 10 SDK).
2. The migration was a 1-day job (per §8).
3. Works with all IDEs the team uses (VS / Rider / VSCode / Neovim).
4. `CentralPackageTransitivePinningEnabled` gives us the same security guarantee Paket's lock file provides.
5. Future-proof — Microsoft is investing in CPM, not Paket.

---

## 11. Common pitfalls

### 11.1 Forgetting to add the `PackageVersion` entry

**Symptom:**

```
error NU1007: Dependency specified was 'Tomlyn' but it was not found in the
              central package management file at /Directory.Packages.props.
```

**Fix:** Add `<PackageVersion Include="Tomlyn" Version="0.17.0"/>` to `Directory.Packages.props`.

### 11.2 Adding a `Version=` attribute to a `<PackageReference>` while CPM is enabled

**Symptom:**

```
warning NU1605: PackageReference Version 'X' was ignored because the package
                is managed centrally.
```

(or build error, depending on NuGet version)

**Fix:** Delete the `Version="..."` attribute from the `<PackageReference>` in the .csproj.

### 11.3 Different versions across multi-targeted projects

If `contrib/apps/Harbor.App.Maui` targets both `net10.0-desktop` and `net10.0-maccatalyst`, and you need different package versions per target, use a conditional `<PackageVersion>`:

```xml
<!-- Directory.Packages.props -->
<ItemGroup>
  <PackageVersion Include="Microsoft.Maui.Controls" Version="10.0.0"
                  Condition="'$(TargetFramework)' == 'net10.0-desktop'" />
  <PackageVersion Include="Microsoft.Maui.Controls" Version="10.0.0-preview.10"
                  Condition="'$(TargetFramework)' == 'net10.0-maccatalyst'" />
</ItemGroup>
```

### 11.4 Test project packages

Test projects (e.g. `Harbor.Tui.Tests`) often need packages that production code doesn't (e.g. `Moq`, `TUnit`). These are still declared in `Directory.Packages.props` (no special treatment needed). The `Directory.Build.targets` file handles the "this is a test project" detection:

```xml
<!-- Directory.Build.targets -->
<ItemGroup Condition="$(IsTestProject) == 'true'">
  <PackageReference Include="TUnit"/>
</ItemGroup>
```

The `Version` comes from `Directory.Packages.props`:

```xml
<PackageVersion Include="TUnit" Version="0.50.0" />
```

### 11.5 Source generators need `<PrivateAssets>all</PrivateAssets>`

Source generators (e.g. `ZLinq.DropInGenerator`, `Termina.Generators`) should be marked as private assets so they don't flow to consuming projects:

```xml
<!-- src/Harbor.Application/Harbor.Application.csproj -->
<PackageReference Include="ZLinq.DropInGenerator">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

The `Version` still comes from `Directory.Packages.props` — `<PrivateAssets>` and `<IncludeAssets>` are per-project consumption settings, not version settings.

### 11.6 Analyzers declared in `Directory.Build.props`

Harbor declares analyzers (Roslynator, SonarAnalyzer, etc.) in `Directory.Build.props` rather than per-csproj, because they apply to every project. The `Version` comes from `Directory.Packages.props`:

```xml
<!-- Directory.Build.props -->
<ItemGroup>
  <PackageReference Include="Roslynator.Analyzers">
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
</ItemGroup>
```

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Roslynator.Analyzers" Version="4.14.0" />
```

### 11.7 Don't edit `Directory.Packages.props` from inside an IDE "Manage NuGet Packages" UI

Most IDEs (VS, Rider) will try to write a `Version="..."` attribute back to the .csproj when you use the GUI "Manage NuGet Packages" dialog. This will trigger warning NU1605. Always edit `Directory.Packages.props` by hand (or via `dotnet add package`).

---

## 12. Verifying CPM is working

### 12.1 Verify the switch is on

```bash
grep ManagePackageVersionsCentrally Directory.Build.props
# Expected: <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>

grep CentralPackageTransitivePinningEnabled Directory.Build.props
# Expected: <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
```

### 12.2 Verify no `.csproj` has a `Version=` on a `PackageReference`

```bash
# Should print nothing:
grep -rn '<PackageReference Include="[^"]*" Version="' src/ tests/ samples/ apps/
```

(If anything prints, that .csproj still has a `Version=` that needs to be stripped.)

### 12.3 Verify restore works

```bash
dotnet restore --force
# Expected: "All projects are up-to-date on restore" or similar; no errors
```

### 12.4 Verify a sample package resolves from CPM

```bash
# Check what version of ZLinq gets restored
dotnet list src/Harbor.Application/Harbor.Application.csproj package | grep ZLinq
# Expected: ZLinq 1.5.6 (matches Directory.Packages.props)
```

### 12.5 Verify transitive pinning

```bash
dotnet list package --include-transitive
# Should show direct + transitive packages, all from Directory.Packages.props
```

### 12.6 Verify no known vulnerabilities

```bash
dotnet list package --vulnerable
# Expected: "no vulnerabilities found" (or list of vulnerable packages to fix)
```

---

## 13. References

### 13.1 Microsoft docs

- **Central Package Management (official)** — https://learn.microsoft.com/nuget/consume-packages/central-package-management
- **`Directory.Packages.props` reference** — https://learn.microsoft.com/nuget/consume-packages/central-package-management#directorypackagesprops
- **`Directory.Build.props` and `Directory.Build.targets`** — https://learn.microsoft.com/visualstudio/msbuild/customize-your-build
- **`ManagePackageVersionsCentrally` property** — https://learn.microsoft.com/nuget/consume-packages/central-package-management#enabling-central-package-management
- **`CentralPackageTransitivePinningEnabled` property** — https://learn.microsoft.com/nuget/consume-packages/central-package-management#transitive-pinning

### 13.2 Blog posts / articles

- **"Central Package Management in NuGet"** (Microsoft DevBlogs, 2022) — https://devblogs.microsoft.com/nuget/introducing-central-package-management/
- **"Migrating to Central Package Management"** (Andrew Lock, 2023) — https://andrewlock.net/series/migrating-to-central-package-management/

### 13.3 Internal Harbor docs

- `Directory.Packages.props` (repo root) — the actual central package version file
- `Directory.Build.props` (repo root) — common MSBuild properties + CPM switch
- `Directory.Build.targets` (repo root) — common MSBuild targets
- `docs/DESKTOP_APP_PLAN.md` — master plan for the 4 desktop apps (companion doc, written in same round)
- `docs/FEATURE_RESEARCH.md` §11 — grok-build analysis (companion doc, written in same round)
- `docs/ARCHITECTURE_LAYERS.md` — Clean / Hexagonal / Onion layering rules
- `Harbor.slnx` — solution file (will be updated to add new desktop-app projects in the next round)

---

## 14. The restore: the audit pin, the lock files, and the CI cache

> **Decided on 2026-10-20, issue #1857.** Every number below is measured, not
> estimated: the graph from the live nuget.org package metadata, the cache
> sizes from the GitHub Actions cache API, the restore times from the CI run
> logs. The method is written out with each number so the next reader can
> repeat it.

### 14.1 The NuGet audit pin

`Directory.Build.props` pins two MSBuild properties:

```xml
<NuGetAuditMode>all</NuGetAuditMode>
<NuGetAuditLevel>moderate</NuGetAuditLevel>
```

**The SDK default they are written against.** NuGet 7.0 (the .NET 10 SDK)
changed the default of `NuGetAuditMode`: it is `all` for a project that
targets `net10.0` or higher, and `direct` below that. Harbor targets
`net10.0`, so the effective default here is already `all`; `direct` is the
pre-NuGet-7.0 (.NET 8/9 SDK) behaviour and is still the default for a
project below net10.0. The pin of `all` therefore does not turn the audit on
. It makes the gate a property of the repository rather than a property of
the SDK, and it is the value a project that drops below net10.0 falls back
on.

`NuGetAuditLevel` is the other half. The SDK default is `low`, which reports
every advisory: NU1901 (low), NU1902 (moderate), NU1903 (high), NU1904
(critical). The pin to `moderate` keeps NU1902/NU1903/NU1904 inside the gate
and drops NU1901. That is a narrowing of the SDK default, and it is
deliberate: under `TreatWarningsAsErrors=true`, a low-severity advisory in a
transitive package this repository does not control would break every build
in the repository on the day the advisory is published, and the severities
this repository has actually had to act on are the moderate and high ones:
the MessagePack and ImageSharp advisories, whose per-project `NU1902;NU1903`
suppressions #1911 removed.

**One file governs the pin, and it is the root `Directory.Build.props`.**
`apps/`, `tests/` and `src/` import it by RELATIVE path. The relative path
is not a style choice: the project-file analyzer can only follow an import it
can resolve on disk, and with the `GetPathOfFileAbove` property function it
could not. `src/Directory.Build.props` was the last file still using the
function form, and the consequence was a red build: 60 `error Proj0040:
Set <NuGetAuditMode> to all` on the dev build of 2026-10-20, because the
analyzer could not see the pin from `src/`.

The two places the pin does NOT reach, so the claim is not read as covering
them: `build/` (the NUKE tool, which declares that it does not inherit
solution-wide strictness. It audits at the SDK default of `all`/`low`,
which is WIDER than the repository's gate) and `contrib/` (not built by CI).

### 14.2 The package graph, measured

The graph below was walked from the live nuget.org metadata on 2026-10-20:
the direct `<PackageReference>` set out of the tree, then each package's
nuspec, resolved the way NuGet resolves it (the version CPM pins, or the
lowest version that satisfies the range).

| | |
|---|---|
| Packages in the resolved graph | **196** |
| Dependency edges | 492 |
| Distinct version ranges in the graph | 68 |
| Ranges that FLOAT (`1.0.*`) | **0** |
| Ranges with NO lower bound | **0** |
| Packages whose version `Directory.Packages.props` pins | 60 (every direct reference) |
| Packages resolved by a transitive range instead | 136 |
| `packages.lock.json` files in the tree | **0** |
| Projects that set `RestorePackagesWithLockFile` | **0** |
| Projects that set `RestoreLockedMode` | **0** |

**What the numbers say about reproducibility.** 66 of the 68 ranges are a
bare minimum version (NuGet's `>= x.y.z` form), one is `[2.5.1, 3.0.0)` and
two are exact. Not one of them floats and not one of them is open at the
bottom, so NuGet's lowest-applicable rule always has a fixed lower bound to
resolve against. The resolved graph is therefore a function of
`Directory.Packages.props` and the immutable nuspecs of the packages it
names, which is the same conclusion the `docs/ANALYZERS.md` Proj0040 row
reaches from the other side: a transitive advisory is Harbor's subject
because Harbor decides the transitive versions.

### 14.3 The lock files: NOT adopted, and why

**Decision: `RestorePackagesWithLockFile` stays off, and the tree stays
all-or-nothing.** No project has a `packages.lock.json` and no project sets
the property, so there is no half-migrated state for anybody to inherit.

The reasons, in the order they decided it:

1. **The restore is already reproducible** (§14.2). A lock file's value here
   is a DETECTOR: it fails a restore whose resolved graph no longer matches
   the recorded one, not a fix. The drift it would catch (a package
   unlisted, or a version published later that is LOWER than the current
   lowest applicable) is real but narrow.
2. **The cost is per project.** `RestorePackagesWithLockFile` is a property
   that makes NuGet write one file next to every project: ~140 files, each
   of which every dependabot PR has to reconcile, and each of which turns
   stale into a red CI for everybody else (`RestoreLockedMode` fails the
   restore). The tree would have to land in ONE wave, and a wave that
   half-lands is the worst state of all.
3. **The cheaper detector is the mechanism already in use.**
   `CentralPackageTransitivePinningEnabled` is on, and
   `Directory.Packages.props` pins the transitive versions it names
   deliberately. Pinning the REMAINING 136 transitive packages the same way
   is 136 lines in ONE file: one reviewable diff, no per-project file to
   keep in sync, and no dependabot conflict that is not already a conflict
   on the props file. That is the follow-up, and it is not this change.

**The gate that keeps the decision honest** is `harbor-gates check-test-packages`
(the `test-packages` CI job). It reports the states the tree must never be
in:

| Rule | The state it refuses |
|------|----------------------|
| `RESTORE-INPUTS-UNENFORCED` | a `packages.lock.json` exists but nothing sets `RestorePackagesWithLockFile`, so NuGet never writes it, never checks it, and the file rots |
| `RESTORE-INPUTS-UNWRITTEN` | `RestorePackagesWithLockFile` is set and no project has a lock file, so every restore writes one and the tree is dirty after every restore |
| `RESTORE-LOCKED-WITHOUT-FILES` | `RestoreLockedMode` is set without `RestorePackagesWithLockFile`, which is a restore that fails |
| `RESTORE-INPUTS-PARTIAL` | a scope where some projects carry a lock file and others do not, which means two projects in one scope restore differently |

Its `--self-test` carries a fixture for each rule and one for the green
case (every project of the scope locked, property set), so the gate is known
to bite and known not to cry wolf.

**What the wave would need, when it is taken up:** a machine that can
restore (this change was authored where `dotnet restore` is not available),
the lock files regenerated and committed for EVERY project in one commit, the
gate above extended to compare them against the recorded graph, and a
dependabot story that keeps them in step.

### 14.4 The CI cache: what a pin change costs, and the fix

`actions/setup-dotnet@v6` with `cache: true` and
`cache-dependency-path: "Directory.Packages.props"` builds the cache key as
`dotnet-cache-${os}-${hashFiles('Directory.Packages.props')}` and calls
`cache.restoreCache(paths, primaryKey)` with **no restore keys** (the
action's own source, `src/cache-restore.ts`). Two consequences, both
measured:

* **A pin change is a TOTAL cache miss.** Nothing falls back to the previous
  entry, so the whole graph is downloaded again: **452 MiB across 196
  packages**, measured by pulling the whole graph from nuget.org on
  2026-10-20 (45 s wall clock at 16 parallel connections). The cost in CI
  job time is smaller than the byte count suggests. The restore step took
  21-30 s cold against 13 s warm, so **8-17 s per restoring job**, and there
  are two jobs that restore (`build` and `test-packages`).
* **The cache entry is 1.54 GiB, and there are 7 of them.** Read from the
  GitHub Actions cache API on 2026-10-20: each `dotnet-cache-Linux-…`
  entry is 1,617,932,154 bytes, and the repository held 7 entries
  (10.8 GiB) against GitHub's 10 GB per-repository quota. The entries are
  per props-hash AND per ref, so a PR that changes a pin adds one, and a
  dev push after a pin change adds another.

**The fix, when the CI lane is next restructured:** give the cache a
restore-key fallback, so a miss costs the DELTA instead of the whole graph.
`setup-dotnet` does not expose one, so it takes an explicit `actions/cache`
step in place of the built-in one:

```yaml
      - name: Cache the NuGet packages (a miss costs the delta, not the graph)
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('Directory.Packages.props') }}
          restore-keys: |
            nuget-${{ runner.os }}-
```

**This change does not apply it**, and the reason is a conflict, not a
judgement: `.github/workflows/ci.yml` is claimed by four open PRs
(1923, 1944, 1937 and 1946), and 1923 ("split the PR lane") restructures
the lanes and adds a `pack` job that consumes the same cache key. A
cache-key change made now would be re-landed or lost in that restructure;
it belongs with it.

---

## Document metadata

- **Author:** Subagent G (researcher-r4)
- **Date:** 2026
- **Task ID:** G
- **Files written:**
  - `docs/CENTRAL_PACKAGE_MANAGEMENT.md` (this file)
  - `docs/FEATURE_RESEARCH.md` §11 (grok-build analysis — companion)
  - `docs/DESKTOP_APP_PLAN.md` (4-app master plan — companion)
- **Migration status:** ✅ Complete (performed by Subagent C in round R4)

End of document.
