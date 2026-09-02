# Harbor.Ui.Framework.Reducers

Pure reducer functions for Harbor UI state. Each reducer is a stateless switch expression that takes an `AgentEvent` (or `UiMsg`) plus current state and returns new state. No side effects, no services.

## Layer

**Presentation (framework reducers).** Depends on `Harbor.Abstractions` and `Harbor.Ui.Framework.State` only.

## What's in it

| File | Purpose |
|------|---------|
| `AppReducer.cs` | `AppState Reduce(AgentEvent, AppState)` — top-level reducer for the app store. |
| `AppStore.cs` | `AppStore` — thin wrapper holding `AppState` and dispatching events to `AppReducer`. |
| `ChatViewReducer.cs` | `ChatViewState Reduce(AgentEvent, ChatViewState)` — chat transcript reducer. |
| `ChromeReducer.cs` | `ChromeViewState Reduce(AgentEvent, ChromeViewState)` — navigation, toasts, modals reducer. |
| `SessionsReducer.cs` | `SessionsViewState Reduce(AgentEvent, SessionsViewState)` — sessions list reducer. |

## Public API summary

- **`AppReducer.Reduce(...)`**: handles all `AgentEvent` subtypes and returns a new `AppState`.
- **`ChatViewReducer.Reduce(...)`**: updates `Lines`, `IsStreaming`, `IsThinking`, `StreamingBuffer`, `ToolCalls`.
- **`ChromeReducer.Reduce(...)`**: manages `NavigationStack`, `ActiveModal`, `Toasts`.
- **`SessionsReducer.Reduce(...)`**: manages `Sessions` list and `ActiveSessionId`.
- **`AppStore`**: `Dispatch(AgentEvent)` applies the reducer and raises `StateChanged`.

## Dependencies

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | `AgentEvent`, domain types |
| `Harbor.Ui.Framework.State` | `AppState`, `ChatViewState`, `ChromeViewState`, `SessionsViewState` |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/`.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.Reducers/Harbor.Ui.Framework.Reducers.csproj
```

## Known limitations

- Reducers are synchronous and CPU-only — no async I/O, no service lookups.
- `AppStore` is a simple event publisher; thread-safety is the renderer's responsibility.
