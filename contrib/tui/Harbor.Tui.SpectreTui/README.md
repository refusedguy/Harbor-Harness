# Harbor.Tui.SpectreTui

Default interactive TUI renderer using Spectre.TUI (the official Spectre widget framework). Provides a fully interactive REPL with input box, scrollable transcript, command palette, and panel system. **This is the default `harbor` CLI renderer in interactive mode.**

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)
- `Spectre.Tui`
- `Spectre.Tui.App`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `SpectreTuiInteractiveRenderer` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `SpectreTuiInteractiveRenderer` — main interactive renderer
- `ChatScreen` — transcript + input view
- `CommandPalette` — Cmd+K command menu

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, SpectreTuiInteractiveRenderer>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## Default renderer

This is what `harbor` boots into when stdout is a TTY and no `HARBOR_TUI` env var is set. Implements the full panel system — plugins can register `ITuiPanelPlugin` instances that show up as docked panels.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
