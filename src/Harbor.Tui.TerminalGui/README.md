# Harbor.Tui.TerminalGui

Experimental TUI renderer using Terminal.Gui v2 — the mainstream .NET TUI framework. Provides a mature widget set (menus, dialogs, text editors, tree views) and cross-platform key/mouse handling.

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)
- `Terminal.Gui`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `TerminalGuiTuiRenderer` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `TerminalGuiTuiRenderer` — Terminal.Gui-based renderer
- `ChatView` — uses Terminal.Gui's TextView + ListView

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, TerminalGuiTuiRenderer>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## Experimental status

Terminal.Gui v2 is stable but its application model (Application.Init/Run) doesn't play well with Harbor's DI-based composition. This renderer is currently a proof-of-concept; further work is needed to integrate it cleanly.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
