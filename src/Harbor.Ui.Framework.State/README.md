# Harbor.Ui.Framework.State

TEA (The Elm Architecture) state machine and panel system for the Harbor UI Framework — `UiStore`, `UiState`, `AppMsg`/`ChatAppMsg`, `AppReducer`/`ChatAppReducer`, `TuiEffect`, and the panel registry.

The state is split by concern into two typed parts with **no flat forwarding
surface** (#33/T4, #364):

| Part | Type | Owns |
|------|------|------|
| `UiState.Ui` | `TerminalUiState` | input, focus, scroll, viewport, panels, quit — zero chat/AI concerns |
| `UiState.Chat` | `ChatDomainState` | transcript, streaming buffers, agent lifecycle, costs, sessions |

Read `state.Ui.Input` / `state.Chat.Cost` directly; there is no `state.Input` /
`state.Cost` shortcut. No `ImmutableDictionary<string, object?>` extension bag
exists (it was rejected for boxing + IL2xxx under NativeAOT) — extension is typed
composition only.

## Layer

**Presentation (framework state).** Innermost UI Framework project after Abstractions. Depends on `Harbor.Ui.Framework.Abstractions` and `Harbor.Abstractions`.

## What's in it

| Subfolder / File | Purpose |
|------------------|---------|
| `State/UiState.cs` | Root state record: the `Ui` + `Chat` composition, `Revision`, and the fold helpers (`AddLine`, `SetLine`, `SetInput`, `SetFocus`, `SetScroll`, `ClearTranscript`). |
| `TerminalUiState.cs` / `ChatDomainState.cs` | The two typed parts of the composition. |
| `State/AppMsg.cs` | **Generic** message union: `KeyInput`, `InputText`, `Quit`, `Reset`, `Viewport`, `HistoryMeasured`, `ScrollResetToTail`, `ScrollClamp`, `TogglePanel`, `FocusPanel`, `CyclePanelFocus`, `ResizePanel`, `SeedPanels`, `SetPanelCursor`, `SetPanelDirectory`. |
| `State/ChatAppMsg.cs` | **Harbor chat** arms (`AppMsg` subtype): `Agent`, `AgentStarted`, `AgentEnded`, `StatusChanged`, `AppendLine`, `HydrateSession`, `ConfigureRuntime`, `SyncSessions`. |
| `State/AppReducer.cs` | **Generic** reducer: `Update(UiState, AppMsg, IAppReducerPlugin?)` + panel/scroll/input/focus helpers. Declares `ReduceResult` and the `IAppReducerPlugin` extension point. |
| `State/ChatAppReducer.cs` | **Harbor chat** extension: `Reduce(UiState, AgentEvent)`, `Update(UiState, AppMsg)`, plus the `ChatAppReducerPlugin` adapter for `IAppReducerPlugin`. |
| `State/UiStore.cs` | `UiStore` — the Elm-style store that owns state, dispatches messages, and runs `TuiEffect`s via `ITuiEffectRunner`. |
| `AppState.cs` | Legacy flat app state (`Harbor.Ui.Framework.Reducers.AppReducer` / `AppStore`) kept only for the not-yet-migrated shell-chrome consumers. **Not** on the TEA read path. |
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

- **`UiStore`**: `State`, `Dispatch(AppMsg)`, `Bind(UiEffectRunner)`, `Changed` events for state changes.
- **`AppReducer.Update`**: generic pure reducer; the optional `IAppReducerPlugin` argument is the extension point (claim → generic → plugin post-fold, in that order).
- **`ChatAppReducer.Update`**: the composed entry point used by `UiStore.Dispatch` = `AppReducer.Update` + the chat plugin.
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
| `Harbor.Ui.Framework.Rendering` | BCL-only key vocabulary (`UiKeyDto`) via `KeyEventAdapter` (issue #33 T1) |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/`.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.State/Harbor.Ui.Framework.State.csproj
```

## Known limitations

- `UiStore` is not thread-safe; the renderer must serialize dispatches.
- `PanelRegistry` does not persist panel sizes across app restarts.
