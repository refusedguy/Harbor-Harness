# Harbor.Tui.Abstractions

TUI abstractions — MVVM-style contracts shared across all TUI renderers (Ansi, Plain, Spectre, etc.) and desktop GUI apps. Defines `ITuiRenderer`, `ITuiReducer`, `UiState`, `UiEvent`, and the panel system contracts.

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)
- `CommunityToolkit.Mvvm` (source-generated ObservableObject)
- `CommunityToolkit.HighPerformance`
- `CSharpFunctionalExtensions`

## Public API

- `(none — abstractions only)` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `ITuiRenderer` — sink for UiState snapshots
- `ITuiReducer` — folds AgentEvent -> UiState
- `UiStore` — observable state container (Transition helper exposed to desktop GUIs via InternalsVisibleTo)
- `UiState` / `UiEvent` — immutable record types
- `ITuiPanelPlugin` — panel-system contract for TUI plugins

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, (none — abstractions only)>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## InternalsVisibleTo

Exposes `UiStore.Transition` to: `Harbor.App.Avalonia`, `Harbor.Tui.Avalonia`, `Harbor.Tui.Wpf`, `Harbor.Tui.Maui`, `Harbor.Tui.Blazor`. Desktop GUIs use it to fold non-agent state transitions (e.g. inserting a user-input line into the transcript before the agent emits a UserMessage event).

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
