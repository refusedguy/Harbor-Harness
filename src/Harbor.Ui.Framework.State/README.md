# Harbor.Ui.Framework.State

TEA (The Elm Architecture) state machine and panel system for the Harbor UI Framework — `UiStore`, `UiState`, `UiReducer`, `UiMsg`, `TuiEffect`, and the panel registry.

## Layer

**Presentation (framework state).** Innermost UI Framework project after Abstractions. Depends on `Harbor.Ui.Framework.Abstractions` and `Harbor.Abstractions`.

## What's in it

| Subfolder / File | Purpose |
|------------------|---------|
| `State/UiState.cs` | Root state record: `Lines`, `Active`, `PendingStreamText`, `PendingStreamThink`, `Cost`, `Model`, `Provider`, `AgentName`, navigation, modals, toasts. |
| `State/UiMsg.cs` | Message discriminated union: `Agent(AgentEvent)`, `KeyInput(ChatAction)`, `Viewport`, `TogglePanel`, `FocusPanel`, `ResizePanel`, etc. |
| `State/UiReducer.cs` | `Reduce(UiState, AgentEvent)` and `Update(UiState, UiMsg)` — pure reducers with panel/viewport helpers. |
| `State/UiStore.cs` | `UiStore` — the Elm-style store that owns state, dispatches messages, and runs `TuiEffect`s via `ITuiEffectRunner`. |
| `State/AppState.cs` | Top-level app state aggregating `ChatViewState`, `ChromeViewState`, `SessionsViewState`. |
| `State/ChatViewState.cs` | Chat transcript state: `Lines`, `ToolCalls`, `IsStreaming`, `IsThinking`, `StreamingBuffer`, `PendingStreaming`. |
| `State/ChromeViewState.cs` | Chrome state: `ActiveSessionId`, `NavigationStack`, `ActiveModal`, `Toasts`, plus helper reducers. |
| `State/SessionsViewState.cs` | Sessions list state: `Sessions`, `ActiveSessionId`, `IsLoading`. |
| `Panels/` | `IPanelRegistry`, `PanelRegistry`, `IPanelProvider`, `TuiPanel`, `TuiPanelPlacement`, `TuiPanelState`, `PanelContext`, `ITuiPanelPlugin`. |
| `State/AsyncData.cs` | `AsyncData<T>` struct: `Idle`, `Loading`, `Success`, `Error`, `Refreshing`. |
| `State/AsyncFeed.cs` | `AsyncFeed<T>` — disposable async data source with `RefreshAsync`. |
| `State/ChatAction.cs` | `ChatAction` enum + `ChatCommands` slash commands + `FocusMode`. |
| `State/ChatKeyMap.cs` | `ChatKeyMap` — binds `UiKey` → `ChatAction`. |
| `State/InputModel.cs` | `InputModel` — text input state with history navigation. |
| `State/ShellStatus.cs` | `ShellStatus` — observable status bar model. |
| `State/TuiEffectHost.cs` | `TuiEffectHost` — bridges `UiStore` into `ITuiEffectRunner`. |
| `State/UiKey.cs` | `UiKeyCode`, `KeyModifierSet`, `UiKey` struct. |
| `State/ChunkedBuffer.cs` | `ChunkedBuffer` — immutable streaming text buffer with `Append`/`Materialize`. |
| `State/StreamingSync.cs` | `StreamingSync` — decides when to flush streaming text to the renderer. |

## Public API summary

- **`UiStore`**: `State`, `Dispatch(UiMsg)`, `Bind(UiEffectRunner)`, events for state changes.
- **`UiReducer.Reduce/Update`**: pure functions returning new `UiState`.
- **`PanelRegistry`**: `Register`, `Unregister`, `GetVisible`, `GetVisibleByPlacement`, `GetState`, `GetSize`, `SetSize`, `Toggle`, `Focus`, `CycleFocus`.
- **`IPanelProvider`**: `Id`, `Title`, `DefaultPlacement`, `DefaultSize`, `Build(ctx)`, `OnKey`.
- **`AsyncFeed<T>` / `AsyncData<T>`**: async data primitives with status tracking.
- **`ChatKeyMap`**: `Resolve(UiKey) → ChatAction`, `Get(ChatAction) → Entry`.
- **`ChunkedBuffer`**: immutable streaming buffer for progressive text rendering.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging.Abstractions` | Logging |
| `CommunityToolkit.Mvvm` | `ObservableValidator` / `ObservableObject` |
| `CSharpFunctionalExtensions` | `Result` types |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | `AgentEvent`, `SessionId`, `KeyPress` |
| `Harbor.Ui.Framework.Abstractions` | Contracts |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/`.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.State/Harbor.Ui.Framework.State.csproj
```

## Known limitations

- `UiStore` is not thread-safe; the renderer must serialize dispatches.
- `PanelRegistry` does not persist panel sizes across app restarts.
