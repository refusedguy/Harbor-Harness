# Shell Reconnaissance — Unified Report

> Generated: 2026-08-12
> Scope: Complete UI shell architecture reconnaissance for Harbor
> **Note:** snapshot generated 2026-08-12, before sprint-2. `Harbor.Tui.{Spectre,Spectre.Fullscreen,SpectreTui,TerminalGui,Termina,RazorConsole,Sixel}` now live in `contrib/tui/`, `Harbor.App.{Wpf,Maui,Blazor}` in `contrib/apps/`, their tests in `contrib/tests/`. Paths below are not updated.
> **Status (2026-08-27):** АРХИВНАЯ разведка (вход для прошлых спринтов, см. §6 «Key Findings»). За снапшотом появились ConsoleEx CE-0…CE-5 (`src/Harbor.Tui.ConsoleEx/`), PROD-UI-0 и декомпозиция плагинов; актуальное состояние UI-шелла — `docs/PROJECT_STATUS.md` + `docs/SPECTRE_TUI_DEEP_DIVE.md`.

> Sources: desktop analysis, TUI analysis, tests coverage, specs vs code

---

## Executive Summary

The Harbor UI shell consists of:
- **Avalonia desktop app** with `MainViewModel` (11 params), `AvaloniaContentHost` (10 VMs), overlay system (`OverlayController` + `IOverlayStack`), and 8 view code-behinds casting `DataContext as MainViewModel`
- **WPF desktop app** with `MainViewModel` (8 params), `PanelTab` dockable panels, no overlay system, no `StoreSubscriberViewModel`
- **SpectreTUI** with mature TEA architecture: `UiStore`/`UiReducer`/`UiState`/`UiMsg`, panel system, `ChatViewProjector`, `DefaultUiProjector`
- **Shared framework** (`Harbor.Ui.Framework`) with `StoreSubscriberViewModel`, `IContentHost`, `OverlayController`, `PanelRegistry`, `ShellState`, `TuiEffect`

Key gaps: TUI lacks overlay/modal stack, session rail, command palette, settings. Desktop lacks typed routes, has constructor parameter explosion, direct `MainViewModel` coupling in palette/hotkeys/views.

---

## 1. Desktop Shell Architecture

### 1.1 Avalonia MainViewModel

**File:** `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs`

**Constructor parameters (11, 1 optional):**

| # | Parameter | Type | Notes |
|---|-----------|------|-------|
| 1 | `contentHost` | `AvaloniaContentHost` | Aggregates all child VMs |
| 2 | `commandPalette` | `Lazy<CommandPaletteViewModel>` | Lazy to break DI cycle |
| 3 | `logger` | `ILogger<MainViewModel>` | |
| 4 | `effects` | `TuiEffectHost` | |
| 5 | `dispatcher` | `IDispatcherAdapter` | Forwarded to `StoreSubscriberViewModel` |
| 6 | `theme` | `IThemeService` | |
| 7 | `toasts` | `IToastService` | |
| 8 | `shellStatus` | `ShellStatus` | |
| 9 | `overlayController` | `OverlayController` | |
| 10 | `costAnimator` | `CostAnimator` | |
| 11 | `overlayStack` | `IOverlayStack?` | Optional, defaults to null |

**Inheritance:** `MainViewModel` → `StoreSubscriberViewModel` → `ObservableObject`

**Observable properties (22):**

| Property | Backing Field | Category |
|----------|---------------|----------|
| `ShellStatus` | `_shellStatus` | Status |
| `ActiveSessionCount` | `_activeSessionCount` | Status |
| `ActiveView` | `_activeView` | Pass-through |
| `AgentLabel` | `_agentLabel` | Status |
| `CostUsd` | `_costUsd` | Status |
| `IsCommandPaletteOpen` | `_isCommandPaletteOpen` | Overlay flag |
| `IsDiffOpen` | `_isDiffOpen` | Overlay flag |
| `IsFocusSessionOpen` | `_isFocusSessionOpen` | Overlay flag |
| `IsModelPickerOpen` | `_isModelPickerOpen` | Overlay flag |
| `IsProviderBrowserOpen` | `_isProviderBrowserOpen` | Overlay flag |
| `IsRunning` | `_isRunning` | Status |
| `IsRightDrawerOpen` | `_isRightDrawerOpen` | Drawer |
| `ActiveDiffText` | `_activeDiffText` | Status |
| `ActiveDiffTitle` | `_activeDiffTitle` | Status |
| `IsSettingsOpen` | `_isSettingsOpen` | Overlay flag |
| `IsSidebarVisible` | `_isSidebarVisible` | Sidebar |
| `IsTokenUsageOpen` | `_isTokenUsageOpen` | Overlay flag |
| `MessageCount` | `_messageCount` | Status |
| `ModelLabel` | `_modelLabel` | Status |
| `ProviderLabel` | `_providerLabel` | Status |
| `StatusText` | `_statusText` | Status |
| `TokensIn` | `_tokensIn` | Status |
| `TokensOut` | `_tokensOut` | Status |
| `HasOverlay` | `_hasOverlay` | Overlay state |

**Pass-through properties (10):**

| Property | Delegates To |
|----------|--------------|
| `Chat` | `_contentHost.Chat` |
| `Sessions` | `_contentHost.Sessions` |
| `CodeEditor` | `_contentHost.CodeEditor` |
| `Diff` | `_contentHost.Diff` |
| `TokenUsage` | `_contentHost.TokenUsage` |
| `FocusSession` | `_contentHost.FocusSession` |
| `Board` | `_contentHost.Board` |
| `ProviderBrowser` | `_contentHost.ProviderBrowser` |
| `ProviderModelPicker` | `_contentHost.ProviderModelPicker` |
| `Settings` | `_contentHost.Settings` |
| `CommandPalette` | `_commandPalette.Value` (Lazy) |

**RelayCommands (13):**

| Command | Action |
|---------|--------|
| `OverlayPop` | Close top overlay |
| `ToggleSidebar` | Toggle sidebar visibility |
| `ToggleTheme` | Toggle dark/light theme |
| `OpenCommandPalette` | Open command palette |
| `OpenSettings` | Open settings dialog |
| `OpenProviderBrowser` | Open provider browser |
| `OpenModelPicker` | Open model picker flyout |
| `OpenDiff` | Open diff view |
| `OpenTokenUsage` | Open token usage chart |
| `ToggleFocusSession` | Toggle focus session overlay |
| `SwitchView` | Switch active main view (chat/code/diff) |
| `ToggleRightDrawer` | Toggle right drawer with tab |
| `AddToast` | Push toast notification |

**OnStoreChanged behavior:**
Updates all status fields from `UiState`: `StatusText`, `ProviderLabel`, `ModelLabel`, `AgentLabel`, `TokensIn`, `TokensOut`, `CostUsd`, `IsRunning`, `ActiveSessionCount`, `MessageCount`. Also updates `ShellStatus`, manages cost animation, appends to `TokenHistory`, calls `_contentHost.TokenUsage.RecordUsage(state)`.

### 1.2 AvaloniaContentHost

**File:** `apps/Harbor.App.Avalonia/Navigation/AvaloniaContentHost.cs`

**Constructor parameters (10):**

| # | Parameter | Type |
|---|-----------|------|
| 1 | `chat` | `ChatViewModel` |
| 2 | `sessions` | `SessionListViewModel` |
| 3 | `codeEditor` | `CodeEditorViewModel` |
| 4 | `diff` | `DiffViewModel` |
| 5 | `tokenUsage` | `TokenUsageViewModel` |
| 6 | `providerBrowser` | `ProviderBrowserViewModel` |
| 7 | `providerModelPicker` | `ProviderModelPickerViewModel` |
| 8 | `settings` | `SettingsViewModel` |
| 9 | `focusSession` | `FocusSessionViewModel` |
| 10 | `board` | `BoardViewModel` |

**IContentHost interface:**

```csharp
public interface IContentHost
{
    object? ActiveView { get; }
    void NavigateTo(string route);
}
```

**NavigateTo behavior:** Uses `switch` expression. **Throws `NotSupportedException` for unknown routes.**

| Route | ViewModel |
|-------|-----------|
| `"chat"` | `Chat` |
| `"sessions"` | `Sessions` |
| `"code"` | `CodeEditor` |
| `"diff"` | `Diff` |
| `"tokenUsage"` | `TokenUsage` |
| `"settings"` | `Settings` |
| `"board"` | `Board` |
| *anything else* | **Throws** `NotSupportedException` |

### 1.3 WPF MainViewModel

**File:** `apps/Harbor.App.Wpf/ViewModels/MainViewModel.cs`

**Constructor parameters (8):**

| # | Parameter | Type |
|---|-----------|------|
| 1 | `theme` | `ThemeService` |
| 2 | `dialogs` | `DialogService` |
| 3 | `chat` | `ChatViewModel` |
| 4 | `sessions` | `SessionListViewModel` |
| 5 | `tokens` | `TokenUsageViewModel` |
| 6 | `editor` | `CodeEditorViewModel` |
| 7 | `diff` | `DiffViewModel` |
| 8 | `toasts` | `ToastNotificationViewModel` |

**Inheritance:** `MainViewModel` → `ObservableObject` (does NOT use `StoreSubscriberViewModel`)

**Observable properties (8):**

| Property | Backing Field |
|----------|---------------|
| `ActivePanel` | `_activePanel` |
| `CostText` | `_costText` |
| `IsRunning` | `_isRunning` |
| `Model` | `_model` |
| `Provider` | `_provider` |
| `StatusText` | `_statusText` |
| `Title` | `_title` |
| `TokenCount` | `_tokenCount` |

**Panel management:** Uses `ObservableCollection<PanelTab> Panels` with 4 initial tabs: Chat, Editor, Diff, Tokens. `ActivePanelContent` derives from `ActivePanel?.Content`. `ActivatePanel(string panelId)` iterates the collection.

**Key differences from Avalonia:**
- No `StoreSubscriberViewModel` inheritance
- No overlay system
- No `ShellStatus`/`ShellState`
- No `AvaloniaContentHost` — child VMs injected directly
- No `Lazy<CommandPaletteViewModel>` cycle
- Panels are dockable tabs, not an overlay stack
- Theme toggling shows a toast via `_toasts.Show`

### 1.4 MainViewModelBase

**File:** `src/Harbor.Desktop.Abstractions/ViewModels/MainViewModelBase.cs`

**Constructor parameters (2 overloads):**

| Overload | Parameters |
|----------|------------|
| 1 | `IDispatcherAdapter dispatcher`, `IThemeService theme`, `IOverlayStack? overlayStack`, `ILogger logger` |
| 2 | Above + `OverlayController overlayController`, `CostAnimator costAnimator` |

**Note:** Avalonia `MainViewModel` does NOT inherit from `MainViewModelBase`. WPF `MainViewModel` does NOT inherit from it either.

### 1.5 KeyboardShortcutService

**File:** `apps/Harbor.App.Avalonia/Services/KeyboardShortcutService.cs`

**Constructor parameters:**
- `IOverlayStack overlayStack` (optional, defaults to new `OverlayStackService()`)

**References MainViewModel?** Yes, via `HandleKeyDown(MainViewModel? vm, KeyEventArgs e)` parameter. It dispatches directly to `MainViewModel` commands/properties.

**All key bindings:**

| Key Combination | Action | Target |
|-----------------|--------|--------|
| `Esc` | Close top overlay | `vm.CloseTopOverlay()` |
| `Ctrl+P` | Open command palette | `vm.OpenCommandPaletteCommand.Execute(null)` |
| `Ctrl+B` | Toggle sidebar | `vm.ToggleSidebarCommand.Execute(null)` |
| `Ctrl+Shift+T` | Toggle theme | `vm.ToggleThemeCommand.Execute(null)` |
| `Ctrl+O` | Open file | `vm.CodeEditor.OpenFileCommand.ExecuteAsync(null)` |
| `Ctrl+S` | Save file | `vm.CodeEditor.SaveCommand.ExecuteAsync(null)` |
| `Ctrl+L` | Clear chat | `vm.Chat.ClearCommand.Execute(null)` |

**Wiring:** `MainWindow.axaml.cs` calls `_keyboard.HandleKeyDown(_vm, e)` from `OnKeyDown`.

### 1.6 CommandPaletteViewModel

**File:** `apps/Harbor.App.Avalonia/ViewModels/CommandPaletteViewModel.cs`

**Constructor parameters (4):**
- `IDispatcherAdapter dispatcher`
- `ILogger<CommandPaletteViewModel> logger`
- `MainViewModel mainViewModel` (direct dependency, NOT Lazy)
- `TuiEffectHost effects`

**References MainViewModel?** Yes, directly via constructor injection stored in `_mainViewModel`.

**How it's created in MainViewModel:** Via `Lazy<CommandPaletteViewModel>` to break the DI cycle.

### 1.7 OverlayController

**File:** `src/Harbor.Ui.Framework/Overlays/OverlayController.cs`

**Constructor parameters:**
- `IOverlayStack? stack = null` (defaults to `OverlayStackService`)

**API:**

| Method | Signature | Behavior |
|--------|-----------|----------|
| `Register` | `void Register(string id, Action<bool> setter)` | Maps overlay id to boolean flag setter. Throws if id empty or setter null. |
| `Open` | `void Open(string id)` | Calls setter with `true`, pushes id onto `IOverlayStack`. |
| `Close` | `void Close(string id)` | Calls setter with `false`. |
| `CloseTop` | `bool CloseTop()` | Peeks top id, calls `Close(top)`, then `PopTop()`. |
| `HasOverlay` | `bool` (property) | True when stack has current overlay. |

**Overlay flag mapping (7 overlays):**

| Overlay Id | Boolean Flag | Property |
|------------|-------------|----------|
| `palette` | `IsCommandPaletteOpen` | Command palette |
| `settings` | `IsSettingsOpen` | Settings dialog |
| `providerBrowser` | `IsProviderBrowserOpen` | Provider browser |
| `modelPicker` | `IsModelPickerOpen` | Model picker flyout |
| `diff` | `IsDiffOpen` | Diff view |
| `tokenUsage` | `IsTokenUsageOpen` | Token usage chart |
| `focusSession` | `IsFocusSessionOpen` | Focus session overlay |

### 1.8 Views Referencing MainViewModel

**Avalonia `.axaml.cs` files (8 casts):**

| File | Reference Type | Usage |
|------|----------------|-------|
| `Views/MainWindow.axaml.cs` | Direct field `_vm` | `DataContext = vm;` |
| `Views/Shell/ActivityRailView.axaml.cs` | `DataContext as MainViewModel` | `Vm?.SwitchViewCommand.Execute(...)` |
| `Views/Shell/RightDrawerView.axaml.cs` | `DataContext as MainViewModel` | `Vm?.ToggleRightDrawerCommand.Execute(null)` |
| `Views/Shell/StatusBarView.axaml` | `x:DataType="vm:MainViewModel"` | XAML type-safe binding |
| `Views/ProviderBrowserView.axaml.cs` | `window.DataContext as MainViewModel` | `main.IsProviderBrowserOpen = false` |
| `Views/DiffView.axaml.cs` | `window.DataContext as MainViewModel` | `main.IsDiffOpen = false` |
| `Views/TokenUsageView.axaml.cs` | `window.DataContext as MainViewModel` | `main.IsTokenUsageOpen = false` |
| `Views/FocusSessionView.axaml.cs` | `window.DataContext as MainViewModel` | `main.IsFocusSessionOpen = false` |
| `Views/SettingsView.axaml.cs` | `window.DataContext as MainViewModel` | `main.IsSettingsOpen = false` |
| `Views/CommandPaletteView.axaml.cs` | `window.DataContext as MainViewModel` | `main.OverlayPopCommand.Execute(null)` |
| `Views/Overlays/ModalHostView.axaml.cs` | `DataContext is ViewModels.MainViewModel` | `vm.OverlayPopCommand?.Execute(null)` |
| `Views/Controls/ToolCallCardView.axaml.cs` | `window.DataContext is MainViewModel` | `main.ActiveDiffText = ...; main.IsRightDrawerOpen = true` |

### 1.9 StoreSubscriberViewModel Hierarchy

| Class | File | `OnStoreChanged` behavior |
|-------|------|---------------------------|
| `MainViewModelBase` | `src/Harbor.Desktop.Abstractions/ViewModels/MainViewModelBase.cs` | Abstract base — does NOT override `OnStoreChanged`. Owns overlay stack, cost animation, token history. |
| `MainViewModel` (Avalonia) | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs` | Updates all status fields from `UiState`. Updates `ShellStatus`. Manages cost animation. Records token usage. |
| `ChatViewModelBase` | `src/Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs` | Abstract base — does NOT override `OnStoreChanged`. Holds chat lines, tool calls, streaming state. |
| `ChatViewModel` (Avalonia) | `apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs` | Transitive subclass of `ChatViewModelBase`. |

**Note:** `SessionListViewModelBase` does NOT inherit from `StoreSubscriberViewModel`.

### 1.10 ShellStatus

**File:** `src/Harbor.Ui.Framework/State/ShellStatus.cs`

```csharp
public sealed partial class ShellStatus : ObservableValidator
{
    [ObservableProperty] private string _status = "idle";
    [ObservableProperty] private string _provider = "ollama";
    [ObservableProperty] private string _model = "—";
    [ObservableProperty] private string _agentName = "code";
    [ObservableProperty] private long _tokensIn;
    [ObservableProperty] private long _tokensOut;
    [ObservableProperty] private decimal _costUsd;
    [ObservableProperty] private bool _isAgentRunning;
    [ObservableProperty] private int _activeSessionCount = 1;
    [ObservableProperty] private int _messageCount;
}
```

**Total fields:** 10 observable properties.

### 1.11 DI Registration

**File:** `apps/Harbor.App.Avalonia/Hosting/ViewModelRegistration.cs`

- `MainViewModel` is registered as **Singleton**
- `CommandPaletteViewModel` is **Singleton**
- `AvaloniaContentHost` is registered as `IContentHost` **Singleton**
- `ShellStatus` is **Singleton**
- `OverlayController` is **Singleton**
- `CostAnimator` is **Singleton**

---

## 2. TUI Shell Architecture

### 2.1 UiState Properties

**File:** `src/Harbor.Ui.Framework/State/UiState.cs`

| Property | Type | Purpose |
|----------|------|---------|
| `Lines` | `ImmutableArray<ChatLine>` | Full transcript history, oldest first. |
| `Active` | `ActiveMessage` | Live streaming message buffers (`TextBuffer` + `ThinkBuffer`). |
| `IsStreaming` | `bool` | Whether a message is actively streaming. |
| `Status` | `string` | Human-readable status: `idle` / `running` / `compacting` / `error`. |
| `Cost` | `CostSnapshot` | Cumulative token/cost accounting. |
| `Model` | `string` | Active model id. |
| `Provider` | `string` | Active provider id. |
| `AgentName` | `string` | Active agent name. |
| `IsAgentRunning` | `bool` | Whether the agent is currently running. |
| `WasRunning` | `bool` | Snapshot of `IsAgentRunning` from previous event (rising-edge). |
| `ShouldQuit` | `bool` | User requested quit. |
| `Input` | `InputModel` | Editable prompt state. |
| `Focus` | `FocusMode` | Which region owns keyboard: `Input`, `Chat`, or `Panel`. |
| `ScrollOffset` | `int` | History scroll-back offset. |
| `ViewportLines` | `int` | Number of history rows currently visible. |
| `TotalLines` | `int` | Total number of wrapped transcript rows. |
| `ScrollPercent` | `int` (computed) | How far scrolled as percentage. |
| `PanelStates` | `ImmutableDictionary<string, TuiPanelState>` | Per-panel runtime visibility state. |
| `PanelSizes` | `ImmutableDictionary<string, int>` | Per-panel size override. |
| `FocusedPanelId` | `string?` | Id of panel currently owning keyboard focus, or `null` for chat. |
| `RegisteredPanelIds` | `ImmutableArray<string>` | Registered panel ids in registration order. |

**Mutating methods:** `AddLine`, `SetLine`, `SetInput`, `SetFocus`, `ClearTranscript`, `SetScroll`.

### 2.2 UiMsg Types

**File:** `src/Harbor.Ui.Framework/State/UiMsg.cs`

| DU Case | Category | Description |
|---------|----------|-------------|
| `Agent(AgentEvent)` | agent | Wrap an agent event into the UI pipeline. |
| `KeyInput(ChatAction, UiKey)` | key | Resolved UI action with originating key. |
| `Viewport(int)` | viewport | Renderer reports visible history height. |
| `HistoryMeasured(int)` | viewport | Renderer reports total wrapped transcript rows. |
| `TogglePanel(string)` | panel | Toggle panel Hidden ↔ Visible. |
| `FocusPanel(string?)` | panel | Set focus to panel, or `null` for chat. |
| `CyclePanelFocus` | panel | Cycle focus to next visible panel; last → chat. |
| `ResizePanel(string, int)` | panel | Grow/shrink panel by delta. |
| `ScrollResetToTail` | viewport | Reset scroll offset to 0. |
| `ScrollClamp(int)` | viewport | Clamp scroll offset to max. |
| `SeedPanels(...)` | panel | Host-side seeding of registered panels at startup. |

### 2.3 TuiEffect Types

**File:** `src/Harbor.Ui.Framework/State/UiStore.cs`

| Effect | Trigger | What it does |
|--------|---------|--------------|
| `None` | No-op | Identity effect. |
| `PromptAgent(string)` | Submit input | Run user prompt through agent. |
| `RunSlash(string)` | Slash command | Invoke slash command handler. |
| `AbortAgent` | Abort / Ctrl+C | Cancel running agent. |
| `QuitApp` | Quit / exit words | Leave interactive loop. |

**Missing effects (gap):**
- No `OpenOverlay(string id)` / `CloseOverlay()` effect for modals/palettes.
- No `ShowShell(string mode)` effect for shell-mode switching.
- No `NavigateToSession(SessionId)` effect for session rail clicks.

### 2.4 UiStore API

**File:** `src/Harbor.Ui.Framework/State/UiStore.cs`

- `Dispatch(AgentEvent)` — applies `UiReducer.Reduce` through lock-free CAS loop on `volatile UiState _state`.
- `Dispatch(UiMsg)` — applies `UiReducer.Update` through same CAS loop, returns `TuiEffect`.
- `Transition(Func<UiState, UiState>)` — internal escape hatch for effect runner.
- `Changed` event — raised after every successful state transition.
- `BindSession(model, provider, agentName)` — bind session chrome into state.
- `Reset()` — reset to fresh empty state.

### 2.5 PanelRegistry API

**File:** `src/Harbor.Ui.Framework/Panels/IPanelRegistry.cs`

- `Register(IPanelProvider panel)` — thread-safe, replaces in-place if id exists.
- `Unregister(string id)` — thread-safe removal.
- `All` — all registered providers in registration order.
- `Get(string id)` — lookup by id.
- `View(UiState state)` — build `PanelRegistryView` snapshot.
- `PanelRegistryView.GetVisible()` — providers not `Hidden`.
- `PanelRegistryView.GetVisibleByPlacement(placement)` — filtered by placement.
- `PanelRegistryView.GetState(id)` — current `TuiPanelState`.
- `PanelRegistryView.GetSize(id)` — current size override.

### 2.6 TUI Projectors

**ChatViewProjector** (`src/Harbor.Tui.SpectreTui/View/ChatViewProjector.cs`):
- Facade wiring chrome + history + layout shell.
- Properties: Status, Model, Provider, Agent, IsReadingInput, IsStreaming, TokensIn, TokensOut, Cost, InputText, Focus, FooterText, StreamBuffer, ThinkBuffer, ScrollOffset, etc.
- Widgets built: `Header`, `History`, `Input`, `Footer`, plus optional `StreamBar`.

**DefaultUiProjector** (`src/Harbor.Ui.Framework/Projection/DefaultUiProjector.cs`):
- Projects `UiState` into `UiScreenModel`.
- Outputs: `UiHeaderModel`, `UiTranscriptModel`, `UiStatusBarModel`, `UiInputModel`.
- Pure function reading `UiState` directly.

### 2.7 TUI Keyboard Handling

**Key flow:**
1. Spectre.TUI `KeyMessage` arrives in `ChatScreen.OnMessage`.
2. `ToUiKey(key)` translates Spectre `Key` + `KeyModifiers` → `UiKey`.
3. `ChatKeyMap.Resolve(uiKey)` maps `UiKey` → `ChatAction`.
4. Special cases: `Ctrl+L` → Clear, `Ctrl+C` → Abort, `?` → HelpPanel.
5. `HandlePanelAction(action, uiKey)` handles panel-specific actions before reducer:
   - `TogglePanelSlot` (Alt+1..9) → `UiMsg.TogglePanel(id)`
   - `CyclePanelFocus` (Ctrl+Tab) → `UiMsg.CyclePanelFocus()`
   - `ResizePanelGrow/Shrink` (Ctrl+Up/Down) → `UiMsg.ResizePanel(id, delta)`
   - `HelpPanel` (`?`) → `UiMsg.TogglePanel("help")`
   - `ToggleLogsPanel` (F12) → `UiMsg.TogglePanel("logs")`
6. If panel owns focus, route key to `panel.OnKey(uiKey, ctx)` first.
7. Remaining actions dispatched via `_store.Dispatch(new UiMsg.KeyInput(action, uiKey))`.
8. If effect is not `None`, `_effects.Run(effect)` executes it.

### 2.8 TUI Render Loop

**SpectreTuiRenderer.RunInteractiveAsync:**
1. Creates `UiStore` and `TuiEffectHost`.
2. Binds session chrome into state.
3. Registers builtin panels: HelpPanel, TodoListPanel, DiffPreviewPanel, FileTreePanel, TokenBreakdownPanel, DiagnosticsPanel, LogsPanel.
4. Calls `SeedPanelRegistryIntoState()` to push panel ids/states/sizes into `UiState`.
5. Creates `ChatScreen` and runs `Application.Create(settings).RunAsync(_screen)`.

**ChatScreen.RenderCore (per frame):**
1. Rebuild layout tree if signature changed.
2. Measure `History` area height → dispatch `Viewport`.
3. Rising-edge: if `IsAgentRunning && !WasRunning`, dispatch `ScrollResetToTail`.
4. Project state: `_projector.Project(state)` → `UiScreenModel` → `_viewport.Apply(screen)`.
5. Build widgets: `_panels.BuildWidgets(viewport, state)`.
6. Measure `TotalLines` → dispatch `HistoryMeasured`.
7. Clamp scroll to `MaxScroll` → dispatch `ScrollClamp`.
8. Render each widget into its layout area.

### 2.9 Gap Analysis — Missing Desktop Chrome Features in TUI

| Feature | Desktop (Avalonia/WPF) | SpectreTui | Gap |
|---------|------------------------|------------|-----|
| Overlay / modal stack | `OverlayController` + `IOverlayStack` | ❌ Not used | No modals, flyouts, pickers. |
| Shell state / mode toggles | `ShellState` (right-drawer tab) | ❌ Not used | No right-drawer, no shell-mode switching. |
| Session rail / sidebar | `SessionRowViewModel` + shell rail | ❌ Not present | No session list, no session switching UI. |
| Command palette | `CommandPaletteViewModel` | ❌ Not implemented | No command palette overlay exists. |
| Settings / config overlay | `IThemeService`, `OverlayController` | ❌ Not present | No settings modal. |
| Theme picker | HDS palettes via `IThemeService` | ❌ Not present | TODO in PLAN.md. |
| Permission prompts | Interactive permission flow | ❌ Not present | TODO in PLAN.md. |
| Center / FloatingTab placements | `TuiPanelPlacement.Center`, `FloatingTab` | ❌ Not used | Defined but not laid out. |
| Panel persistence (Pinned) | `TuiPanelState.Pinned` | ❌ Not used | Defined but reducer never transitions to it. |

---

## 3. Test Coverage

### 3.1 Test Projects

| Test project | Purpose |
|---|---|
| `Harbor.Abstractions.Tests` | ValueObjects, identifiers, events |
| `Harbor.Architecture.Tests` | Reflection + NetArchTest layering invariants |
| `Harbor.Core.Tests` | Agent-loop, event-bus, registries, compaction, permissions |
| `Harbor.Tui.Tests` | **Primary TUI unit-test project**: UiReducer, UiStore, InputModel, PanelRegistry, SpectreTui renderer, ChatViewProjector, DefaultUiProjector |
| `Harbor.App.Avalonia.Tests` | Avalonia desktop app: DI, view inflation, killer features, locator convention |
| `Harbor.App.Wpf.Tests` | WPF app DI tests |
| `Harbor.E2E.App.Avalonia` | Avalonia UI E2E: MainViewModel-driven component tests |
| `Harbor.E2E.Tui.SpectreTui` | SpectreTUI PTY E2E: streaming, tool-call, error, compaction, panels, scroll |
| `Harbor.E2E.Framework` | E2E test framework helpers |

### 3.2 Coverage Summary

| Component | Status | Gaps |
|-----------|--------|------|
| `UiReducer` event-reduction | ✅ Well-covered | Overlay messages, full `Update` dispatch for panel messages |
| `UiState` | ✅ Covered via E2E factories | No direct unit tests for state transitions |
| `PanelRegistry` | ✅ Well-covered | `Unregister`, concurrency |
| `TUI projectors` | ✅ Good core coverage | Panel/overlay slots |
| `ContentHost` / `IContentHost` | ❌ **Zero tests** | Entire interface untested |
| `MainViewModel` | ⚠️ E2E + DI only | No isolated unit tests |
| `StoreSubscriberViewModel` | ❌ **Zero tests** | Base class untested |
| `OverlayController` | ❌ **Zero tests** | Overlay stack, open/close/result routing untested |
| `KeyboardShortcutService` | ❌ **Zero tests** | Key mapping untested |
| `CommandPaletteViewModel` | ⚠️ E2E only | No isolated unit tests |
| SpectreTUI E2E | ✅ Solid shell E2E | Overlay interaction, panel resize, WPF/MAUI/Blazor shell E2E |
| Architecture tests | ✅ Strong layering rules | No Presentation-layer ViewModel locator rules |

---

## 4. Specs vs Code

### 4.1 Outdated Specs

**`specs/07-tui.md` — entirely outdated:**
- `TuiState` → actual code uses `UiState`
- Custom ANSI `TuiApp` with `Channel<object>` → actual code uses `UiStore` + `UiReducer`
- `ISlashCommand` interface → actual code uses `SlashCommandDispatcher` with DI
- No mention of `UiStore`, `UiReducer`, `UiMsg`, `StoreSubscriberViewModel`, `TuiEffectHost`

**`docs/DESKTOP_APP_PLAN.md` — outdated project structure:**
- References `Harbor.Desktop.Shared`, `Harbor.Desktop.DesignSystem`, `Harbor.Desktop.Animations`, `Harbor.Desktop.CodeEditor` — none exist
- Actual code uses `Harbor.Ui.Framework` + `Harbor.Desktop.Abstractions`
- Claims `IContentHost<T>` generic — actual is non-generic `IContentHost`
- Claims constructor is 3 params — actual is 11 for Avalonia

**`docs/ARCHITECTURE.md` — outdated layer names:**
- Still shows `Harbor.Core` as monolith containing `AgentLoop`
- Actual: `Harbor.Core` is facade; `AgentLoop` in `Harbor.Application.dll`

**`docs/ARCHITECTURE_LAYERS.md` — outdated matrix:**
- Uses `Harbor.Terminal.Abstractions` instead of `Harbor.Tui.Abstractions`
- Doesn't account for `Harbor.Ui.Framework` or `Harbor.Desktop.Abstractions`

### 4.2 Key Contradictions

| Claim in spec | Code reality | Verdict |
|---------------|--------------|---------|
| "Constructor already 3 parameters" | Avalonia `MainViewModel` has 11 params | **FALSE** |
| `IContentHost<T>` generic | `IContentHost` is non-generic (`object? ActiveView`) | **FALSE** |
| ContentHost creates Visual | `AvaloniaContentHost` exposes ViewModels, not Visuals | **FALSE** |
| Page VM = Transient | Avalonia shell VMs are Singleton; only Onboarding is Transient | **PARTIAL** |
| WPF shares `MainViewModelBase` | WPF does NOT use `MainViewModelBase` | **FALSE** |
| `Factory.Create(IServiceProvider)` | Actual pattern is `IViewModelLocator` with cached delegates | **FALSE** |
| "TUI is reference, don't touch" | Desktop apps are separate, actively developed | **FALSE** |

---

## 5. Specs Inventory

| Document | Path | Relevance |
|---|---|---|
| 07 — TUI | `specs/07-tui.md` | **OUTDATED** — pre-TEA design |
| Desktop App Plan | `docs/DESKTOP_APP_PLAN.md` | **OUTDATED** — wrong project structure |
| Alternative UIs | `docs/ALTERNATIVE_UIS.md` | Mostly accurate, minor corrections needed |
| Spectre TUI Deep Dive | `docs/SPECTRE_TUI_DEEP_DIVE.md` | **Accurate** — up-to-date |
| Architecture | `docs/ARCHITECTURE.md` | Needs update for `Harbor.Application` split |
| Architecture Layers | `docs/ARCHITECTURE_LAYERS.md` | Needs matrix update |

---

## 6. Key Findings for Orchestrator

### What exists and can be reused
- `UiState` already has `PanelStates`, `PanelSizes`, `FocusedPanelId`, `RegisteredPanelIds` — the chrome state is ALREADY in the shared TUI state
- `UiReducer.Update` already handles `TogglePanel`, `FocusPanel`, `CyclePanelFocus`, `ResizePanel`, `SeedPanels`
- `PanelRegistry` is registration-only; runtime state lives in `UiState`
- `OverlayController` + `IOverlayStack` exist in `Harbor.Ui.Framework`
- `StoreSubscriberViewModel` is the base for all desktop VMs
- `IContentHost` exists but needs route type safety
- `DefaultUiProjector` already projects status bar from `UiState`
- `ChatViewProjector` is the Spectre TUI facade

### What needs to be built
1. **`ShellRoute`** — typed closed set of routes, `TryParse`, no throw
2. **`IShellCommands` / `IOverlayCommands`** — UI-agnostic ports
3. **`DesktopChromeState`** (if S2 chosen) — desktop-only overlay flags separate from `UiState`
4. **Extend `IContentHost`** — add `Navigate(ShellRoute)`, `CanNavigate`, `AvailableRoutes`
5. **`WpfContentHost`** — WPF analog of `AvaloniaContentHost`
6. **`StatusBarViewModel` / projector** — extract from `MainViewModel.OnStoreChanged`
7. **`KeyboardShortcutService` without `MainViewModel`** — dispatch through ports
8. **`CommandPaletteViewModel` without `MainViewModel`** — use ports only
9. **Remove `as MainViewModel` casts** in 8 view code-behinds
10. **ADR for surfaces** — classify each surface as route/overlay/tui-panel/tui-only/desktop-only

### Critical decisions needed
1. **S1 vs S2:** Chrome flags in `UiState` (shared) OR `DesktopChromeState` (desktop-only) for overlay flags
2. **Route vs overlay:** Is `Settings` a route or an overlay? Is `Diff` a route or an overlay?
3. **Single `MainViewModel` base:** Should WPF adopt `MainViewModelBase`? Should Avalonia stop duplicating its fields?
4. **Palette implementation:** Is command palette a TUI panel or a desktop overlay?

---

## 7. File Reference Index

### Desktop files
- `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs` — Avalonia shell VM (11 params)
- `apps/Harbor.App.Avalonia/Navigation/AvaloniaContentHost.cs` — Avalonia content host (10 VMs)
- `apps/Harbor.App.Avalonia/Services/KeyboardShortcutService.cs` — Key dispatch to MainViewModel
- `apps/Harbor.App.Avalonia/ViewModels/CommandPaletteViewModel.cs` — Palette with MainViewModel dep
- `apps/Harbor.App.Wpf/ViewModels/MainViewModel.cs` — WPF shell VM (8 params, PanelTab)
- `src/Harbor.Desktop.Abstractions/ViewModels/MainViewModelBase.cs` — Base with overlay/sidebar fields
- `src/Harbor.Ui.Framework/Overlays/OverlayController.cs` — Overlay stack + id→flag mapping
- `src/Harbor.Ui.Framework/Navigation/IContentHost.cs` — Content host interface
- `src/Harbor.Ui.Framework/State/ShellStatus.cs` — 10-field status model

### TUI files
- `src/Harbor.Ui.Framework/State/UiState.cs` — Immutable UI state (17 props + panel chrome)
- `src/Harbor.Ui.Framework/State/UiReducer.cs` — Pure reducer (agent + panel + key)
- `src/Harbor.Ui.Framework/State/UiMsg.cs` — 11 DU cases (panel, key, agent, viewport)
- `src/Harbor.Ui.Framework/State/UiStore.cs` — Lock-free CAS store + TuiEffect
- `src/Harbor.Ui.Framework/Panels/IPanelRegistry.cs` — Registration-only panel registry
- `src/Harbor.Tui.SpectreTui/View/ChatViewProjector.cs` — Spectre TUI facade
- `src/Harbor.Ui.Framework/Projection/DefaultUiProjector.cs` — UiState → UiScreenModel

### View code-behinds with MainViewModel casts
- `apps/Harbor.App.Avalonia/Views/Shell/ActivityRailView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/Shell/RightDrawerView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/ProviderBrowserView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/DiffView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/TokenUsageView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/FocusSessionView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/SettingsView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/CommandPaletteView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/Overlays/ModalHostView.axaml.cs`
- `apps/Harbor.App.Avalonia/Views/Controls/ToolCallCardView.axaml.cs`
