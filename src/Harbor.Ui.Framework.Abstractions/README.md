# Harbor.Ui.Framework.Abstractions

Contracts and abstractions for the Harbor UI Framework — configuration, diagnostics, navigation, and shell chrome. Renderer-agnostic; desktop GUIs and terminal hosts both reference this project.

## Layer

**Presentation (framework contracts).** Innermost UI Framework project. Depends on `Harbor.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` only.

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `Configuration/` | `ICommonConfigReader` — reads provider/model overrides from common config. |
| `Diagnostics/` | `IDiagnosticsPanel`, `InMemoryDiagnosticsPanel`, `DiagnosticEntry`, `DiagnosticsPanelLoggerProvider` (ILoggerProvider that forwards logs into the panel). |
| `Navigation/` | `IContentHost`, `IShellChrome`, `IWorkspaceCommands`, `OverlayIds` (palette, settings, diff, token usage, provider browser, model picker, sessions flyout, focus session). |

## Public API summary

- **`ICommonConfigReader.TryReadProviderModelAsync()`**: returns `(ProviderId?, ModelId?)` from common config.
- **`IDiagnosticsPanel`**: `Log(level, category, message)`, `GetRecent(max)`, `Clear()`.
- **`DiagnosticsPanelLoggerProvider` / `DiagnosticsPanelLogger` : `ILoggerProvider` / `ILogger` — bridges `Microsoft.Extensions.Logging` into the diagnostics panel.
- **`IShellChrome` / `IWorkspaceCommands`**: navigation contracts for desktop shell integration.
- **`OverlayIds`**: string constants for builtin overlay identifiers.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging.Abstractions` | ILogger contracts |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | `AgentEvent`, `KeyPress` |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/` and app-level tests.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.Abstractions/Harbor.Ui.Framework.Abstractions.csproj
```

## Known limitations

- `OverlayIds` are string constants, not a strongly-typed enum — prone to typos at call sites.
- `InMemoryDiagnosticsPanel` capacity is fixed at construction; no auto-resize.
