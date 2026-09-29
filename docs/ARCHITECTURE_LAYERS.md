# Architecture Layers — Canonical Reference

> **This is the canonical reference for project layering in Harbor.**
> Every `<ProjectReference>` added to a `.csproj` MUST be checked against this document.
> See [ARCHITECTURE.md](./ARCHITECTURE.md) for the high-level design and
> [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) §ARCH-001+ for the audit trail.
>
> **Связанные документы:**
> - [ARCHITECTURE.md](./ARCHITECTURE.md) — high-level design + principles summary.
> - [CLAUDE.md](../CLAUDE.md) §Layering rules — short version + checklist.
> - [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) §ARCH-001..NNN — violations found and fixed.

---

## 1. The layering model

Harbor follows **Clean / Hexagonal / Onion Architecture** (call it whatever you want — the
user said *"слои должны быть по чистой архитектуре, гексогональная луковая называй как хочешь
но это надо"*). The dependency direction is **inward only**: an outer layer may reference an
inner layer, never the other way around. The innermost layer (Domain/Abstractions) references
**nothing** except the BCL and a small set of framework packages.

```
┌─────────────────────────────────────────────────────────────────┐
│  PRESENTATION (UI / CLI — composition roots)                    │
│  - apps/Harbor.App.Cli                                          │
│  - apps/Harbor.App.Avalonia (cross-platform desktop GUI)        │
│  - contrib/apps/: Harbor.App.Wpf / App.Maui / App.Blazor        │
│  - In-solution TUI renderers: Tui.AnsiPlain (unified ANSI +     │
│    plain) / Tui.CellForge (+ .Engine) / Tui.NickConsoleEx /    │
│    Tui.Notifications                                            │
│  - Optional contrib/tui renderers: Spectre / .Fullscreen /      │
│    SpectreTui / TerminalGui / Termina / RazorConsole / Sixel    │
│  Depends on: Application + Ui.Framework + Abstractions          │
└─────────────────────────────────────────────────────────────────┘
                                  ▲
                                  │ uses
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  UI FRAMEWORK (TEA + reusable VMs + components)                 │
│  - Harbor.Ui.Framework (+ split projects Abstractions/, State/, │
│    ViewModels/, Rendering/, Projection/, Services/, Sessions/;  │
│    shell csproj is a meta-package)                             │
│    State/      (UiStore, UiState{Ui,Chat}, AppMsg/ChatAppMsg,   │
│                AppReducer/ChatAppReducer — TEA)                  │
│    ViewModels/ (ChatLineVM, ToolCallVM, TokenUsageVM, ...)      │
│    Rendering/  (ChatMessageRenderer, ChatStreamingPresenter)    │
│    Sessions/   (SessionFactory, SessionSwitcher, SessionContext,│
│                 SessionGitTracker, IChatViewBinder)             │
│    Panels/     (dockable panel system)                          │
│    Services/   (IDispatcherAdapter, IThemeService, IToastService,│
│                 GitService, SessionStatusTracker)               │
│    Configuration/ (ICommonConfigReader)                         │
│  Depends on: Abstractions + Desktop.Abstractions                │
│              (circular-dep workaround: ICommonConfigReader)     │
└─────────────────────────────────────────────────────────────────┘
                                  ▲
                                  │ uses
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  APPLICATION (use cases, orchestration)                         │
│  - Harbor.Application (AgentLoop, Sessions, Agents,             │
│                        Configuration, Permissions, Onboarding)  │
│  - Harbor.Registries                                            │
│  - Harbor.Plugins.{Abstractions, Runtime, Hosting, Registration,│
│    Instantiation, Compilation, Storage, Host} (8 projects)      │
│  - contrib/scripting: Harbor.Scripting.* (ScriptHost, Bridge)   │
│  - Harbor.Ipc.{Abstractions, InProcess, Server, Client}         │
│  - Harbor.Logging (Serilog per-run timestamped files)           │
│  Depends on: Domain ONLY (Harbor.Abstractions +                 │
│              Harbor.Abstractions.Contracts +                    │
│              Harbor.Desktop.Abstractions)                       │
└─────────────────────────────────────────────────────────────────┘
                                  ▲
                                  │ implements
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  INFRASTRUCTURE (adapters, I/O, external services)              │
│  - Harbor.Storage.Jsonl / Memory / Sqlite                       │
│  - Harbor.Providers.OpenAiCompatible / Anthropic / OpenAI /     │
│    Ollama (+ Shared — linked-source, без .csproj)               │
│  - Harbor.Tools.Builtin — все builtin tools в одном проекте,    │
│    каталог Tools/ (20 tools — см. docs/TOOLS_CATALOG.md;        │
│    MCP-клиент в подкаталоге Mcp/)                               │
│  - DesignSystem — отдельный проект src/Harbor.DesignSystem/     │
│  Depends on: Domain ONLY                                        │
└─────────────────────────────────────────────────────────────────┘
                                  ▲
                                  │ declares
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  DOMAIN / ABSTRACTIONS (the hexagon core)                       │
│  - Harbor.Abstractions (interfaces, events, value objs,         │
│    Plugins/IPlugin, PermissionRuleset, Identifiers, …)          │
│  - Harbor.Abstractions.Contracts (Models: Session, ContentPart, │
│    ToolResult, Usage, Pricing etc.; namespace                   │
│    `Harbor.Abstractions.Models`. Бывший `Harbor.Domain.dll` —   │
│    переименован в F1 decoupling (ADR-007, commit fa8d3ae).      │
│  - Harbor.Desktop.Abstractions (Configuration: CommonConfig,    │
│    ICommonConfigStore; base VMs for cross-platform reuse)       │
│  - Harbor.Terminal.Abstractions (TUI interfaces, ITuiPlugin)    │
│  Depends on: NOTHING (only BCL + CSharpFunctionalExtensions +   │
│              Microsoft.Extensions.Logging.Abstractions etc. —   │
│              no other Harbor project)                           │
│  EXCEPTION: Harbor.Desktop.Abstractions → Harbor.Ui.Framework   │
│             (via Harbor.Terminal.Abstractions). Worked around   │
│             via ICommonConfigReader in Ui.Framework.            │
└─────────────────────────────────────────────────────────────────┘
```

### Why so many projects in the Domain layer?

| Project | Why it's separate | Why it's in Domain (not Application) |
|---|---|---|
| `Harbor.Abstractions` | Pure contract surface for the agent harness (LLM, tools, sessions, events, permissions, plugins). A headless consumer (CLI script, MCP bridge, test harness) can reference just this. | Zero dependencies — only BCL + CSharpFunctionalExtensions. |
| `Harbor.Abstractions.Contracts` | Holds the concrete model types (`Session`, `ContentPart`, `ToolResult`, `Usage`, etc.). They declare `namespace Harbor.Abstractions.Models` so consumers don't need a second `using`. Бывший `Harbor.Domain.dll` — переименован в F1 decoupling (ADR-007, commit fa8d3ae, 2026-08-24). | Pure data + formatters — no I/O. |
| `Harbor.Desktop.Abstractions` | Cross-platform contracts shared by every desktop app (Avalonia / WPF / MAUI / Blazor): `CommonConfig`, `ICommonConfigStore`, base VMs. | Configuration schema + VM contracts are stable across platforms. |
| `Harbor.Terminal.Abstractions` | TUI contracts: `ITuiRenderer`, `ITuiPlugin`, panel system entry points. Kept separate from `Harbor.Ui.Framework` because terminal vocabulary (Spectre, ANSI) is not relevant to desktop GUIs. | Used by both `Harbor.Ui.Framework` (panel system) and concrete TUI renderers. |

### Circular-dependency workaround: `ICommonConfigReader`

```
Harbor.Ui.Framework
  ↓ (needs to read config for SessionFactory)
Harbor.Desktop.Abstractions (has ICommonConfigStore + CommonConfig)
  ↓ (uses Ui.Framework.ViewModels via GlobalUsings)
Harbor.Terminal.Abstractions (references Ui.Framework)
  ↓
Harbor.Ui.Framework  ← CYCLE!
```

**Fix**: declared `ICommonConfigReader` in `Harbor.Ui.Framework/Configuration/` with a narrow
contract (just `TryReadProviderModelAsync`). Each platform app implements it as an adapter
over its own `ICommonConfigStore` (e.g. `CommonConfigReaderAdapter` in Avalonia).

### Mermaid diagram

```mermaid
flowchart TB
    subgraph Pres["Presentation (UI / CLI)"]
        Cli["Harbor.App.Cli<br/>(Composition Root)"]
        TuiAnsi["Harbor.Tui.AnsiPlain (ANSI + plain)<br/>/ .Notifications"]
        TuiConsoleEx["Harbor.Tui.CellForge (+ .Engine)<br/>/ .NickConsoleEx (cell-diff backends)"]
        TuiContrib["contrib/tui: Spectre / SpectreTui<br/>/ TerminalGui / Termina / RazorConsole / Sixel"]
    end

    subgraph App["Application (use cases)"]
        AppLayer["Harbor.Application<br/>(AgentLoop, config, permissions)"]
        Core["Harbor.Registries"]
        Plugins["Harbor.Plugins.*<br/>(8 projects, Roslyn CS-source)"]
    end

    subgraph Infra["Infrastructure (adapters)"]
        Storage["Harbor.Storage.Jsonl / Memory / Sqlite"]
        Providers["Harbor.Providers.OpenAiCompatible / Anthropic / OpenAI / Ollama<br/>(+ Shared linked-source, no csproj)"]
        Tools["Harbor.Tools.Builtin<br/>(20 tools incl. MCP client)"]
    end

    subgraph Domain["Domain / Abstractions (hexagon core)"]
        Abs["Harbor.Abstractions + Abstractions.Contracts<br/>(IAgent, ITool, ILlmClient, ISessionStore, ...)"]
        TuiAbs["Harbor.Terminal.Abstractions<br/>(ITuiRenderer, UiState, panels)"]
    end

    Cli --> Core
    Cli --> Plugins
    Cli --> Storage
    Cli --> Providers
    Cli --> Tools
    Cli --> Abs
    Cli --> TuiAbs

    TuiAnsi --> Abs
    TuiAnsi --> TuiAbs
    TuiConsoleEx --> Abs
    TuiConsoleEx --> TuiAbs
    TuiContrib --> Abs
    TuiContrib --> TuiAbs

    AppLayer --> Abs
    Core --> Abs
    Plugins --> Abs
    Plugins --> TuiAbs

    Storage --> Abs
    Providers --> Abs
    Tools --> Abs

    TuiAbs --> Abs

    classDef domain fill:#d4edda,stroke:#28a745,stroke-width:2px
    classDef app fill:#cce5ff,stroke:#007bff,stroke-width:2px
    classDef infra fill:#fff3cd,stroke:#ffc107,stroke-width:2px
    classDef pres fill:#f8d7da,stroke:#dc3545,stroke-width:2px

    class Abs,TuiAbs domain
    class AppLayer,Core,Plugins app
    class Storage,Providers,Tools infra
    class Cli,TuiAnsi,TuiConsoleEx,TuiContrib pres
```

**Dependency direction = inward only.** Outer layers may reference inner layers;
inner layers never reference outer layers. The Domain layer has no inbound
arrows from Harbor projects — only outbound to BCL / third-party NuGet packages.


### Why two projects in the Domain layer?

`Harbor.Abstractions` is the contract surface for the agent harness (LLM, tools, sessions,
events, permissions). `Harbor.Tui.Abstractions` is the contract surface for the UI layer
(views, view models, renderers, panels, UI state). They are kept separate so that a
headless consumer (CLI script, MCP bridge, test harness) can reference just
`Harbor.Abstractions` without dragging in any UI vocabulary. Both projects are in the
Domain layer and may reference each other; in practice `Harbor.Tui.Abstractions` references
`Harbor.Abstractions` (for `IAgent`, `AgentEvent`, `Session`), never the reverse.

---

## 2. Allowed and forbidden project references

> **Единственный механический источник правды** для рёбер `<ProjectReference>` сегодня —
> `tests/Harbor.Architecture.Tests`, в первую очередь `FullLayerMatrixTests` (§5.4):
> data-table на каждый src-assembly главного решения (47/50 строк; вне области по
> документированным причинам: CodeGen build-tool, Plugins.Host exe, Providers.Shared
> linked-source). Таблица ниже — устоявшийся TL;DR, полезный как шпаргалка; при
> расхождении доверяйте тестам.

The matrix below is a **coarse-grain summary** of allowed `<ProjectReference>` edges.

| Project (row) → may reference (column)         | Domain (Abs. / Contracts / Tui.Abs.) | Application (Application/Core/Registries) | Plugins.* | Storage.* | Providers.* | Tools.Builtin | Tui.* (concrete) | Cli |
|------------------------------------------------|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **Harbor.Abstractions**                        | ❌ (—) | ❌  | ❌  | ❌  | ❌  | ❌  | ❌  | ❌  |
| **Harbor.Application / Core**                  | ✅  | Registries only | ❌  | ❌  | ❌  | ❌  | ❌* | ❌  |
| **Harbor.Plugins.{Runtime, Hosting, …}**       | ✅ (Runtime also Tui.Abstractions) | ❌  | —   | ❌  | ❌  | ❌  | ❌  | ❌  |
| **contrib/scripting Harbor.Scripting***        | ✅  | ❌  | ❌  | ❌  | ❌  | ❌  | ❌  | ❌  |
| **Harbor.Storage.Jsonl / Memory / Sqlite**     | ✅  | ❌  | ❌  | —   | ❌  | ❌  | ❌  | ❌  |
| **Harbor.Providers.OpenAiCompatible / .Anthropic / .OpenAI / .Ollama** | ✅  | ❌  | ❌  | ❌  | —   | ❌  | ❌  | ❌  |
| **Harbor.Tools.Builtin**                       | ✅  | ❌  | ❌  | ❌  | ❌  | —   | ❌  | ❌  |
| **Tui renderers (Ansi / Plain / ConsoleEx / Notifications; contrib Spectre-family и др.)** | ✅  | ❌  | ❌  | ❌  | ❌  | ❌  | ❌ (не друг друга) | ❌  |
| **apps/Harbor.App.Cli**                        | ✅  | ✅  | ✅  | ✅  | ✅  | ✅  | ✅  | —   |

`❌*` Application layer (Core/Application) may not depend on UI vocabulary
(`Harbor.Tui.Abstractions`) — Core is the agent-harness application layer and must not
know about UI vocabulary. If Core needs to expose a hook that the UI cares about,
declare the contract in `Harbor.Abstractions` (or `Harbor.Tui.Abstractions` if it is
UI-specific), and let the Composition Root (`HostBuilder`) wire the UI-side adapter in.
Within the Application layer, Core may reference only its siblings
`Harbor.Application` + `Harbor.Registries` plus Domain
(see rule `Core_ReferencesOnlyApplicationAndRegistriesAndAbstractions`).

`\*` contrib-проекты (`Harbor.Scripting.*`, `contrib/apps/*`) живут вне `Harbor.slnx`
и правятся в `contrib/Contrib.slnx`; правила направления зависимостей те же.

### Concrete impl types belong ONLY in the Composition Root

Concrete implementations of:

- `ILlmClient` (e.g. `AnthropicLlmClient`, `OpenAiCompatibleLlmClient`, `OllamaLlmClient`)
- `ISessionStore` (e.g. `JsonlSessionStore`, `SqliteSessionStore`, `MemorySessionStore`)
- `ITool` (e.g. `ReadTool`, `BashTool`, `WebFetchTool`)
- `ITuiRenderer` (e.g. `AnsiTuiRenderer`, ConsoleEx `ScreenSession`-based renderer)
- `IAgent` (e.g. `DefaultAgent`)
- `IAgentLoop` (e.g. `AgentLoop`)
- `IEventBus` (e.g. `InMemoryEventBus`)

…may be **constructed** (i.e. `new`'d) **only** inside
`apps/Harbor.App.Cli/Hosting/HostBuilder.cs`
(the Composition Root). `Program.cs` resolves them from DI by their interface; it must NOT
`new` them directly. The architecture tests in §5 do NOT enforce this rule mechanically
(it requires call-site analysis, not just assembly references) — code review enforces it.

---

## 3. Layer responsibilities

### Domain / Abstractions

- **Contains:** interfaces (`IAgent`, `IAgentLoop`, `ITool`, `ILlmClient`,
  `ISessionStore`, `IProviderRegistry`, `IToolRegistry`, `IAgentRegistry`,
  `IEventBus`, `IPermissionService`, `ICompactionService`, `ISystemPromptBuilder`),
  value objects (`SessionId`, `MessageId`, `ProviderId`, `ModelRef`, `ToolName`,
  `AgentName`), record models (`Session`, `Message`, `AgentEvent`, `LlmEvent`,
  `ToolResult`, `AgentDefinition`, `AgentState`), and pure rulesets
  (`PermissionRuleset`).
- **Forbidden:** any I/O (file, network, console), any DI registration, any
  Roslyn/Jint/HttpClient dependency, any `sealed class` with mutable state.
- **Allowed NuGet:** `CSharpFunctionalExtensions` (for `Result<T>`),
  `Microsoft.Extensions.Logging.Abstractions`, `MemoryPack`,
  `CommunityToolkit.HighPerformance`, `ZLinq`.

### Application

- **Contains:** use-case orchestration. `AgentLoop.RunAsync` is the canonical use case
  ("advance the conversation one full turn"). `CompactionService.CompactAsync`,
  `PermissionService.CheckAsync`, contrib `ScriptHost.EvaluateAsync`,
  `PluginHost.LoadAllAsync` are all use cases. Registries (`ToolRegistry`,
  `ProviderRegistry`, `AgentRegistry`) live in `Harbor.Registries`/`Harbor.Application`
  because they hold the in-memory state that use cases mutate.
- **Forbidden:** `HttpClient`, file I/O outside of well-defined adapter seams,
  `Console.Write*`.
- **Note:** Roslyn (CS-source plugin compilation) lives in its own project
  `Harbor.Plugins.Compilation` since the plugin-host decomposition — the old
  «Compilation subfolder inside Plugins.Runtime» layout is gone. `Harbor.Plugins.Runtime`
  owns only `CsPluginLoader` + compiled-result types; hosting graph lives in
  `Harbor.Plugins.Hosting`; source sources in `Harbor.Plugins.Storage`;
  instantiation/lifecycle in `Harbor.Plugins.Instantiation`; registration in
  `Harbor.Plugins.Registration`. The Architecture tests treat the whole
  `Harbor.Plugins.*` family as Application-layer projects for dependency-direction
  purposes.

### Infrastructure

- **Contains:** adapters — concrete implementations of `ILlmClient`, `ISessionStore`,
  `ITool`, etc. These projects translate between the outside world (HTTP, filesystem,
  subprocess, native interop) and the Domain contracts.
- **Forbidden:** references to the Application layer (`Harbor.Application`,
  `Harbor.Registries`) or to each other (`Harbor.Providers.Anthropic` must not
  reference `Harbor.Providers.OpenAI`).
- **Allowed NuGet:** anything I/O-related — `Microsoft.Data.Sqlite`,
  `Microsoft.Extensions.Http`, `Jint`, etc.

### Presentation

- **Contains:** entry points and UI renderers. `apps/Harbor.App.Cli/Program.cs` is the main
  CLI `Main` entry point (`apps/Harbor.App.Avalonia/Program.cs` for the desktop app).
  `Harbor.Tui.*` projects are renderers — each implements
  `ITuiRenderer` (or one of the more specific TUI interfaces from
  `Harbor.Terminal.Abstractions`).
- **Composition Root:** `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` is the only place that
  knows about concrete Infrastructure types. It wires them into the DI container by
  interface.
- **Forbidden (references):** Presentation projects must NOT reference each other (e.g.
  `Harbor.Tui.AnsiPlain` must not reference `Harbor.Tui.CellForge`). They may share the
  `Harbor.Terminal.Abstractions` contract surface.
- **Forbidden (capabilities):** a Presentation assembly must not exercise an I/O
  capability directly. The reference matrix cannot catch this — `System.IO.File`,
  `System.Diagnostics.Process` and `System.Net.Http` are BCL types, visible in no
  `<ProjectReference>`. Enforced mechanically by
  `tests/Harbor.Architecture.Tests/PresentationCapabilityRules.cs` (see §5.6); the
  capability-to-layer assignment comes from the Infrastructure clause at the top of this
  section ("subprocess, filesystem, native interop"):

  | Rule id | Forbids | Layer that owns it |
  |---|---|---|
  | `PRESENTATION-MUST-NOT-SPAWN-SUBPROCESSES` | `System.Diagnostics.Process*` | Infrastructure (`BashTool` / `McpProcessClient` in `Harbor.Tools.Builtin`); UI-chrome spawns go through a Domain contract and live in `Harbor.Application` (`ProcessGitQuery` #537, `ProcessNotificationRunner` #665) |
  | `PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-FILES` | `System.IO.File*` | Infrastructure (`Harbor.Storage.*`, config stores) |
  | `PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-DIRECTORIES` | `System.IO.Directory*` | Infrastructure (file-tree / theme scanning) |
  | `PRESENTATION-MUST-NOT-USE-THE-NETWORK` | `System.Net.Http*`, `Sockets`, `WebRequest`, … | Infrastructure (`Harbor.Providers.*`, `Harbor.Transport.Remote`) |
  | `PRESENTATION-MUST-NOT-LOAD-ASSEMBLIES-OR-EMIT-IL` | `System.Reflection.Emit`, `AssemblyLoadContext` | nowhere — banned outright (NativeAOT-readiness) |

  A shell-out from the UI is the sharp edge: because the class is not an `ITool`, the
  call never reaches `PermissionRuleset.Evaluate`, never appears in the tool-call
  transcript, and cannot be cancelled by the agent loop. Route it through an `ITool`.

  **Deliberately not capabilities:** `System.IO.Path` (pure string manipulation —
  Presentation formats paths for display), `Console.*` (the renderer's job), and P/Invoke
  to `kernel32`/`libc` for VT-mode handling (also renderer work, in
  `Harbor.Tui.CellForge.Engine`). `System.Environment.GetEnvironmentVariable` /
  `CurrentDirectory` / `GetFolderPath` are genuine configuration leaks but are used
  pervasively by renderers; they are tracked in #518 rather than baselined here.

---

## 4. Layering rules cheat-sheet

| Rule                                                                                              | Enforced by              |
|---------------------------------------------------------------------------------------------------|--------------------------|
| Domain projects reference no other Harbor project (allowed: Tui.Abstractions → Abstractions only). | Architecture tests       |
| Application projects (Application/Core/Registries/Plugins.*) reference Domain only — never Infrastructure, never Presentation. | Architecture tests       |
| Infrastructure projects (Storage.*, Providers.*, Tools.Builtin) reference Domain only — never Application, never each other, never Presentation. | Architecture tests       |
| Presentation projects (Tui.* renderers) reference Domain only — never Application, never Infrastructure, never each other. | Architecture tests       |
| Presentation projects exercise no I/O capability of their own: no subprocess, no `System.IO.File`/`Directory`, no network, no reflection emit (§3 table, §5.6). | Architecture tests (`PresentationCapabilityRules`) |
| A domain fact decided by the core is decided in ONE place, and the presentation layer reads it: no `SessionStatus`-returning method derives it from the transcript (#687), the `SessionStatus` label/brush table exists once (#663), the core classifies diagnostics (#674), money is priced in the core (#653). | Architecture tests (`SessionStatusSourceRule`, `SessionStatusTableRule`, `DiagnosticsClassificationRule`, `CostPricedInCoreRules`) |
| `apps/Harbor.App.Cli` references everything — it is the Composition Root.                         | (by convention)          |
| Concrete impl types (`AnthropicLlmClient`, `JsonlSessionStore`, …) are `new`'d only inside `HostBuilder.cs`. | Code review              |
| `Program.cs` resolves services by interface from DI; it does not `new` Infrastructure types.       | Code review              |
| New interfaces go in `Harbor.Abstractions` (or `Harbor.Tui.Abstractions` for UI-only contracts), never in the Application layer. | Code review              |
| New value objects / records go in `Harbor.Abstractions.Contracts` (namespace `Harbor.Abstractions.Models`), never in the Application layer.            | Code review              |

---

## 5. Architecture tests

`tests/Harbor.Architecture.Tests/Harbor.Architecture.Tests.csproj` contains the
mechanically-enforced rules. The test project references every Harbor project so it can
load each assembly via reflection and assert on `GetReferencedAssemblies()`.

The tests come in **six files** (`LayerDependencyTests`, `NetArchLayerRules`,
`AbstractionsSplitLayerRules`, `FullLayerMatrixTests`, `CellForgeGraphRules`,
`PresentationCapabilityRules`, `EnforcerIntegrityTests`) — the executed total
exceeds the method count because of parameterised cases:

### 5.1 Reflection-based — `LayerDependencyTests.cs`

Uses plain `System.Reflection` (`Assembly.GetReferencedAssemblies()`) — zero extra
dependencies, fast, trivially readable. This is the zero-dependency fallback so the
layering rules still run even if NetArchTest ever fails to restore.

The 11 rules cover:

1. `Abstractions_HasNoHarborProjectReferences`
2. `TuiAbstractions_ReferencesOnlyAbstractions`
3. `UiFrameworkState_ReferencesOnlyAbstractionsFamily`
4. `Application_ReferencesOnlyAbstractions`
5. `Registries_ReferencesOnlyAbstractions`
6. `PluginsRuntime_ReferencesOnlyAbstractions` (Runtime also allows Tui.Abstractions)
7. `Providers_ReferencesOnlyAbstractions`
8. `Storage_ReferencesOnlyAbstractions`
9. `ToolsBuiltin_ReferencesOnlyAbstractions`
10. `TuiRenderers_ReferencesOnlyAbstractionsAndTuiAbstractions`
11. `AllExpectedHarborAssembliesAreLoaded` (coverage guard — a new src project without a rule fails loudly)

> #451 removed the `Harbor.Core` facade and with it
> `Core_ReferencesOnlyApplicationAndRegistriesAndAbstractions`; the invariant it
> stood for is now enforced against both real owners (`Harbor.Application`,
> `Harbor.Registries`) above and, table-wide, by `FullLayerMatrixTests`.
>
> #450 removed the `Harbor.Scripting` entries from the shared forbidden lists:
> that name is a `contrib/` project *family*, never an assembly, so every
> constraint naming it could never fail. The whole-layer rules above already
> cover those assemblies.

### 5.2 NetArchTest-based — `NetArchLayerRules.cs`

Uses the [`NetArchTest.Rules`](https://github.com/BenMorris/NetArchTest) fluent DSL
(`Types.InAssembly(...).Should().NotHaveDependencyOn(...).GetResult()`). The same
invariants are expressed in the more declarative NetArchTest style; this acts as a
cross-check that survives both tool surfaces and serves as a copy-paste template for
contributors who already know NetArchTest.

The 21 NetArchTest rules mirror the reflection-based rules above with names like
`NetArch_<Project>_DoesNotDependOn_<ForbiddenLayer>`. The pattern is:

```csharp
[Test]
public async Task NetArch_Core_DoesNotDependOn_Infrastructure()
{
    var types = Types.InAssembly(typeof(AgentLoop).Assembly);
    var result = types
        .Should()
        .NotHaveDependencyOn("Harbor.Providers.OpenAiCompatible")
        .And().NotHaveDependencyOn("Harbor.Storage.Jsonl")
        // ...
        .GetResult();
    await Assert.That(result.IsSuccessful).IsTrue();
}
```

Additionally `AbstractionsSplitLayerRules.cs` pins the F1-decoupling invariants
(`Harbor.Abstractions.Contracts` ↔ `Harbor.Abstractions` split — ADR-007).

### 5.3 Running the tests

```bash
# Build only:
dotnet build tests/Harbor.Architecture.Tests/Harbor.Architecture.Tests.csproj

# Run the architecture tests:
dotnet run --project tests/Harbor.Architecture.Tests/Harbor.Architecture.Tests.csproj
```

If a test fails, the assertion message lists every forbidden assembly that the project
actually references. Fix the `<ProjectReference>` in the offending `.csproj`, then
re-run.

### 5.4 Full-project matrix — `FullLayerMatrixTests.cs` (ROP-D)

Before ROP-D only assemblies with a hand-written `typeof()` probe had layer rules
(~18 src projects). `FullLayerMatrixTests.cs` closes the gap with one data
table covering **every main-solution src assembly** (45 rows at the time; grown to
47/50 src dirs as of 2026-08-27 — see §2 banner):

1. *Reference check* — actual `Assembly.GetReferencedAssemblies()` ⊆ the row's
   Allowed set (+ documented exceptions). IL-level: transitive ProjectReferences
   that leak types into a consumer's AssemblyRef are caught too.
2. *Allowed-set liveness* — every entry a row permits must be a reference the
   assembly really has. Without this the Allowed side rots silently: a permitted
   edge nobody uses is a hole waiting for the next contributor.
3. *Table guard* — Allowed sets themselves must respect the layer classes
   (Presentation ↛ Infrastructure/Application, Infrastructure ↛ Presentation,
   Domain ↛ Domain-only), so a violation cannot be pre-declared as "allowed";
   real exceptions live in `DocumentedExceptions`, each with a reason.
4. *Exception liveness* — every documented exception must correspond to a real
   current reference (no rotting into blanket permissions).
5. *Exception scope* — a documented exception names the **individual source
   files** allowed to bind the target assembly, not the whole project. A new
   file reaching into the offending assembly fails, so an exception cannot
   absorb unrelated future violations.
6. *Coverage* — adding a src project without a matrix row (and a ProjectReference
   in the test csproj) fails loudly instead of silently skipping.

Out of scope by design: `Harbor.CodeGen` (source-generator project, consumed via
`OutputItemType=Analyzer`), `Harbor.Plugins.Host` (OutputType=Exe out-of-process
MCP server — an app), `Harbor.Providers.Shared` (a shared-source folder with no
csproj: its files are `<Compile Include>`-linked into the four provider
assemblies), and `apps/*` composition roots. Every one of these lives in
`OutOfScopeAssemblies` / `SharedSourceFolders` with a reason, and
`EnforcerIntegrityTests.SrcProjects_AreAllClassified` fails when a new `src/`
project is in neither list.

### 5.5 Rules about the rules — `EnforcerIntegrityTests.cs` (#450)

The checks above have a failure mode they cannot see themselves: a rule naming
something that does not exist, permitting more than intended, or covering nothing
is green forever. `EnforcerIntegrityTests.cs` closes that class:

| Rule | Guards against |
|---|---|
| `RepositoryInventory_IsDiscoverable` | csproj-walking rules silently passing when the repo root is not found |
| `RuleTargetAssemblyNames_AllExist` | **vacuous targets** — a typo, rename or deleted project left in a forbidden/allowed list |
| `SrcProjects_AreAllClassified` | an assembly in `src/` that is on no list at all (unbounded reach) |
| `Matrix_AllowedEntries_AreLive` | **allowed-set rot** — permitted edges nobody uses |
| `DeclaredProjectReferences_AreJustifiedByTheMatrix` | **csproj-level drift** — a `<ProjectReference>` that binds no type emits no IL, so every AssemblyRef-based rule is blind to it |
| `DeclaredButUnboundProjectReferences_AreReallyUnbound` | a "vestigial" exemption that is actually a live dependency in disguise |
| `DocumentedExceptions_AreScopedToNamedFiles` | **project-granular exceptions** hiding new violations |
| `DocumentedExceptions_AllHaveReasons` | reasonless, accidental exceptions |

The vacuity trap in particular: NetArchTest's `NotHaveDependencyOn("X")` and the
reflection `FindForbiddenReferences(asm, "X")` both treat a **non-existent
assembly name as a satisfied constraint**. `Harbor.Scripting` (a `contrib/`
project *family*, never an assembly) and `Harbor.Domain` (deleted in the F1
split) were named by 15 rule positions and enforced nothing.
`RuleTargetAssemblyNames_AllExist` resolves every name against the real project
tree, so that class of rot now fails.

Adding a `<ProjectReference>` that binds no type therefore needs an explicit
entry in `DeclaredButUnboundProjectReferences` with a reason — a deliberate act
a reviewer can see, instead of an invisible edge.

### 5.6 Skipping a test for a known violation

When a real violation is found and cannot be fixed in the current sprint, mark the
test with a `// TODO(arch): violation, see ARCHITECTURE_LAYERS.md §known-violations`
comment and skip it via `Assert.Skip(...)`. Do NOT leave a layering test red on
`main` — either fix the violation or skip the test.

```csharp
[Test]
public async Task NetArch_SomeRule()
{
    // TODO(arch): violation, see ARCHITECTURE_LAYERS.md §known-violations
    await Assert.Skip("Known violation — see ARCHITECTURE_LAYERS.md §known-violations");
    // ... rest of test stays for documentation ...
}
```

### 5.6 Capability rules — `PresentationCapabilityRules.cs` (#455)

The four files above enforce the **reference** half of the contract: which
`<ProjectReference>` edges may exist. That is not sufficient. `System.IO.File`,
`System.Diagnostics.Process` and `System.Net.Http` are BCL types — they appear in no
ProjectReference, so a perfectly layered assembly can still fork a shell or overwrite
the user's config. `PresentationCapabilityRules.cs` enforces the missing half.

**Mechanism.** Mono.Cecil (the copy `NetArchTest.Rules 1.3.2` already restores — no new
`PackageReference`) reads each Presentation assembly's metadata, walks every method body,
and collects every BCL type referenced from an IL operand or a type/field/method
signature. A rule is a list of type-name prefixes matched with `StartsWith`, so one entry
covers a family (`System.IO.File` also covers `FileStream`, `FileInfo`, `FileAccess`).
Because this is IL-level it catches a `File.ReadAllText` that no assembly-reference check
can see.

**Scope.** The Presentation set is not a second hand-maintained list — it is read from
`FullLayerMatrixTests.Matrix` via `PresentationLayerAssemblies()`, so a new Presentation
src project becomes capability-enforced the moment it gets a matrix row.

**Attribution.** `async` methods and lambdas live in compiler-generated nested types
(`<Foo>d__7`, `<>c__DisplayClass0_0`). The probe collapses those to the nearest type a
human wrote, so baseline keys survive edits and CI failures name a findable type.

**Baseline, not a skip.** Presentation performs I/O today, so the rules land *enforced*
against a `KnownViolations` table keyed by (assembly, rule id, declaring type) — the same
shape as `DocumentedExceptions` in §5.4. A rule holds only if its hits are a subset of the
baseline, so new I/O in any Presentation type is red on the spot. The baseline is
type-granular, not method-granular; see §6 for the inventory and the per-site issues.

**Permanent capabilities, in a second table.** A baseline row is a promise *to fix*: it
names the issue that will delete it, and §6 counts rows deleted. Some capabilities are
real, legitimate and unfixable — the console device a renderer reads stdin from is the
canonical one. Filing those as violations misfiles a permission as debt: it reads as
precedent ("renderers may touch the filesystem"), and it makes the owning issue's
checkbox unreachable, since the row can be neither deleted nor kept in the right place.
Such capabilities go in `PermanentCapabilities`, keyed identically but valued by their
**reason** rather than a tracking issue. The reason is mandatory and the table gets the
baseline's anti-rot liveness test, so it is a decision on the record, not a hole
(#669).

**Non-vacuity.** A rule nobody can fail is a comment, and NetArchTest's
`NotHaveDependencyOn` has a failure mode here: an unmatched name is a satisfied
constraint, so a typo'd or deleted assembly passes forever. Five tests close that door:

1. `NonVacuity_Probe_ReadsRealIlFromThisTestAssembly` — probes this test assembly, whose
   source is in the repo and is known to call `Directory.GetFiles` and `File.Exists`; both
   rules must fire, and the assembly file must exist. "No violations" can never mean "no
   input".
2. `NonVacuity_Probe_CanStillDetectForbiddenCapabilities` — the sensitivity control: the
   same probe, rules and matchers run against a *real* positive control,
   `Harbor.Tools.Builtin` (ReadTool/EditTool/GlobTool touch the filesystem,
   BashTool/McpProcessClient fork processes), and MUST report hits. If the probe ever
   degrades to "nothing anywhere", the Presentation rules go red instead of vacuously
   green.
3. `NonVacuity_GrandfatheredViolations_AreStillReal` — every baseline row must still match
   a real hit, so the list cannot rot into a blanket permission (the
   `DocumentedExceptions_AllCurrentlyRealized` pattern).
4. `PermanentCapabilities_AreAllCurrentlyRealized` — the same guarantee for the
   permanent-capability table, so the second table gets no weaker a deal than the first.
5. `RuleTable_And_Baseline_Are_WellFormed` — rule ids unique and non-blank, every rule
   states what it forbids and why, every baseline row names an existing rule, points at a
   tracking issue, and names an assembly the matrix really classifies as Presentation (a
   typo there would grandf nothing). For the permanent table: same, minus the issue URL,
   plus a mandatory reason and no row may appear in both tables at once.

`PRESENTATION-MUST-NOT-USE-THE-NETWORK` and
`PRESENTATION-MUST-NOT-LOAD-ASSEMBLIES-OR-EMIT-IL` have **empty** baselines: Presentation
is clean on both today, so they run fully armed rather than being deferred with the rest.

### 5.7 Single-source rules — a fact decided once, in the core

The §5.6 rules ask *which capabilities* Presentation may use. These ask a different
question: **is a domain fact decided in one place, or does every consumer reach its
own verdict?** A second verdict is not a style difference — it disagrees with the first,
and nothing in the type system objects.

| Rule | Fact | Issue |
|---|---|---|
| `SessionStatusSourceRule` | a `SessionStatus` is decided on the transition that establishes it (`ChatAppReducer`, from the core's own `AgentErrorEvent` / `AgentEndEvent`), and no method returning one may read the transcript | [#687](https://github.com/refusedguy/Harbor-Harness/issues/687) |
| `SessionStatusTableRule` | the `SessionStatus` → label / brush-key table exists in exactly one file (`StatusMappers`) | [#663](https://github.com/refusedguy/Harbor-Harness/issues/663) |
| `DiagnosticsClassificationRule` | the detector patterns are declared once, in the core detector, and the LSP counts stay connected to state | [#674](https://github.com/refusedguy/Harbor-Harness/issues/674) |
| `CostPricedInCoreRules` | money is priced by the core from the model that made the call; no renderer recomputes it | [#653](https://github.com/refusedguy/Harbor-Harness/issues/653) |

**Mechanism.** Repository text scans over `src/` + `apps/` (`contrib/` is outside CI and
outside support by owner decision, so it is neither scanned nor expected clean). These
are rules about a table NOT existing in a second file and about a method's body, neither
of which survives into metadata — a compiled check could not be landed before the fix it
guards, and the whole point of writing the guard first is that it is red against the
defect. Comments are stripped first (`SourceCommentStripper`, shared across these rules)
so a file's own doc comment quoting the rule it violates is not graded as code.

**Perimeters are derived, not re-typed.** `SessionStatusSourceRule` reads its Presentation
set from `FullLayerMatrixTests.PresentationLayerAssemblies()` for the same reason §5.6
does, so a new Presentation project is covered the moment it gets a matrix row.

**Non-vacuity.** Each rule carries a liveness check (the scan really walked a checkout,
the canonical file really exists, the rule table is non-empty) and a positive control that
feeds the probe a synthetic violation and REQUIRES a hit, alongside snippets it must not
report. A probe whose matchers stopped working reports nothing, and the rule goes green
while enforcing nothing — which is the failure mode these rules exist to prevent.

---

## 6. Known violations

> This section lists every layering violation that exists in the codebase at the time
> of writing and is intentionally NOT fixed yet. Each entry has a `TODO(arch)` owner
> and a planned fix sprint. **Do not add a new violation without adding an entry
> here.**

**As of 2026-09-29 (issue #455) the REFERENCE matrix has zero known violations** —
`LayerDependencyTests` + `NetArchLayerRules` + `AbstractionsSplitLayerRules` +
`FullLayerMatrixTests` + `CellForgeGraphRules` are all green. The previously cited counts
(46 tests = 21 reflection + 25 NetArchTest, 54 executed cases) are historical.

**The CAPABILITY rules added in #455 have 7 known violations, in 4 types across 3 of the
18 Presentation assemblies.** They are not skipped: each is a row in
`PresentationCapabilityRules.KnownViolations` that a rule holds only while it is still
exactly reproduced, and each carries a tracking issue. Fifteen Presentation assemblies
are clean and fully enforced.

Seven rows left that no longer count, in three issues. #536: the four
`Harbor.DesignSystem` rows — `ThemeStore` and `ThemeDirectoryWatcher` — are gone, and
the `["Harbor.DesignSystem"]` entry with them, because an entry with no rows is still a
claim that the assembly is dirty. That assembly is the HDS v1 package: `IsPackable`,
`PackageId: Harbor.DesignSystem`, an empty allowed-reference set, no `PackageReference`.
It is the one assembly a consumer can take without pulling Harbor in, and the two types
were the only thing in it that read a disk or the user's home directory. The persistence
half moved to `Harbor.Hosting.Themes`; the `IThemeStore` port and every token stayed in
the leaf, so the contract and the bytes are now in different assemblies on purpose.
`DesignSystemLeafTakesNoIoRules` is the guard, and it covers the two `Environment.Get*`
reads that no capability rule could see. #668: the terminal `JsonThemeLoader` and
`ThemeFileWatcher` were not a filesystem permission but a second implementation of theme
loading beside `ThemeStore`. They read through `IThemeStore` now, and
`ThemeStoreSeamRules` fails if a second implementation of that port appears. #667: the two
`CellForgeFileTreePanel` rows — the file tree, described below. All three deletions are
forced rather than asserted: the liveness test fails the build on a row that outlived
its violation, and the resolved-list test fails it if the capability returns.

A further capability is recorded in `PermanentCapabilities` — not a violation, so not
counted above. See "Permanent capabilities" in §5.6.

> These counts come from `KnownViolations`; the table below is the source of truth for
> the per-rule split. The prose had drifted from it three times — "21 … 14 types across
> 7" when the table held 13 across 6, then "18 … 12 types" when it held 13 across 6
> again, then "13 … 7 types across 4" when the deletions of #537, #665, #667 and #668
> had left it holding 11 across 6 — so it now says where the numbers come from rather
> than only what they are. #536 recomputed both halves from the table.

| Rule | Violating types | Assemblies | Tracking issues |
|---|---:|---:|---|
| `PRESENTATION-MUST-NOT-SPAWN-SUBPROCESSES` | 1 | 1 | [#538](https://github.com/refusedguy/Harbor-Harness/issues/538) (jump palette) |
| `PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-FILES` | 3 | 3 | [#534](https://github.com/refusedguy/Harbor-Harness/issues/534) (config stores), [#535](https://github.com/refusedguy/Harbor-Harness/issues/535) (recent items) |
| `PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-DIRECTORIES` | 3 | 2 | [#534](https://github.com/refusedguy/Harbor-Harness/issues/534) (config stores), [#535](https://github.com/refusedguy/Harbor-Harness/issues/535) (recent items) |
| `PRESENTATION-MUST-NOT-USE-THE-NETWORK` | 0 | 0 | — clean, unbaselined |
| `PRESENTATION-MUST-NOT-LOAD-ASSEMBLIES-OR-EMIT-IL` | 0 | 0 | — clean, unbaselined |

| Permanent capability | Assembly | Reason |
|---|---|---|
| `PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-FILES` / `TerminalInputStream` | `Harbor.Tui.CellForge.Engine` | Console device, not storage: fd 0 is the renderer's input medium (#669) |

### ARCH-5 template — new capability violations

The reference-layer template below applies to the capability rules too, keyed by
(assembly, rule id, declaring type). **Do not delete a baseline row when a rule goes
red** — that is the one move that turns an enforced rule back into a comment. Either fix
the I/O (move it behind a Domain contract + an Infrastructure implementation) or, if it
genuinely must stay, add a row with a real issue URL.

### ARCH-5 completed — the file tree (#667)

`CellForgeFileTreePanel` was the clearest example of why a rule needs a *design* and not
just a moved call. It listed the working directory with `Directory.EnumerateDirectories`
/ `EnumerateFiles` and read `FileAttributes`, from inside `Build` — inside a painted
frame. Deleting those three calls was not possible on its own, because the listing lived
in a private field and **a render-thread cache can only be filled by the render thread**.
Three layers had to land together:

| Layer | Before | After |
|---|---|---|
| State | `_entries` + `_entriesDir` fields, lock-guarded | `UiState.Ui.FileTrees` → `FileTreeSnapshot`, reducer-written |
| Seam | none — the call was inline | Domain `IDirectoryLister`, implemented by `SystemDirectoryLister` (Application) |
| Cancellation | none | `FileTreeLoader` owns a per-panel `CancellationTokenSource` |

The third layer is what makes the second one safe rather than merely relocated: a walk
is bounded (entry cap), cancellable, and superseded — navigating away cancels the walk
in flight, and a result that arrives for a directory the panel has left is dropped by
the reducer rather than painted. The panel now performs **no** `System.IO` at all;
`Path.GetDirectoryName` replaces `Directory.GetParent` for the `h` key, which is pure
string handling and explicitly not forbidden.

Two baseline rows are gone, and both deletions are enforced in both directions:
`NonVacuity_GrandfatheredViolations_AreStillReal` fails the build on a row that outlived
its violation, and `ResolvedViolations_HaveNoHits` fails it if the capability returns.
The second is the one worth copying for the remaining #538 sites — a removed violation
with nothing guarding its removal is indistinguishable from one that was never paid for.

### Previously suspected (not a violation)

The previously suspected violation — *"Harbor.Tui.Abstractions references the
agent-harness assembly via `IAgent`"* — does **not** exist: `IAgent` lives in
`Harbor.Abstractions/Agents/IAgent.cs` (Domain), not in `Harbor.Application` or
`Harbor.Registries`. The
`Harbor.Tui.Abstractions.csproj` file references only `Harbor.Abstractions`:

```xml
<ItemGroup>
  <ProjectReference Include="..\Harbor.Abstractions\Harbor.Abstractions.csproj"/>
</ItemGroup>
```

### Template for future violations

When (not if) a new violation slips in, add an entry in the format below so the
audit history is preserved:

```markdown
### ARCH-NNN: <project> illegally references <forbidden-project>

- **Detected by:** `NetArch_<Project>_DoesNotDependOn_<ForbiddenLayer>` (or
  the reflection-based equivalent).
- **Symptom:** the test fails with `<forbidden-project>` listed in the
  forbidden-references set.
- **Root cause:** <one-paragraph explanation>.
- **Planned fix:** <one-paragraph description of the design change that
  removes the dependency — usually "extract the shared type into Domain" or
  "introduce a new interface in Domain and inject it via the Composition Root">.
- **Owner / sprint:** TODO(arch) — Sprint N.
- **Tracking test skip:** <test name> is skipped via `Assert.Skip(...)` with a
  `// TODO(arch): violation, see ARCHITECTURE_LAYERS.md §known-violations`
  comment until the fix lands.
```

---


## 7. How to add a new project

1. **Pick the layer.** Decide which of the four layers the new project belongs to
   using §3 as a guide. If it implements an interface from `Harbor.Abstractions`,
   it's almost certainly Infrastructure.
2. **Add `<ProjectReference>` entries** that respect the matrix in §2. If you need
   a reference that is marked ❌, you have a layering violation — fix the design
   first.
3. **Update `Harbor.Architecture.Tests`** if the new project should be covered by
   an existing rule (e.g. a new `Harbor.Providers.XYZ` provider is automatically
   covered by `Providers_ReferencesOnlyAbstractions` because the test enumerates
   all loaded assemblies whose name starts with `Harbor.Providers.`).
4. **Update `Harbor.slnx`** to include the new project.
5. **Update this document** if the new project creates a new sub-category that
   deserves its own row in the §2 matrix.

### Worked example — adding `Harbor.Providers.Bedrock`

```xml
<!-- Harbor.Providers.Bedrock.csproj -->
<ItemGroup>
  <ProjectReference Include="..\Harbor.Abstractions\Harbor.Abstractions.csproj"/>
  <!-- ❌ DO NOT add Harbor.Application / Harbor.Registries here — Bedrock is Infrastructure, they are Application. -->
</ItemGroup>
<ItemGroup>
  <PackageReference Include="AWSSDK.BedrockRuntime" Version="..."/>
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0"/>
</ItemGroup>
```

Then in `HostBuilder.cs`:

```csharp
pb.AddProvider("bedrock", () => new BedrockLlmClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("bedrock"),
    new BedrockConfig(),
    loggerFactory.CreateLogger<BedrockLlmClient>()));
```

The new project is automatically picked up by the
`Providers_ReferencesOnlyAbstractions` architecture test.

---

## 8. Audit history

See [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) §ARCH-001..§ARCH-NNN for the
full audit trail of layering violations found and fixed when this document was
introduced.
