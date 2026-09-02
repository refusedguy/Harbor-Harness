# Harbor.Ui.Framework.Sessions

Session orchestration for the Harbor UI Framework — session factory, manager, switcher, git tracker, and chat view binder. Turns raw `Session` domain objects into bound `UiStore` instances ready for rendering.

## Layer

**Presentation (framework sessions).** Depends on `Harbor.Ui.Framework.State`, `Harbor.Ui.Framework.Services`, `Harbor.Ui.Framework.ViewModels`, `Harbor.Ui.Framework.Abstractions`, and `Harbor.Abstractions`.

## What's in it

| File | Purpose |
|------|---------|
| `Sessions/ISessionManager.cs` | `ISessionManager` — create, open, close, list, switch sessions. |
| `Sessions/SessionManager.cs` | `SessionManager` — in-memory session lifecycle with status/message-count events. |
| `Sessions/SessionFactory.cs` | `SessionFactory` — creates default, new, or branched sessions; resolves provider/model/agent from config. |
| `Sessions/SessionSwitcher.cs` | `SessionSwitcher` — opens a session and hydrates its `UiStore` into a target store. |
| `Sessions/SessionContext.cs` | `SessionContext` — binds a `Session`, `UiStore`, status, git branch, and hydration flag together. |
| `Sessions/SessionGitTracker.cs` | `SessionGitTracker` — refreshes git status for a session directory. |
| `Sessions/IChatViewBinder.cs` | `IChatViewBinder` — rebinds a `UiStore` to a chat view after session switch. |

## Public API summary

- **`SessionFactory`**: `CreateDefaultAsync`, `CreateNewAsync`, `CreateBranchAsync`, `ResolveProviderModelFromConfigAsync`, `ResolveAgentDefinitionAsync`, `MessageToChatLine`.
- **`SessionManager`**: `Active`, `ActiveContext`, `OpenAsync`, `CloseAsync`, `GetContext`, `GetStatus`, `SetStatus`, `NotifyMessageCount`, events for status/count changes.
- **`SessionSwitcher`**: `OpenAsync(session, targetStore)` — loads session and binds it.
- **`SessionContext`**: `Session`, `Store`, `Status`, `GitBranch`, `GitIsDirty`, `StoreWasHydrated`, `MetaLine`.
- **`IChatViewBinder.Rebind(UiStore)`**: reattaches chat view after a session switch.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging.Abstractions` | Logging |
| `Microsoft.Extensions.Logging` | Logging (concrete) |
| `CSharpFunctionalExtensions` | Result types |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | `Session`, `AgentEvent`, `AgentDefinition` |
| `Harbor.Ui.Framework.State` | `UiStore`, state records |
| `Harbor.Ui.Framework.Services` | `SessionStatusTracker`, `GitService` |
| `Harbor.Ui.Framework.ViewModels` | View models |
| `Harbor.Ui.Framework.Abstractions` | Contracts |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/`.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.Sessions/Harbor.Ui.Framework.Sessions.csproj
```

## Known limitations

- Session state is in-memory; durability relies on the storage backend (`Harbor.Storage.*`), not this project.
- `SessionSwitcher.OpenAsync` is synchronous from the caller's perspective but does async I/O internally.
