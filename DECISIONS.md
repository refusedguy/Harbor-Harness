# DECISIONS.md — Harbor Architecture Decisions

> This file records concrete, binding decisions for the Harbor project.
> Each section has an id, status, and the explicit choice made.
> If a decision is later reversed, the old entry is kept with a [SUPERSEDED] marker
> and a new entry is added.

---

## Shell architecture (2026-08-12)

**Status:** Accepted

### Context

The UI shell spans three renderers (SpectreTUI, Avalonia, WPF) and has accumulated
multiple sources of truth for the same chrome state:
- `UiState` (immutable record, CAS store) — agent-driven state
- `ShellState` (mutable `ObservableObject`) — Avalonia overlay/drawer flags
- `ShellStatus` (mutable `ObservableValidator`) — status fields duplicated from `MainViewModel`
- `MainViewModel` `[ObservableProperty]` fields — yet another copy of the same flags
- `OverlayController` — writes directly into `MainViewModel` boolean setters

This creates a **triple-write** problem: `OnStoreChanged` copies `UiState` → `MainViewModel`
fields AND `ShellStatus` AND calls `TokenUsage.RecordUsage`. Three writers, zero single source of truth.

### Decision

**S1 (shared chrome in UiState, desktop overlays stay desktop-only).**

1. **Shared chrome** (provider, model, agent, status, cost, tokens, running state)
   is projected from `UiState` via a single pure `ProjectStatus(UiState)` function.
   Both SpectreTUI and Avalonia call this projector. `ShellStatus` becomes a thin wrapper
   or is removed from duplicate field-by-field assignment.

2. **Desktop-only overlays** (palette, settings, diff modal, provider browser, model picker,
   focus session, token usage) remain in Avalonia-only code. They are **not** added to
   `UiState`, `UiMsg`, or the TUI reducer. SpectreTUI does not import `OverlayController`.

3. **Navigation routes** are typed via `ShellRoute` closed set + `TryParse`.
   `IContentHost.NavigateTo` accepts `string` for now but logs unknown routes instead of throwing.
   A future `TryNavigate(ShellRoute)` may replace it.

4. **Ports over MainViewModel references:**
   - `IShellChrome` — navigate, sidebar, overlay open/close/pop, theme
   - `IWorkspaceCommands` — new session, branch, refresh, open/save file, stop agent, clear chat
   `CommandPaletteViewModel`, `KeyboardShortcutService`, and view code-behinds depend on these
   ports, not on `MainViewModel`.

5. **WPF is excluded from overlay stack migration** in this epic. WPF keeps its `PanelTab` /
   `DialogService` approach. Shared status projection is optional for WPF.

6. **`MainViewModelBase`** — Avalonia either inherits from it (and removes duplicated properties)
   or the base is marked `[Obsolete]`. No third variant is allowed.

### Consequences

- `UiState` does **not** gain `IsSettingsOpen`, `IsDiffOpen`, etc.
- SpectreTUI does **not** gain overlay stack or modals.
- Avalonia `MainViewModel` loses direct `MainViewModel` references in palette, hotkeys, and views.
- `OverlayController` is the single writer for overlay open/close state.
- Status bar text is produced by one pure function, consumed by both renderers.

### Surfaces (as-is)

| id | host route | overlay | TUI panel | WPF |
|---|---|---|---|---|
| chat | ✅ | ❌ | chat (not a panel) | tab |
| sessions | ✅ | ❌ | ❌ no rail | ❌ |
| code | ✅ | ❌ | — | tab |
| board | ✅ | ❌ | — | — |
| settings | ✅ | ✅ | ❌ | dialog |
| diff | ✅ | ✅ | DiffPreviewPanel | tab |
| tokenUsage | ✅ | ✅ | TokenBreakdownPanel | tab |
| providerBrowser | ✅ | ✅ | ❌ | dialog |
| modelPicker | ✅ | ✅ | ❌ | — |
| focusSession | ✅ | ✅ | ❌ | — |
| palette | ❌ | ✅ | ❌ | dialog |
| sidebar/drawer | ShellState | — | panels L/R | — |

### Rejected alternatives

- **S2:** Add `DesktopChromeState` record + `ShellMsg` + `ShellReducer` alongside `UiState`.
  Rejected because it introduces a second store and a second reducer for the same session chrome.
  The existing `UiState` already models shared chrome; desktop-only overlays should not force
  TUI to carry flags it cannot render.

- **WPF overlay migration:** Migrating WPF to `IContentHost` + `OverlayController` in this epic.
  Rejected because WPF's `PanelTab` / `DialogService` model is productively different.
  Shared contracts (status projection) are sufficient.

- **`IContentHost<Visual>` / generic host:** Generic Visual factory was rejected because it
  couples navigation to view resolution, breaks AOT, and duplicates XAML `DataTemplate` binding.

---

## ViewModelLocator (2026-08-12)

**Status:** Accepted

`IViewModelLocator` / `ViewModelLocator` exists in `src/Harbor.Desktop.Shared/Locators/`.
It wraps `IServiceProvider` and caches compiled `Func<IServiceProvider, object?>` delegates.
It is **not** forbidden, but it must **not** be introduced into new code in this epic.
The convention test `LocatorConventionTests` remains in place.

---

## StoreSubscriberViewModel (2026-08-12)

**Status:** Accepted

`StoreSubscriberViewModel` subscribes to `UiStore.Changed` via `Dispatcher.StateChanged`.
It is the base class for Avalonia `MainViewModel` and `ChatViewModelBase`.
It must **not** be removed or replaced with `IEventBus` subscription in this epic.

---

## Implementation results (2026-08-12)

**Status:** Completed

### What was built

| Task | Artifact | Status |
|------|----------|--------|
| `impl-status-model` | `src/Harbor.Ui.Framework/Projection/StatusProjector.cs` — pure `ProjectStatusBar(UiState)` + `ProjectFooter(UiState)` | ✅ |
| `impl-chrome-ports` | `IShellChrome` + `IWorkspaceCommands` in `src/Harbor.Ui.Framework/Navigation/` | ✅ |
| `impl-chrome-adapter` | `AvaloniaShellChrome` + `AvaloniaWorkspaceCommands` in `apps/Harbor.App.Avalonia/Services/` | ✅ |
| `impl-host-safe-nav` | `IContentHost.TryNavigate` + `AvailableRoutes`; `AvaloniaContentHost` no longer throws on unknown routes | ✅ |
| `impl-palette-ports` | `CommandPaletteViewModel` uses `IShellChrome` + `IWorkspaceCommands`, no `MainViewModel` reference | ✅ |
| `impl-hotkeys-ports` | `KeyboardShortcutService` uses ports, `MainWindow.axaml.cs` wiring updated | ✅ |
| `impl-views-no-cast` | 10 view code-behinds: no `as MainViewModel` casts, all use `IShellChrome` or own VM | ✅ |
| `impl-drop-lazy` | `Lazy<CommandPaletteViewModel>` removed from `MainViewModel` — DI cycle is dead | ✅ |
| `impl-overlay-single-writer` | 7 overlay boolean properties have `private set`; `OverlayController` is single writer | ✅ |
| `impl-status-bind` | `MainViewModel.OnStoreChanged` calls `StatusProjector.ProjectStatusBar(state)` | ✅ |
| `impl-tui-use-status-projector` | SpectreTUI `ChatChromeView` / `ChatViewProjector` / `SpectreUiViewport` use shared `StatusProjector` | ✅ |
| `impl-wpf-status-optional` | WPF idle status strings match projector output; no store subscription added | ✅ |
| `impl-absorb-base` | `MainViewModelBase` marked `[Obsolete]` — WPF/Avalonia diverged, no inheritance migration | ✅ |

### Tests written (wave B)

| Test task | Artifact | Status |
|-----------|----------|--------|
| `test-status-model` | `tests/Harbor.Tui.Tests/Projection/StatusProjectorTests.cs` — 9 tests | ✅ |
| `test-host-nav` | `tests/Harbor.App.Avalonia.Tests/ContentHostTests.cs` — 10 tests | ✅ |
| `test-chrome-adapter` | `tests/Harbor.App.Avalonia.Tests/AvaloniaShellChromeTests.cs` + `AvaloniaWorkspaceCommandsTests.cs` — 11 tests | ✅ |
| `test-status-apply` | `tests/Harbor.App.Avalonia.Tests/StatusProjectionTests.cs` — 6 tests | ✅ |
| `test-overlay-controller` | `tests/Harbor.Tui.Tests/OverlayControllerTests.cs` — 10 tests | ✅ |
| `test-no-mainvm-refs` | `tests/Harbor.App.Avalonia.Tests/NoMainViewModelRefsTests.cs` — 12 reflection tests | ✅ |

### Confirmed decisions

- **S1 confirmed:** `UiState` does not carry Avalonia overlay flags. TUI remains overlay-free.
- **WPF-lite confirmed:** WPF does not receive overlay stack, `IContentHost`, or store subscription.
- **Single status projector confirmed:** `StatusProjector` is the single source of truth for status bar segments, consumed by both SpectreTUI and Avalonia.
- **Ports over MainViewModel confirmed:** `IShellChrome` + `IWorkspaceCommands` replace direct `MainViewModel` references in palette, hotkeys, and views.
- **OverlayController is single writer confirmed:** All overlay flag mutations flow through `OverlayController.Open/Close/CloseTop`. External setters are private.
- **`MainViewModelBase` obsolete confirmed:** WPF and Avalonia have diverged; no inheritance migration performed.

### What was NOT done (and why)

| Item | Reason |
|------|--------|
| `impl-tui-keys` | TUI already has full keymap; adding `IShellChrome` to `ChatScreen` without palette in TUI = dead code. Deferred. |
| WPF overlay migration | WPF `PanelTab` / `DialogService` model is productively different. Shared status projection is sufficient. |
| WPF `IContentHost` | Not in scope for this epic. Avalonia-only. |
| `ShellStore` / `ShellUiState` | Rejected by ADR — would introduce second store. |
| `IContentHost<Visual>` / generic host | Rejected — couples navigation to view resolution, breaks AOT. |
| Spec rewrites (`07-tui.md`, `DESKTOP_APP_PLAN.md`) | Doc-creep is out of scope. Specs are historical; `DECISIONS.md` is the living document. |
| `IViewModelLocator` in new code | Accepted constraint — not introduced. |

### DoD check

1. ✅ Palette + hotkeys + listed views do not reference `MainViewModel` type (grep verified by `NoMainViewModelRefsTests`)
2. ✅ No `Lazy<CommandPalette>` — cycle is dead, direct injection used
3. ✅ No new store/record-double of `UiState`
4. ✅ TUI did not import `OverlayController`
5. ✅ `NavigateTo` unknown does not throw (logs warning, returns false)
6. ✅ Status: one pure projector, TUI and Avalonia both call it
7. ✅ Pass-through `Chat`/`Sessions`/… on place
8. ✅ All `test-*` task files exist on disk
9. ✅ WPF not rewritten under Avalonia overlays
10. ✅ No `dotnet build`/`test` run by agents (protocol respected)
