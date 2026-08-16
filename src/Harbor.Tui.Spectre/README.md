# Harbor.Tui.Spectre

TUI renderer using Spectre.Console — rich panels, tables, markup, and color. Best for one-shot commands and report-style output. For interactive REPL use `Harbor.Tui.SpectreTui` or `Harbor.Tui.Spectre.Fullscreen`.

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)
- `Spectre.Console`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `SpectreTuiRenderer` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `SpectreTuiRenderer` — renders UiState to Spectre panels
- `SpectreMarkupConverter` — Markdown -> Spectre markup

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, SpectreTuiRenderer>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## When to use

Set `HARBOR_TUI=spectre` for richer panels/tables in non-interactive mode. For interactive use (input box, live updates) prefer SpectreTui or Spectre.Fullscreen.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
