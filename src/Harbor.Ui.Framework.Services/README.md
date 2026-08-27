# Harbor.Ui.Framework.Services

Platform service abstractions and implementations for the Harbor UI Framework — dispatcher, dialogs, theme, toast, file picker, overlays, git, and event-bus integration.

## Layer

**Presentation (framework services).** Depends on `Harbor.Ui.Framework.State`, `Harbor.Ui.Framework.Reducers`, `Harbor.Ui.Framework.Abstractions`, and `Harbor.Abstractions`.

## What's in it

| Subfolder / File | Purpose |
|------------------|---------|
| `EventBusAppStoreDispatcher.cs` | `EventBusAppStoreDispatcher` — bridges `IEventBus` into `AppStore.Dispatch`. |
| `IRenderEngine.cs` | `IRenderEngine` — renderer lifecycle contract. |
| `Overlays/OverlayController.cs` | `OverlayController` — manages overlay open/close/stack state. |
| `Services/IDialogService.cs` | `IDialogService` — `ConfirmAsync`, `PromptAsync`, `AlertAsync`. |
| `Services/IDispatcherAdapter.cs` | `IDispatcherAdapter` — platform dispatcher (`Post`, `Invoke`, `Bind`/`Unbind` store). |
| `Services/IFilePicker.cs` | `IFilePicker` — `PickFilesAsync`, `PickSaveFileAsync`, `PickFolderAsync`. |
| `Services/IOverlayStack.cs` | `IOverlayStack` — `Push`, `PopTop`, `Current`, `Stack`. |
| `Services/IThemeService.cs` | `IThemeService` — theme apply/toggle/dark/light/HDS variants. |
| `Services/IToastService.cs` | `IToastService` — `Show(message, kind)` with `ToastAdded` event. |
| `Services/GitService.cs` | `GitService` — reads git status (`GetGitStatus`) for session metadata. |
| `Services/GitSessionInfo.cs` | `GitSessionInfo` record (`Branch`, `IsDirty`, `DirtyCount`, `LastCommit`). |
| `Services/SessionStatusTracker.cs` | `SessionStatusTracker` — tracks per-session status and message count, raises events. |

## Public API summary

- **`EventBusAppStoreDispatcher`**: `Start()`, `DisposeAsync` — pumps `AgentEvent`s into the app store.
- **`OverlayController`**: `Register(id, setter)`, `Open(id)`, `Close(id)`, `CloseTop()`, `HasOverlay`.
- **Platform services**: `IDialogService`, `IDispatcherAdapter`, `IFilePicker`, `IThemeService`, `IToastService`, `IOverlayStack` — all renderer-agnostic contracts.
- **`GitService`**: `GetGitStatus(directory)` → `GitSessionInfo`.
- **`SessionStatusTracker`**: `Get/SetStatus`, `NotifyMessageCount`, events for status/count changes.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging.Abstractions` | Logging |
| `Microsoft.Extensions.Logging` | Logging (concrete) |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | Domain types |
| `Harbor.Ui.Framework.State` | State records |
| `Harbor.Ui.Framework.Reducers` | Reducers |
| `Harbor.Ui.Framework.Abstractions` | Contracts |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/` and app-level tests.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.Services/Harbor.Ui.Framework.Services.csproj
```

## Known limitations

- `GitService` shells out to `git`; behavior depends on host git installation and repo state.
- `SessionStatusTracker` is in-memory only — not persisted across app restarts.
