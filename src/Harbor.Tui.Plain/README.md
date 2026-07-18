# Harbor.Tui.Plain

Plain-text TUI renderer — no ANSI, no colors, no cursor movement. Designed for piped output, CI logs, screen readers, and accessibility. Output is a linear stream suitable for `>` redirection.

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)


## Public API

- `PlainTuiRenderer` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `PlainTuiRenderer` — minimal stream writer

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, PlainTuiRenderer>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## When to use

Set `HARBOR_TUI=plain` or pipe stdout to a file. The renderer emits one line per assistant delta and bracketed `[tool:start]`/`[tool:end]` markers for tool calls.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
