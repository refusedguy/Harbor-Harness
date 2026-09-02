# Desktop Shell Architecture Reconnaissance

> **Note:** snapshot generated 2026-08-12, before sprint-2. `Harbor.Tui.{Spectre,Spectre.Fullscreen,SpectreTui,TerminalGui,Termina,RazorConsole,Sixel}` now live in `contrib/tui/`, `Harbor.App.{Wpf,Maui,Blazor}` in `contrib/apps/`, their tests in `contrib/tests/`. Paths below are not updated.
> **Status (2026-08-27):** АРХИВНАЯ разведка десктопного шелла; актуальное состояние — `docs/PROJECT_STATUS.md`.

## 1. MainViewModel Constructors

### Avalonia `MainViewModel`
**File:** `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs`

| Parameter | Type | Notes |
|-----------|------|-------|
| `contentHost` | `AvaloniaContentHost` | Aggregates all child VMs |
| `commandPalette` | `Lazy<CommandPaletteViewModel>` | Lazy to break DI cycle |
| `logger` | `ILogger<MainViewModel>` | |
| `effects` | `TuiEffectHost` | |
| `dispatcher` | `IDispatcherAdapter` | Forwarded to `StoreSubscriberViewModel` |
| `theme` | `IThemeService` | |
| `toasts` | `IToastService` | |
| `shellStatus` | `ShellStatus` | |
| `overlayController` | `OverlayController` | |
| `costAnimator` | `CostAnimator` | |
| `overlayStack` | `IOverlayStack?` | Optional, defaults to null |

**Total constructor parameters:** 11 (1 optional)

### WPF `MainViewModel`
**File:** `apps/Harbor.App.Wpf/ViewModels/MainViewModel.cs`

| Parameter | Type | Notes |
|-----------|------|-------|
| `theme` | `ThemeService` | |
| `dialogs` | `DialogService` | |
| `chat` | `ChatViewModel` | |
| `sessions` | `SessionListViewModel` | |
| `tokens` | `TokenUsageViewModel` | |
| `editor` | `CodeEditorViewModel` | |
| `diff` | `DiffViewModel` | |
| `toasts` | `ToastNotificationViewModel` | |

**Total constructor parameters:** 8 (no optional)

---

## 2. Observable Properties

### Avalonia `MainViewModel`
**File:** `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs`

| Property | Backing Field | Category |
|----------|---------------|----------|
| `ShellStatus` | `_shellStatus` | Status |
| `ActiveSessionCount` | `_activeSessionCount` | Status |
| `ActiveView` | `_activeView` | Pass-through (via ShellState) |
| `AgentLabel` | `_agentLabel` | Status |
| `CostUsd` | `_costUsd` | Status |
| `IsCommandPaletteOpen` | `_isCommandPaletteOpen` | Overlay flag |
| `IsDiffOpen` | `_isDiffOpen` | Overlay flag |
| `IsFocusSessionOpen` | `_isFocusSessionOpen` | Overlay flag |
| `IsModelPickerOpen` | `_isModelPickerOpen` | Overlay flag |
| `IsProviderBrowserOpen` | `_isProviderBrowserOpen` | Overlay flag |
| `IsRunning` | `_isRunning` | Status |
| `IsRightDrawerOpen` | `_isRightDrawerOpen` | Drawer/Sidebar |
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

### WPF `MainViewModel`
**File:** `apps/Harbor.App.Wpf/ViewModels/MainViewModel.cs`

| Property | Backing Field | Category |
|----------|---------------|----------|
| `ActivePanel` | `_activePanel` | Panel |
| `CostText` | `_costText` | Status |
| `IsRunning` | `_isRunning` | Status |
| `Model` | `_model` | Status |
| `Provider` | `_provider` | Status |
| `StatusText` | `_statusText` | Status |
| `Title` | `_title` | Window |
| `TokenCount` | `_tokenCount` | Status |

### `MainViewModelBase`
**File:** `src/Harbor.Desktop.Abstractions/ViewModels/MainViewModelBase.cs`

| Property | Backing Field | Category |
|----------|---------------|----------|
| `ActiveSessionCount` | `_activeSessionCount` | Status |
| `ActiveView` | `_activeView` | Pass-through |
| `AgentLabel` | `_agentLabel` | Status |
| `CostUsd` | `_costUsd` | Status |
| `HasOverlay` | `_hasOverlay` | Overlay state |
| `IsCommandPaletteOpen` | `_isCommandPaletteOpen` | Overlay flag |
| `IsDiffOpen` | `_isDiffOpen` | Overlay flag |
| `IsFocusSessionOpen` | `_isFocusSessionOpen` | Overlay flag |
| `IsModelPickerOpen` | `_isModelPickerOpen` | Overlay flag |
| `IsProviderBrowserOpen` | `_isProviderBrowserOpen` | Overlay flag |
| `IsRightDrawerOpen` | `_isRightDrawerOpen` | Drawer |
| `IsRunning` | `_isRunning` | Status |
| `IsSettingsOpen` | `_isSettingsOpen` | Overlay flag |
| `IsSidebarVisible` | `_isSidebarVisible` | Sidebar |
| `IsTokenUsageOpen` | `_isTokenUsageOpen` | Overlay flag |
| `MessageCount` | `_messageCount` | Status |
| `ModelLabel` | `_modelLabel` | Status |
| `ProviderLabel` | `_providerLabel` | Status |
| `StatusText` | `_statusText` | Status |
| `TokensIn` | `_tokensIn` | Status |
| `TokensOut` | `_tokensOut` | Status |

**Note:** Avalonia `MainViewModel` does NOT inherit from `MainViewModelBase`. It inherits directly from `StoreSubscriberViewModel`. WPF `MainViewModel` inherits from `ObservableObject` and does NOT use `StoreSubscriberViewModel` at all.

---

## 3. Pass-Through Properties

### Avalonia `MainViewModel`
All pass through to `_contentHost` (`AvaloniaContentHost`):

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

Derived/computed properties (not pass-through):
- `RightDrawerTab` → `ShellState.RightDrawerTab`
- `StatusBrushKey` → `StatusMappers.StatusToBrushKey(StatusText)`
- `TokensInText` → `StatusMappers.TokensToCompact(TokensIn)`
- `TokensOutText` → `StatusMappers.TokensToCompact(TokensOut)`
- `CostText` → `StatusMappers.CostToUsd(CostUsd)`
- `RunningDurationText` → local `_runningStartTime`
- `AnimatedCostText` → `StatusMappers.CostToUsd(_displayCost)`
- `ShowAnimatedCost` → `_runningStartTime is not null`
- `ThemeService` → `_theme`
- `Toasts` → `ObservableCollection<ToastNotification>`

### WPF `MainViewModel`
No pass-through pattern. Child VMs are stored as direct properties:

| Property | Type | Notes |
|----------|------|-------|
| `Chat` | `ChatViewModel` | |
| `Sessions` | `SessionListViewModel` | |
| `Tokens` | `TokenUsageViewModel` | |
| `Editor` | `CodeEditorViewModel` | |
| `Diff` | `DiffViewModel` | |
| `Toasts` | `ToastNotificationViewModel` | |
| `Panels` | `ObservableCollection<PanelTab>` | Dockable panel collection |
| `ActivePanelContent` | `ObservableObject?` | Derived from `ActivePanel?.Content` |

---

## 4. Overlay System

### Registration
In Avalonia `MainViewModel` constructor, 7 overlays are registered:

```csharp
_overlayController.Register("palette", v => IsCommandPaletteOpen = v);
_overlayController.Register("settings", v => IsSettingsOpen = v);
_overlayController.Register("providerBrowser", v => IsProviderBrowserOpen = v);
_overlayController.Register("modelPicker", v => IsModelPickerOpen = v);
_overlayController.Register("diff", v => IsDiffOpen = v);
_overlayController.Register("tokenUsage", v => IsTokenUsageOpen = v);
_overlayController.Register("focusSession", v => IsFocusSessionOpen = v);
```

### OverlayController API
**File:** `src/Harbor.Ui.Framework/Overlays/OverlayController.cs`

| Method | Signature | Behavior |
|--------|-----------|----------|
| `Register` | `void Register(string id, Action<bool> setter)` | Maps overlay id to boolean flag setter. Throws `ArgumentException` if id is empty, `ArgumentNullException` if setter is null. |
| `Open` | `void Open(string id)` | Calls the setter with `true`, then pushes id onto `IOverlayStack`. No-op if id is empty or not registered. |
| `Close` | `void Close(string id)` | Calls the setter with `false`. No-op if id is empty or not registered. |
| `CloseTop` | `bool CloseTop()` | Peeks top id from stack, calls `Close(top)`, then `PopTop()`. Returns `true` if an overlay was closed, `false` if stack empty. |
| `HasOverlay` | `bool` (property) | True when stack has a current overlay. |

### Overlay Flag Mapping
| Overlay Id | Boolean Flag | Property |
|------------|-------------|----------|
| `palette` | `IsCommandPaletteOpen` | Open/close command palette |
| `settings` | `IsSettingsOpen` | Open/close settings dialog |
| `providerBrowser` | `IsProviderBrowserOpen` | Open/close provider browser |
| `modelPicker` | `IsModelPickerOpen` | Open/close model picker flyout |
| `diff` | `IsDiffOpen` | Open/close diff view |
| `tokenUsage` | `IsTokenUsageOpen` | Open/close token usage chart |
| `focusSession` | `IsFocusSessionOpen` | Open/close focus session overlay |

---

## 5. Keyboard Shortcuts

### KeyboardShortcutService
**File:** `apps/Harbor.App.Avalonia/Services/KeyboardShortcutService.cs`

**Constructor parameters:**
- `IOverlayStack overlayStack` (optional, defaults to new `OverlayStackService()`)

**Does it reference MainViewModel?** Yes, via `HandleKeyDown(MainViewModel? vm, KeyEventArgs e)` parameter. It dispatches directly to `MainViewModel` commands/properties.

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

---

## 6. Command Palette

### CommandPaletteViewModel
**File:** `apps/Harbor.App.Avalonia/ViewModels/CommandPaletteViewModel.cs`

**Constructor parameters:**
- `IDispatcherAdapter dispatcher`
- `ILogger<CommandPaletteViewModel> logger`
- `MainViewModel mainViewModel` (direct dependency, NOT Lazy)
- `TuiEffectHost effects`

**Does it reference MainViewModel?** Yes, directly via constructor injection stored in `_mainViewModel`. Exposed as `private MainViewModel Main => _mainViewModel;`

**How it's created in MainViewModel:** Via `Lazy<CommandPaletteViewModel>` to break the DI cycle (MainViewModel → CommandPaletteViewModel → MainViewModel).

**All commands exposed:**

| Command Label | Type | Action |
|---------------|------|--------|
| "Switch to chat" | command | `SwitchToChat()` → `Main.SwitchViewCommand.Execute("chat")` |
| "Switch to code editor" | command | `SwitchToCode()` → `Main.SwitchViewCommand.Execute("code")` |
| "Open settings" | command | `OpenSettings()` → `Main.IsSettingsOpen = true` |
| "Open provider browser" | command | `OpenProviderBrowser()` → `Main.IsProviderBrowserOpen = true` |
| "Open diff view" | command | `OpenDiff()` → `Main.IsDiffOpen = true` |
| "Open token usage chart" | command | `OpenTokenUsage()` → `Main.IsTokenUsageOpen = true` |
| "Toggle sidebar (Ctrl+B)" | command | `ToggleSidebar()` → `Main.ToggleSidebarCommand.Execute(null)` |
| "Toggle theme (Ctrl+Shift+T)" | command | `ToggleTheme()` → `Main.ToggleThemeCommand.Execute(null)` |
| "New session" | command | `NewSession()` → `Main.Sessions.NewSessionCommand.ExecuteAsync(null)` |
| "Branch active session" | command | `BranchSession()` → `Main.Sessions.BranchCommand.ExecuteAsync(null)` |
| "Open file (Ctrl+O)" | command | `OpenFile()` → `Main.CodeEditor.OpenFileCommand.ExecuteAsync(null)` |
| "Save file (Ctrl+S)" | command | `SaveFile()` → `Main.CodeEditor.SaveCommand.ExecuteAsync(null)` |
| "Stop agent" | command | `StopAgent()` → `Main.Chat.StopCommand.Execute(null)` |
| "Clear chat (Ctrl+L)" | command | `ClearChat()` → `Main.Chat.ClearCommand.Execute(null)` |
| "Refresh session list" | command | `RefreshSessions()` → `Main.Sessions.RefreshCommand.ExecuteAsync(null)` |
| "/help" | slash | `RunSlash("/help")` |
| "/exit" | slash | `RunSlash("/exit")` |
| "/setup" | slash | `RunSlash("/setup")` |
| "/auth" | slash | `RunSlash("/auth")` |
| "/model" | slash | `RunSlash("/model")` |
| "/agent" | slash | `RunSlash("/agent")` |
| "/config" | slash | `RunSlash("/config")` |
| "/providers" | slash | `RunSlash("/providers")` |
| "/sessions" | slash | `RunSlash("/sessions")` |
| "/tui" | slash | `RunSlash("/tui")` |
| "/storage" | slash | `RunSlash("/storage")` |
| "/clear" | slash | `RunSlash("/clear")` |

**Fuzzy search:** Implemented via `FuzzyScore()` (subsequence match with length penalty). Results are filtered and ordered in `OnQueryChanged` partial method.

---

## 7. ContentHost

### IContentHost Interface
**File:** `src/Harbor.Ui.Framework/Navigation/IContentHost.cs`

```csharp
public interface IContentHost
{
    object? ActiveView { get; }
    void NavigateTo(string route);
}
```

### AvaloniaContentHost
**File:** `apps/Harbor.App.Avalonia/Navigation/AvaloniaContentHost.cs`

**Constructor parameters (10):**
- `ChatViewModel chat`
- `SessionListViewModel sessions`
- `CodeEditorViewModel codeEditor`
- `DiffViewModel diff`
- `TokenUsageViewModel tokenUsage`
- `ProviderBrowserViewModel providerBrowser`
- `ProviderModelPickerViewModel providerModelPicker`
- `SettingsViewModel settings`
- `FocusSessionViewModel focusSession`
- `BoardViewModel board`

**Generic type parameters:** None.

**Exposed properties:** `Chat`, `Sessions`, `CodeEditor`, `Diff`, `TokenUsage`, `ProviderBrowser`, `ProviderModelPicker`, `Settings`, `FocusSession`, `Board`

**NavigateTo behavior:** Uses a `switch` expression. Throws `NotSupportedException` for unknown routes.

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

---

## 8. Views Referencing MainViewModel

### Avalonia `.axaml.cs` files

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
| `Views/ToastNotificationsView.axaml.cs` | Comment only | No code-behind reference |

### WPF `.xaml.cs` files

| File | Reference Type | Usage |
|------|----------------|-------|
| `Views/MainWindow.xaml.cs` | Direct field `_vm` | `DataContext = vm;` |
| `Services/DialogService.cs` | `DataContext = vm` | Sets DataContext on dialog windows |

---

## 9. StoreSubscriberViewModel Hierarchy

### Direct subclasses of `StoreSubscriberViewModel`

| Class | File | `OnStoreChanged` behavior |
|-------|------|---------------------------|
| `MainViewModelBase` | `src/Harbor.Desktop.Abstractions/ViewModels/MainViewModelBase.cs` | Abstract base — does NOT override `OnStoreChanged`. Owns overlay stack, cost animation, token history. |
| `MainViewModel` (Avalonia) | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs` | Updates all status fields from `UiState`. Updates `ShellStatus`. Manages cost animation start/stop. Records token usage. |
| `ChatViewModelBase` | `src/Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs` | Abstract base — does NOT override `OnStoreChanged`. Holds chat lines, tool calls, streaming state. |

### Transitive subclasses (derive from `ChatViewModelBase`)

| Class | File |
|-------|------|
| `ChatViewModel` (Avalonia) | `apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs` |

**Note:** `SessionListViewModelBase` does NOT inherit from `StoreSubscriberViewModel`. It inherits from `ViewModelBase`. The Avalonia `SessionListViewModel` inherits from `ObservableObject` directly.

---

## 10. WPF MainViewModel

### Constructor parameters (8)
`ThemeService theme, DialogService dialogs, ChatViewModel chat, SessionListViewModel sessions, TokenUsageViewModel tokens, CodeEditorViewModel editor, DiffViewModel diff, ToastNotificationViewModel toasts`

### Observable properties (8)
`ActivePanel`, `CostText`, `IsRunning`, `Model`, `Provider`, `StatusText`, `Title`, `TokenCount`

### RelayCommands
- `ToggleTheme` → `_theme.Toggle(); _toasts.Show(...)`
- `BrowseProviders` → `_dialogs.ShowProviderBrowser()`
- `OpenSettings` → `_dialogs.ShowSettings()`
- `OpenCommandPalette` → `_dialogs.ShowCommandPalette(owner)`

### Panel management
Uses `ObservableCollection<PanelTab> Panels` with 4 initial tabs: Chat, Editor, Diff, Tokens. `ActivePanelContent` derives from `ActivePanel?.Content`. `ActivatePanel(string panelId)` iterates the collection to find and set the active panel.

### Key differences from Avalonia
- No `StoreSubscriberViewModel` inheritance
- No overlay system
- No `ShellStatus`/`ShellState`
- No `AvaloniaContentHost` — child VMs injected directly
- No `Lazy<CommandPaletteViewModel>` cycle
- Panels are dockable tabs, not an overlay stack
- Theme toggling shows a toast via `_toasts.Show`

---

## 11. ShellStatus

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

---

## 12. OnStoreChanged Analysis

### Avalonia `MainViewModel.OnStoreChanged`
**File:** `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs` (lines 254-305)

Updates these fields from `UiState`:
- `StatusText` = `state.Status`
- `ProviderLabel` = `state.Provider` (or "—")
- `ModelLabel` = `state.Model` (or "—")
- `AgentLabel` = `state.AgentName` (or "—")
- `TokensIn` = `state.Cost.TokensIn`
- `TokensOut` = `state.Cost.TokensOut`
- `CostUsd` = `state.Cost.CostUsd`
- `IsRunning` = `state.IsAgentRunning`
- `ActiveSessionCount` = `Math.Max(1, _contentHost.Sessions.Sessions.Count)`
- `MessageCount` = `state.Lines.Length`

Also updates `ShellStatus` fields (same values), manages cost animation start/stop based on `IsRunning` transition, appends to `TokenHistory` (max 60 entries), and calls `_contentHost.TokenUsage.RecordUsage(state)`.

Raises `OnPropertyChanged` for: `StatusBrushKey`, `TokensInText`, `TokensOutText`, `CostText`, `RunningDurationText`, `AnimatedCostText`, `ShowAnimatedCost`.

---

## 13. Constructor Size Summary

| Variant | File | Parameter Count | Optional |
|---------|------|-----------------|----------|
| Avalonia `MainViewModel` | `apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs` | 11 | 1 (`overlayStack`) |
| WPF `MainViewModel` | `apps/Harbor.App.Wpf/ViewModels/MainViewModel.cs` | 8 | 0 |
| `MainViewModelBase` (ctor 1) | `src/Harbor.Desktop.Abstractions/ViewModels/MainViewModelBase.cs` | 4 | 1 (`overlayStack`) |
| `MainViewModelBase` (ctor 2) | `src/Harbor.Desktop.Abstractions/ViewModels/MainViewModelBase.cs` | 6 | 0 |
| `AvaloniaContentHost` | `apps/Harbor.App.Avalonia/Navigation/AvaloniaContentHost.cs` | 10 | 0 |
| `CommandPaletteViewModel` | `apps/Harbor.App.Avalonia/ViewModels/CommandPaletteViewModel.cs` | 4 | 0 |
| `KeyboardShortcutService` | `apps/Harbor.App.Avalonia/Services/KeyboardShortcutService.cs` | 1 | 1 (default ctor) |

---

## 14. DataContext Assignments

### Avalonia
| File | Target |
|------|--------|
| `Views/MainWindow.axaml.cs` | `DataContext = vm` (MainViewModel) |
| `Views/OnboardingWindow.axaml.cs` | `DataContext = viewModel` (OnboardingViewModel) |

### WPF
| File | Target |
|------|--------|
| `Views/MainWindow.xaml.cs` | `DataContext = vm` (MainViewModel) |
| `Services/DialogService.cs` | `view.DataContext = vm` (various dialog VMs) |

---

## 15. Architecture Notes

### DI Registration
**File:** `apps/Harbor.App.Avalonia/Hosting/ViewModelRegistration.cs`

- `MainViewModel` is registered as **Singleton**
- `CommandPaletteViewModel` is **Singleton**
- `AvaloniaContentHost` is registered as `IContentHost` **Singleton**
- `ShellStatus` is **Singleton**
- `OverlayController` is **Singleton**
- `CostAnimator` is **Singleton**

The `Lazy<CommandPaletteViewModel>` in `MainViewModel` breaks the circular dependency: `MainViewModel` → `CommandPaletteViewModel` → `MainViewModel`.

### Avalonia vs WPF divergence
- Avalonia uses the full `StoreSubscriberViewModel` / `OverlayController` / `ShellStatus` architecture
- WPF uses a simpler `ObservableObject`-based approach with `DialogService` for modals and `PanelTab` for tabbed panels
- WPF has no overlay stack, no status-bar sparkline, no cost animation
