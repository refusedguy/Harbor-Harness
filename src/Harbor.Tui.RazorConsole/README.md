# Harbor.Tui.RazorConsole

Experimental TUI renderer using RazorConsole — Razor templates compiled to terminal output. Lets you write `.razor` files that render TUI views. Proof-of-concept for declarative TUI authoring.

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)
- `RazorConsole.Core`
- `Spectre.Console`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `RazorConsoleTuiRenderer` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `RazorConsoleTuiRenderer` — renders `.razor` views to terminal
- `ChatView.razor` — sample chat view

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, RazorConsoleTuiRenderer>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## Experimental status

This is a research project. RazorConsole's terminal rendering model is fundamentally stream-based, which fights with the cursor-addressable model that good TUIs need. May be removed in a future release.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
