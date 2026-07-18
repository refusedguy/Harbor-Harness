# Harbor.Tui.Termina

Experimental TUI renderer using Termina — a source-generator-based TUI framework. Declarative UI definition via attributes; the generator emits the rendering code.

## Layer

Presentation — TUI renderer. References `Harbor.Abstractions` (Domain) + `Harbor.Tui.Abstractions` (Presentation contracts).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Tui.Abstractions` (Presentation contracts: `ITuiRenderer`, `UiState`, `UiEvent`)
- `Termina`
- `Termina.Generators` (source generator, PrivateAssets=all)
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `TerminaTuiRenderer` — implements `ITuiRenderer` from Harbor.Tui.Abstractions
- `TerminaTuiRenderer` — Termina-based renderer
- `ChatView` — declarative view definition

## Usage

Registered via DI in the composition root:

```csharp
services.AddSingleton<ITuiRenderer, TerminaTuiRenderer>();
```

Then `AgentLoop` emits `AgentEvent`s; the active `ITuiReducer` folds them into a `UiState`; the renderer is called once per state change.

## Build note

The csproj removes the `Termina.Generators` analyzer from `CoreCompile` to avoid a conflict with .NET 10's analyzer pipeline. The generator still runs at design-time for IntelliSense.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
