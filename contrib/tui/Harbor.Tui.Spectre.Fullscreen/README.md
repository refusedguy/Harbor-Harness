# Harbor.Tui.Spectre.Fullscreen

Full-screen TUI renderer using Spectre.Console — live layout with separate panels for chat, tools, and status. Uses Spectre's `LiveDisplay` for in-place updates without scrollback.

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)
- `Spectre.Console`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `SpectreFullscreenTuiRenderer` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `SpectreFullscreenTuiRenderer` — full-screen layout
- `FullscreenLayout` — three-panel layout (chat / tools / status)

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, SpectreFullscreenTuiRenderer>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## When to use

Set `HARBOR_TUI=spectre-fullscreen`. Best for long-running agent tasks where you want to watch progress without losing scrollback. Requires a terminal >= 80 columns.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
