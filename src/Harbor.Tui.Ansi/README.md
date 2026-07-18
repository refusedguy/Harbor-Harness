# Harbor.Tui.Ansi

Default streaming TUI renderer using raw ANSI escape codes. AOT-compatible. Writes assistant text deltas, tool-call borders, and errors in red. The renderer used by `harbor` CLI when no other is selected.

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `AnsiTuiRenderer` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `AnsiCodes` — ANSI escape sequence constants
- `AnsiTuiRendererOptions` — color toggle, UTF-8 enforcement

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, AnsiTuiRenderer>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## Terminal detection

Detects `NO_COLOR`, `TERM=dumb`, and piped stdout. Falls back to `PlainTuiRenderer` behavior (no colors, no cursor movement) when colors are disabled.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
