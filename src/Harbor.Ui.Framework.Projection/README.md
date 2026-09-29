# Harbor.Ui.Framework.Projection

Renderer-agnostic projection of `UiState` into `UiScreenModel`. The bridge between the TEA state machine and whatever renderer (Avalonia, WPF, Spectre, Terminal.Gui) paints the screen.

## Layer

**Presentation (framework projection).** Depends on `Harbor.Ui.Framework.State` and `Harbor.Ui.Framework.Abstractions`.

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `Projection/` | `IUiProjector`, `DefaultUiProjector`, `IUiViewport`, `UiScreenModel` record hierarchy (`UiHeaderModel`, `UiTranscriptModel`, `UiInputModel`, `UiStatusBarModel`), `UiRenderedLine`, `StyledSpan`, `RgbColor` |
| `Projection/StatusBarFacts.cs` | `StatusBarFacts` — the status bar's cells (chrome, status, agent, tokens, cost, scroll) derived from `UiState` exactly once (#488). |
| `Projection/PaletteRows.cs` | `PaletteRows` / `PaletteRow` — the shared Ctrl+P command-palette rows (slash commands + panels + sessions) with untrusted text scrubbed of ANSI escape sequences and control characters, so every shell renders the same inert rows (#554). |
| `Rendering/` | `ChatStreamingPresenter` — derives `SessionStatus` and streaming flags from `UiState`. |

## Public API summary

- **`IUiProjector.Project(UiState)`**: pure function returning `UiScreenModel`.
- **`DefaultUiProjector`**: full projection implementation; also exposes `State`, `Screen`, `Transcript`, `Lines`, `BaseRendered`, `BaseBlocks`, `IsStreaming`, `ThinkBuf` for renderer binding.
- **`StatusProjector`**: `ProjectStatusBar(UiState)` and `ProjectFooter(UiState)` — partial projections for chrome regions. It only *packs* cells; it never formats one.
- **`StatusBarFacts.Of(UiState)`**: the single derivation of the status-bar cells every renderer reads (#488) — including token and cost text, whose rules live in `Harbor.Ui.Framework.State.StatusBarText`.
- **`UiScreenModel` records**: `UiMessageBlock`, `UiSpanStyle`, `MessageRenderPhase`, `ToolCallStatus`, etc.
- **`ChatStreamingPresenter`**: `DeriveStatus(UiState)` helper.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging.Abstractions` | Logging |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | Domain types |
| `Harbor.Ui.Framework.State` | `UiState`, `UiStore`, state records |
| `Harbor.Ui.Framework.Abstractions` | Contracts |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/`.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.Projection/Harbor.Ui.Framework.Projection.csproj
```

## Known limitations

- Projection is synchronous and pure — no async I/O, no side effects.
- `DefaultUiProjector` is a single pass; incremental updates during streaming are handled by the renderer re-invoking `Project`.
