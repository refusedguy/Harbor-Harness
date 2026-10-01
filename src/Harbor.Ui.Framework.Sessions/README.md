# Harbor.Ui.Framework.Sessions

Session orchestration for the Harbor UI Framework — session factory, manager, switcher, git tracker, and chat view binder. Turns raw `Session` domain objects into bound `UiStore` instances ready for rendering.

## Layer

**Presentation (framework sessions).** Depends on `Harbor.Ui.Framework.State`, `Harbor.Ui.Framework.Services`, `Harbor.Ui.Framework.ViewModels`, `Harbor.Ui.Framework.Abstractions`, and `Harbor.Abstractions`.

## What's in it

| File | Purpose |
|------|---------|
| `Sessions/ISessionManager.cs` | `ISessionManager` — composite of the three narrow contracts below (back-compat facade). |
| `Sessions/ISessionQueries.cs` | `ISessionQueries` — read-only: `Active`, `ActiveContext`, `GetContext`, `GetGitInfo`. |
| `Sessions/ISessionLifecycle.cs` | `ISessionLifecycle` — create/open/branch/delete/rename, git refresh, config rebind. |
| `Sessions/ISessionStatusTracker.cs` | `ISessionStatusTracker` — per-session status + message-count pushes + events. |
| `Sessions/SessionManager.cs` | `SessionManager` — thin delegation facade over the three services below. |
| `Sessions/SessionEventRouter.cs` | `SessionEventRouter` — per-session contexts (live + tombstoned) + active pointer. |
| `Sessions/SessionLifecycleService.cs` | `SessionLifecycleService` — session lifecycle orchestration. |
| `Sessions/SessionStatusService.cs` | `SessionStatusService` — `ISessionStatusTracker` adapter over `SessionStatusTracker`. |
| `Sessions/SessionOptionalFactories.cs` | `SessionOptionalFactories` — explicit Func-factories for host-only deps (no Service Locator). |
| `Sessions/SessionFactory.cs` | `SessionFactory` — creates default and new sessions; resolves provider/model/agent from config; **forks** through the core's `SessionForkService` via the `ISessionForker` port (issue #670 — it used to carry its own copy of the fork, which had drifted). |
| `Sessions/SessionSwitcher.cs` | `SessionSwitcher` — opens a session and hydrates its `UiStore` into a target store. |
| `Sessions/SessionContext.cs` | `SessionContext` — binds a `Session`, `UiStore`, status, git branch, and hydration flag together. |
| `Sessions/SessionGitTracker.cs` | `SessionGitTracker` — refreshes git status for a session directory. |
| `Sessions/IChatViewBinder.cs` | `IChatViewBinder` — rebinds a `UiStore` to a chat view after session switch. |

## Public API summary

- **`SessionFactory`**: `CreateDefaultAsync`, `CreateNewAsync`, `CreateBranchAsync` (delegates to `ISessionForker` — the core fork, not a local copy), `ResolveProviderModelFromConfigAsync`, `ResolveAgentDefinitionAsync`, `MessageToChatLine`.
- **`SessionManager`**: thin facade — `Active`, `ActiveContext`, `GetContext`, `GetStatus`, `SetStatus`, `NotifyMessageCount`, `GetGitInfo`, `RefreshGitInfo`, lifecycle ops, events for status/count changes. All logic lives in the services below.
- **`SessionEventRouter`**: live + tombstoned contexts, `GetOrCreateContext`, `ParkContext`, `ActiveContext`.
- **`SessionLifecycleService`**: `EnsureDefaultSessionAsync`, `New/Open/Branch/Delete/RenameSessionAsync`, `RebindFromCommonConfigAsync`.
- **`SessionStatusService`**: `ISessionStatusTracker` over the shared `SessionStatusTracker` singleton.
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
