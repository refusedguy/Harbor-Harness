# Shell Recon — Specs vs Code

Reconnaissance report for Harbor UI shell architecture. Analyzes `specs/07-tui.md`,
`docs/DESKTOP_APP_PLAN.md`, `docs/ALTERNATIVE_UIS.md`, `docs/SPECTRE_TUI_DEEP_DIVE.md`,
`docs/ARCHITECTURE.md`, and `docs/ARCHITECTURE_LAYERS.md` against the actual codebase.

> **Note:** snapshot generated 2026-08-12, before sprint-2. `Harbor.Tui.{Spectre,Spectre.Fullscreen,SpectreTui,TerminalGui,Termina,RazorConsole,Sixel}` now live in `contrib/tui/`, `Harbor.App.{Wpf,Maui,Blazor}` in `contrib/apps/`, their tests in `contrib/tests/`. Paths below are not updated.

## Specs inventory

| Document | Path | Relevance |
|---|---|---|
| 07 — TUI (terminal UI) | `specs/07-tui.md` | Describes terminal-only TUI with custom ANSI, Elm-style loop, `TuiState` |
| Desktop App Plan | `docs/DESKTOP_APP_PLAN.md` | Master plan for 4 desktop GUIs with `Harbor.Desktop.*` shared libs |
| Alternative UIs | `docs/ALTERNATIVE_UIS.md` | Catalog of all renderers, claims shared `UiStore`/`UiReducer` for all |
| Spectre TUI Deep Dive | `docs/SPECTRE_TUI_DEEP_DIVE.md` | Accurate SpectreTUI internals, panel system, TEA compliance |
| Architecture | `docs/ARCHITECTURE.md` | High-level design, still references `Harbor.Core` as monolith |
| Architecture Layers | `docs/ARCHITECTURE_LAYERS.md` | Canonical layering matrix, mentions `Harbor.Ui.Framework` but rules are stale |

## Claims vs reality table

### State shape: `TuiState` vs `UiState`

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| Central state record is `TuiState` with fields `InputBuffer`, `StreamingText`, `IsBusy`, etc. | `specs/07-tui.md:116-139` | `src/Harbor.Ui.Framework/State/UiState.cs:65` | **OUTDATED** | Actual state is `UiState` with `Lines`, `Active`, `ScrollOffset`, `PanelStates`, `Cost`, etc. `TuiState` name never existed in code. |
| State is immutable record with `with` expressions | `specs/07-tui.md:116` | `src/Harbor.Ui.Framework/State/UiState.cs:65` | **TRUE** | `UiState` is `sealed record` with init-only properties. |
| Desktop shell has separate `ShellState` for drawer/overlay flags | Not in terminal specs | `src/Harbor.Ui.Framework/State/ShellState.cs:21`, `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs:233` | **PARTIAL** | `ShellState` exists as `ObservableObject` for Avalonia overlay flags. Not mentioned in any spec. |

### Reducer pattern

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| `TuiState Update(TuiState state, object msg)` — switch on message type | `specs/07-tui.md:61-73` | `src/Harbor.Ui.Framework/State/UiReducer.cs` | **OUTDATED** | Actual reducer is `UiReducer.Update(state, UiMsg)` — typed discriminated union, not `object`. `UiReducer.Reduce(state, AgentEvent)` also exists for agent events. |
| Reducer returns `(UiState, TuiEffect)` | `specs/07-tui.md` (implied) | `src/Harbor.Ui.Framework/State/UiReducer.cs` | **TRUE** | `UiReducer.Update` returns `(UiState, TuiEffect)`. |
| TEA compliance: single source of truth in state | `docs/SPECTRE_TUI_DEEP_DIVE.md:921-963` | `src/Harbor.Ui.Framework/State/UiState.cs` | **TRUE** | SpectreTUI was restored to TEA compliance. Desktop shell uses `StoreSubscriberViewModel` which reads `UiStore.Changed`. |

### Panel system

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| `PanelRegistry` is registration-only (no `SetState`/`ApplySnapshot`) | `docs/SPECTRE_TUI_DEEP_DIVE.md:794-817` | `src/Harbor.Tui.Abstractions/Panels/PanelRegistry.cs` | **TRUE** | `PanelRegistry` holds only `IPanelProvider` instances. State lives in `UiState.PanelStates`. |
| Panel state in `UiState`: `PanelStates`, `PanelSizes`, `FocusedPanelId` | `docs/SPECTRE_TUI_DEEP_DIVE.md:804-812` | `src/Harbor.Ui.Framework/State/UiState.cs` | **TRUE** | All panel state fields present. |
| Desktop shell uses `IOverlayStack` for modals (palette, settings, diff, etc.) | Not in terminal specs | `src/Harbor.Ui.Framework/Services/IOverlayStack.cs:10`, `src/Harbor.Ui.Framework/Overlays/OverlayController.cs:12` | **PARTIAL** | Avalonia has full overlay system with `OverlayController` + `IOverlayStack`. WPF uses `DialogService` instead. Not documented in any spec. |

### Keyboard handling

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| Terminal: `KeyPressEvent` with `ConsoleKey` + `char` + `ConsoleModifiers` | `specs/07-tui.md:803` | N/A | **OUTDATED** | Actual code uses `UiMsg.KeyInput(ChatAction, UiKey)` in SpectreTUI and `KeyMessage` in Spectre.Tui. `ConsoleKey` not used. |
| Desktop: Ctrl+P, Ctrl+Shift+P, Ctrl+B, Ctrl+Shift+T wired in `MainWindow.axaml.cs` | `docs/DESKTOP_APP_PLAN.md:24` | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs:23` | **TRUE** | Keyboard shortcuts wired in `MainWindow.axaml.cs` and dispatch to `MainViewModel` commands. |
| Desktop: `KeyboardShortcutService` handles Escape via `IOverlayStack` | Not in specs | `apps/Harbor.App.Avalonia/Services/KeyboardShortcutService.cs:17` | **PARTIAL** | Exists but not documented. |

### Overlay system

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| Overlays pushed onto `IOverlayStack`; Escape pops top | `docs/ALTERNATIVE_UIS.md` (implied) | `src/Harbor.Ui.Framework/Services/IOverlayStack.cs:10-33` | **TRUE** | `OverlayStackService` is LIFO stack of string ids. |
| `OverlayController` maps overlay id → boolean property on MainViewModel | Not in specs | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs:35-51` | **PARTIAL** | `OverlayController` exists with `Register(id, setter)` pattern. Not in any spec. |
| Overlay close resolves flag through table instead of string switch | Not in specs | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs:28-44` | **PARTIAL** | `OverlayIdToFlagProperty` dictionary maps id → property name. Not documented. |

### Content host / page navigation

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| `IContentHost<T>` generic interface | `docs/DESKTOP_APP_PLAN.md:37` (implied) | `src/Harbor.Ui.Framework/Navigation/IContentHost.cs:10` | **FALSE** | Actual `IContentHost` is **non-generic**: `object? ActiveView { get; }`, `void NavigateTo(string route)`. No generic version exists. |
| `AvaloniaContentHost` aggregates shell VMs and exposes `ActiveView` | `docs/DESKTOP_APP_PLAN.md:37` | `apps/Harbor.App.Avalonia/Navigation/AvaloniaContentHost.cs:19-74` | **TRUE** | Aggregates Chat, Sessions, CodeEditor, Diff, TokenUsage, etc. |
| `ContentHost` creates Visual from ViewModel | `docs/DESKTOP_APP_PLAN.md:37` (implied) | `apps/Harbor.App.Avalonia/Navigation/AvaloniaContentHost.cs` | **FALSE** | `AvaloniaContentHost` exposes ViewModels, not Visuals. Views are created by XAML `DataTemplate` bindings in `MainWindow.axaml`. |
| `ViewModelLocator` resolves VMs from `IServiceProvider` | Not in specs | `src/Harbor.Desktop.Shared/Locators/ViewModelLocator.cs:21` | **PARTIAL** | Exists and is used, but not mentioned in any spec. |

### View model lifecycle (Transient vs Singleton)

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| "Page VM = Transient" — edit VMs are transient | `docs/DESKTOP_APP_PLAN.md:37` (implied) | `apps/Harbor.App.Avalonia/Hosting/ViewModelRegistration.cs:51-95` | **PARTIAL** | Avalonia: shell VMs are Singleton, edit-style VMs (CodeEditor, Diff) are also Singleton, TokenUsage is Singleton. Only `OnboardingViewModel` is Transient. WPF: ALL VMs are Transient (including MainViewModel). |
| WPF VMs are transient "so each window gets a fresh state" | `apps/Harbor.App.Wpf/App.xaml.cs:252` | `apps/Harbor.App.Wpf/App.xaml.cs:252-263` | **TRUE** | WPF registers all VMs as Transient. Avalonia fixed the transient MainViewModel bug (DeepSeek-flagged). |
| Transient MainViewModel caused DI cycle bug | `apps/Harbor.App.Avalonia/Hosting/ViewModelRegistration.cs:9-16` | `apps/Harbor.App.Avalonia/Hosting/ViewModelRegistration.cs:9-16` | **TRUE** | Documented in code comments. Fixed by making MainViewModel Singleton + `Lazy<CommandPaletteViewModel>`. |

### ViewLocator patterns

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| `IViewModelLocator` for centralized VM resolution | Not in specs | `src/Harbor.Desktop.Shared/Locators/IViewModelLocator.cs:3` | **PARTIAL** | Exists and is registered via `AddViewModelLocator()`. Not mentioned in any spec. |
| Views resolved via XAML `DataTemplate` | Not in specs | `apps/Harbor.App.Avalonia/MainWindow.axaml` | **PARTIAL** | Avalonia uses XAML `DataTemplate` for view resolution. Not in specs. |

### One UiStore vs separate ShellStore

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| Single `UiStore` for all UI state | `docs/ALTERNATIVE_UIS.md:107-112` | `src/Harbor.Ui.Framework/State/UiStore.cs` | **TRUE** | Single `UiStore` with immutable `UiState`. |
| `ShellState` separate from `UiState` for overlay/drawer flags | Not in specs | `src/Harbor.Ui.Framework/State/ShellState.cs:21` | **PARTIAL** | `ShellState` exists as `ObservableObject` (mutable) for Avalonia overlay flags. Not documented in any spec. |

### IEventBus instead of store for shell

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| Desktop shell uses `IEventBus` directly | `docs/ALTERNATIVE_UIS.md:107` (implied decoupling) | `apps/Harbor.App.Avalonia/AppHost.cs:138-159` | **TRUE** | Avalonia `AppHost.BuildAsync` subscribes to `IEventBus` and routes events to per-session `UiStore`. |
| Renderers never touch `IAgent` directly | `docs/ALTERNATIVE_UIS.md:151-152` | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs` | **PARTIAL** | Avalonia shell VMs subscribe to `UiStore.Changed` (not `IEventBus` directly). But `AppHost` bridges `IEventBus` → `UiStore`. |
| "TUI is reference, don't touch" for desktop | `docs/DESKTOP_APP_PLAN.md:67` | `apps/Harbor.App.Avalonia/` | **FALSE** | Desktop apps have their own shell VMs (`MainViewModel`, `ChatViewModel`, etc.) in `apps/`, not in `src/Harbor.Tui.*`. The "reference" claim is misleading — desktop has diverged significantly. |

### Constructor size

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| "Constructor already 3 parameters" | Critique reference | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs:154-165` | **FALSE** | Avalonia `MainViewModel` constructor has **11 parameters**: `AvaloniaContentHost`, `Lazy<CommandPaletteViewModel>`, `ILogger<MainViewModel>`, `TuiEffectHost`, `IDispatcherAdapter`, `IThemeService`, `IToastService`, `ShellStatus`, `OverlayController`, `CostAnimator`, `IOverlayStack?`. |
| WPF `MainViewModel` has 6 parameters | `docs/DESKTOP_APP_PLAN.md:52-61` | `apps/Harbor.App.Wpf/ViewModels/MainViewModel.cs:52-60` | **TRUE** | WPF `MainViewModel` has exactly 6: `ThemeService`, `DialogService`, `ChatViewModel`, `SessionListViewModel`, `TokenUsageViewModel`, `CodeEditorViewModel`, `DiffViewModel`, `ToastNotificationViewModel` (actually 8). |

### `Factory.Create(IServiceProvider)`

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| `Factory.Create(IServiceProvider)` pattern exists | Critique reference | `src/Harbor.Desktop.Shared/Locators/ViewModelLocator.cs:29` | **FALSE** | No `Factory.Create` method exists. The actual pattern is `IViewModelLocator` / `ViewModelLocator` which wraps `IServiceProvider` and caches compiled `Func<IServiceProvider, object?>` delegates. |

### Generic `IContentHost<T>`

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| `IContentHost<T>` generic interface | Critique reference | `src/Harbor.Ui.Framework/Navigation/IContentHost.cs:10` | **FALSE** | `IContentHost` is non-generic. `ActiveView` returns `object?`. `NavigateTo(string route)` uses string routes. |

### ContentHost creates Visual

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| `AvaloniaContentHost` creates Visual | Critique reference | `apps/Harbor.App.Avalonia/Navigation/AvaloniaContentHost.cs:19-74` | **FALSE** | `AvaloniaContentHost` exposes ViewModels as properties. Views are created by XAML `DataTemplate` bindings in `MainWindow.axaml`. |

### TUI parity plans

| Claim | Source | Where in code | Verdict | Notes |
|---|---|---|---|---|
| "TUI is reference, don't touch" — desktop-only plan | `docs/DESKTOP_APP_PLAN.md:67` | `specs/07-tui.md`, `src/Harbor.Tui.SpectreTui/` | **FALSE** | Terminal TUI (SpectreTUI) is actively developed with panel system, TEA compliance, TuiEffectHost, etc. It is NOT a "reference only" — it's the primary interactive renderer. Desktop apps are separate (`Harbor.App.Avalonia`, `Harbor.App.Wpf`). |

## Contradictions between specs

### 07-tui.md vs DESKTOP_APP_PLAN.md

1. **Architecture philosophy**: `07-tui.md` describes a single-threaded Elm-style render loop with `TuiState` and `Channel<object>` event queue. `DESKTOP_APP_PLAN.md` describes MVVM with `Harbor.Desktop.Shared` ViewModels, `IContentHost`, and platform-specific XAML views. These are fundamentally different architectures — the terminal TUI has evolved into TEA (`UiStore`/`UiReducer`), while the desktop plan still assumes a pre-TEA world.

2. **State management**: `07-tui.md` (now outdated) shows `TuiState` as a single record. The actual code uses `UiState` (TEA) for terminal and `ShellState` (mutable `ObservableObject`) for desktop overlays. The desktop plan doesn't mention `ShellState` at all.

3. **Project structure**: `07-tui.md` assumes `Harbor.Tui/` namespace. `DESKTOP_APP_PLAN.md` assumes `Harbor.Desktop.*` projects. Actual code uses `Harbor.Ui.Framework` for shared UI infrastructure, `Harbor.Desktop.Abstractions` for desktop contracts, and `Harbor.App.Avalonia`/`Harbor.App.Wpf` for apps.

### DESKTOP_APP_PLAN.md vs ALTERNATIVE_UIS.md

1. **Renderer registration**: `DESKTOP_APP_PLAN.md` says desktop apps are "standalone" with their own `Main()` and `Program.cs`. `ALTERNATIVE_UIS.md` claims desktop GUIs are registered as `ITuiRenderer` implementations under `HARBOR_TUI=avalonia/wpf/maui/blazor`. Actual code: Avalonia and WPF have their own entry points (`Program.Main`, `App.OnFrameworkInitializationCompleted`) and do NOT register as `ITuiRenderer`. They use `AppHost.BuildAsync` instead.

2. **Namespace naming**: `ALTERNATIVE_UIS.md` refers to `Harbor.Tui.Wpf`, `Harbor.Tui.Avalonia`, etc. Actual code uses `Harbor.App.Wpf`, `Harbor.App.Avalonia`.

3. **EventBus decoupling**: `ALTERNATIVE_UIS.md` claims "renderers never touch `Harbor.Core`" and "all agent activity flows in through `AgentEvent`". Actual Avalonia code subscribes to `IEventBus` in `AppHost.BuildAsync` and routes to `UiStore`. The desktop shell does touch Core services (`IAgent`, `ISessionStore`, `IProviderRegistry`) directly via DI.

### ALTERNATIVE_UIS.md vs ARCHITECTURE_LAYERS.md

1. **Layering rules**: `ALTERNATIVE_UIS.md` says desktop renderers reference only `Harbor.Abstractions` + `Harbor.Tui.Abstractions`. `ARCHITECTURE_LAYERS.md` matrix says the same. But actual Avalonia/WPF projects also reference `Harbor.Ui.Framework` and `Harbor.Desktop.Abstractions` — these references are not accounted for in the matrix.

2. **`Harbor.Terminal.Abstractions` vs `Harbor.Tui.Abstractions`**: `ARCHITECTURE_LAYERS.md` consistently uses `Harbor.Terminal.Abstractions` as the TUI contract project. Actual code uses `Harbor.Tui.Abstractions`. This is a naming inconsistency in the docs.

### SPECTRE_TUI_DEEP_DIVE.md vs DESKTOP_APP_PLAN.md

1. **Panel system scope**: `SPECTRE_TUI_DEEP_DIVE.md` describes the panel system as SpectreTUI-specific (`IPanelProvider`, `PanelRegistry`, `PanelLayoutShell`). `DESKTOP_APP_PLAN.md` describes a different panel system for desktop (`ITuiPanelPlugin`, `Harbor.Desktop.Shared` panels). These are two separate panel architectures — the docs don't acknowledge this divergence.

## Outdated specs

### specs/07-tui.md (entirely outdated)
- `TuiState` → `UiState`
- Custom ANSI `TuiApp` with `Channel<object>` → `UiStore` + `UiReducer` + `IEventBus`
- `ConsoleAppFramework` for CLI → actual CLI uses `ConsoleAppFramework` but architecture is different
- `TuiRenderer` base class → actual code has `ITuiRenderer` + `BaseTuiRenderer`
- Slash command interface (`ISlashCommand`, `ICommandContext`) → actual code uses `SlashCommandDispatcher` with DI
- No mention of `UiStore`, `UiReducer`, `UiMsg`, `StoreSubscriberViewModel`, `TuiEffectHost`

### docs/DESKTOP_APP_PLAN.md (outdated project structure)
- `Harbor.Desktop.Shared` → doesn't exist; actual code uses `src/Harbor.Ui.Framework/` + `src/Harbor.Desktop.Shared/` doesn't exist
- `Harbor.Desktop.Abstractions` → exists but with different interfaces (`IViewModelLocator`, `IOverlayStack`, etc.)
- `Harbor.Desktop.DesignSystem` → doesn't exist; themes are in `apps/Harbor.App.Avalonia/Themes/`
- `Harbor.Desktop.Animations` → doesn't exist; animations are in `src/Harbor.Ui.Framework/Animation/`
- `Harbor.Desktop.CodeEditor` → doesn't exist; editor adapters are in each app project
- `MainViewModelBase` in `Harbor.Desktop.Abstractions` → exists but WPF does NOT use it (WPF has its own `ObservableObject`-based `MainViewModel`)
- Claims Avalonia and WPF share `MainViewModelBase` → only Avalonia uses it

### docs/ARCHITECTURE.md (outdated layer names)
- Still shows `Harbor.Core` as the Application layer containing `AgentLoop`, `ToolRegistry`, etc.
- Actual code: `Harbor.Core` is a thin facade; `AgentLoop` lives in `Harbor.Application.dll`; `ToolRegistry` lives in `Harbor.Registries.dll`
- Doesn't show `Harbor.Ui.Framework` in the architecture diagram
- Doesn't show `Harbor.Desktop.Abstractions` or `Harbor.Desktop.Shared`

### docs/ARCHITECTURE_LAYERS.md (outdated matrix)
- Matrix says `Harbor.Tui.*` renderers reference only `Harbor.Abstractions` + `Harbor.Tui.Abstractions` — FALSE for Avalonia/WPF/MAUI/Blazor which also reference `Harbor.Ui.Framework` and `Harbor.Desktop.Abstractions`
- Uses `Harbor.Terminal.Abstractions` instead of `Harbor.Tui.Abstractions`
- Doesn't list `Harbor.Ui.Framework` in the project inventory
- Doesn't account for `Harbor.Desktop.Shared` (doesn't exist) or `Harbor.Desktop.Abstractions` (does exist but isn't in the matrix)

## Missing specs

### Not covered by any spec but present in code

1. **`Harbor.Ui.Framework`** — the actual shared UI framework project containing:
   - `UiStore` / `UiReducer` / `UiState` / `UiMsg` (TEA)
   - `StoreSubscriberViewModel` (base class for all desktop VMs)
   - `ShellState` (mutable overlay/drawer state)
   - `IContentHost` / `IViewModelLocator` (navigation)
   - `IOverlayStack` / `OverlayController` (overlay system)
   - `SessionFactory` / `SessionManager` / `SessionSwitcher` (session management)
   - `CostAnimator` (animated cost display)

2. **Desktop shell architecture** — `MainViewModel` with `StoreSubscriberViewModel` base, `OnStoreChanged(UiState)` callback, `ShellState` for mutable overlay flags, `OverlayController` with id→property mapping.

3. **`AvaloniaContentHost`** — aggregates shell VMs, breaks constructor parameter explosion. Not mentioned anywhere.

4. **WPF vs Avalonia divergence** — WPF still has all VMs as Transient, doesn't use `MainViewModelBase`, doesn't use `IContentHost`. No spec documents this divergence.

5. **EventBus → UiStore bridge** — `AppHost.BuildAsync` subscribes to `IEventBus` and routes events to per-session `UiStore`. This bridge is not documented.

6. **Lazy<CommandPaletteViewModel>** pattern to break DI cycle — documented in code comments but not in any spec.

7. **ViewModelLocator** — centralized `IServiceProvider`-based VM resolution with cached compiled delegates. Not in any spec.

8. **`Harbor.Core` facade split** — `Harbor.Core` is now an empty facade forwarding to `Harbor.Application` + `Harbor.Registries`. No spec documents this split.

## Recommendations

### Update these specs

1. **`docs/ARCHITECTURE.md`** — Update §"Solution structure" to show actual projects: `Harbor.Application`, `Harbor.Registries`, `Harbor.Ui.Framework`, `Harbor.Desktop.Abstractions`. Update the layering diagram. Add note about `Harbor.Core` being a facade.

2. **`docs/ARCHITECTURE_LAYERS.md`** — Update the project-reference matrix:
   - Add `Harbor.Ui.Framework` as a new layer between Presentation and Application
   - Add `Harbor.Desktop.Abstractions` to the Domain layer
   - Fix `Harbor.Terminal.Abstractions` → `Harbor.Tui.Abstractions`
   - Update Presentation rules: Avalonia/WPF/MAUI/Blazor may reference `Harbor.Ui.Framework` + `Harbor.Desktop.Abstractions`
   - Remove `Harbor.Desktop.Shared` / `Harbor.Desktop.DesignSystem` / `Harbor.Desktop.Animations` / `Harbor.Desktop.CodeEditor` from the plan (they don't exist)

3. **`docs/DESKTOP_APP_PLAN.md`** — Either rewrite or mark as historical. The project structure, ViewModel lifetimes, and ContentHost design have all diverged from the plan. Specifically:
   - Replace `Harbor.Desktop.Shared` with `Harbor.Ui.Framework`
   - Fix `IContentHost<T>` → `IContentHost` (non-generic)
   - Fix Avalonia `MainViewModel` constructor params (11, not 3)
   - Document WPF divergence (Transient VMs, no `MainViewModelBase`)
   - Remove `Harbor.Desktop.CodeEditor` / `Animations` / `DesignSystem` references

4. **`specs/07-tui.md`** — Mark as historical or rewrite for the actual TEA-based terminal TUI architecture. The current spec describes a pre-TUI-overhaul design.

### Mark as historical

- **`specs/07-tui.md`** — completely superseded by `docs/SPECTRE_TUI_DEEP_DIVE.md` and the actual `Harbor.Ui.Framework` architecture.

### Still valid

- **`docs/SPECTRE_TUI_DEEP_DIVE.md`** — Accurate and up-to-date for SpectreTUI internals.
- **`docs/ALTERNATIVE_UIS.md`** — Accurate for terminal renderers and migration path. Desktop section needs minor corrections (project names, layering references).
