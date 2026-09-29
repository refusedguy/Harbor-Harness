# Harbor.Ui.Framework.Projection

Renderer-agnostic projection of `UiState` into `UiScreenModel`. The bridge between the TEA state machine and whatever renderer (Avalonia, WPF, Spectre, Terminal.Gui) paints the screen.

## Layer

**Presentation (framework projection).** Depends on `Harbor.Ui.Framework.State` and `Harbor.Ui.Framework.Abstractions`.

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `Projection/` | `IUiProjector`, `DefaultUiProjector`, `IUiViewport`, `UiScreenModel` record hierarchy (`UiHeaderModel`, `UiTranscriptModel`, `UiInputModel`, `UiStatusBarModel`), `UiRenderedLine`, `StyledSpan`, `RgbColor` |
| `Projection/StatusBarFacts.cs` | `StatusBarFacts` — the status bar's cells (chrome, status, agent, tokens, cost, scroll) derived from `UiState` exactly once (#488). |
| `Rendering/` | `ChatStreamingPresenter` — reads `SessionStatus` off `UiState` (#687). |

## Public API summary

- **`IUiProjector.Project(UiState)`**: pure function returning `UiScreenModel`.
- **`DefaultUiProjector`**: full projection implementation; also exposes `State`, `Screen`, `Transcript`, `Lines`, `BaseRendered`, `BaseBlocks`, `IsStreaming`, `ThinkBuf` for renderer binding.
- **`StatusProjector`**: `ProjectStatusBar(UiState)` and `ProjectFooter(UiState)` — partial projections for chrome regions. It only *packs* cells; it never formats one.
- **`StatusBarFacts.Of(UiState)`**: the single derivation of the status-bar cells every renderer reads (#488) — including token and cost text, whose rules live in `Harbor.Ui.Framework.State.StatusBarText`.
- **`UiScreenModel` records**: `UiMessageBlock`, `UiSpanStyle`, `MessageRenderPhase`, etc. The tool-call lifecycle vocabulary is the shared `ToolCallState` enum from `Harbor.Ui.Framework.Abstractions` (#567) — this project used to declare a fourth, consumer-less `ToolCallStatus`.
- **`ChatStreamingPresenter`**: `DeriveStatus(UiState)` — a READ of `ChatDomainState.SessionStatus` (#687). It used to re-derive the status from the transcript's last line's role, which was a second opinion on a run the core already knew the end of and disagreed with the core in both directions (a clean run ending on a tool result read as idle; a user-cancelled run read as done). The decision now happens once, in `ChatAppReducer`; the guard is `SessionStatusSourceRule` in `tests/Harbor.Architecture.Tests/`.

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
