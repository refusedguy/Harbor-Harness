# Harbor.Ui.Framework.Abstractions

Contracts and abstractions for the Harbor UI Framework — configuration, diagnostics, navigation, and shell chrome. Renderer-agnostic; desktop GUIs and terminal hosts both reference this project.

## Layer

**Presentation (framework contracts).** Innermost UI Framework project. Depends on `Harbor.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` only.

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `Commands/` | `SlashCommandCatalog` + `SlashCommandDefinition` — the single slash-command registry every palette, autocomplete and help surface derives from (issue #462). |
| `Configuration/` | `ICommonConfigReader` — reads provider/model overrides from common config. |
| `Diagnostics/` | `IDiagnosticsPanel`, `InMemoryDiagnosticsPanel`, `DiagnosticEntry`, `DiagnosticsPanelLoggerProvider` (ILoggerProvider that forwards logs into the panel). |
| `Navigation/` | `IContentHost`, `IShellChrome`, `IWorkspaceCommands`, `OverlayIds` (palette, settings, diff, token usage, provider browser, model picker, sessions flyout, focus session). |
| `ToolCallState.cs` | `ToolCallState` + `ToolCallStateExtensions.IsTerminal()` — the single tool-call lifecycle vocabulary (issue #567). |

## Public API summary

- **`SlashCommandCatalog`**: `All` (every command), `Invocations` (`/help`, `/exit`, … — the advertised vocabulary), `Find(text)` (alias- and case-insensitive lookup), plus the palette group constants. `SlashCommandDispatcher` binds its handlers to these entries and throws at construction if a command has no handler, so a command can never be advertised without being runnable.
- **`ICommonConfigReader.TryReadProviderModelAsync()`**: returns `(ProviderId?, ModelId?)` from common config.
- **`IDiagnosticsPanel`**: `Log(level, category, message)`, `GetRecent(max)`, `Clear()`.
- **`DiagnosticsPanelLoggerProvider` / `DiagnosticsPanelLogger` : `ILoggerProvider` / `ILogger` — bridges `Microsoft.Extensions.Logging` into the diagnostics panel.
- **`ToolCallState`**: `Pending`, `Running`, `Success`, `Error`, `Cancelled`, `TimedOut`. The union of the three enums it replaced (`ViewModels.ToolCallStatus`, `Projection.ToolCallStatus`, `CellForge.ToolCallStatus`), which had three different member sets and no terminal-state rule. `IsTerminal()` classifies once instead of per call site. Every switch over the enum names all members explicitly and throws on the unnamed domain, and a guard test walks `Enum.GetValues<ToolCallState>()` — C# cannot express named-member-only exhaustiveness for an enum (CS8524 is unconditional), so the test is the gate that makes the next member a failure rather than a spinner (#567).
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
- The registry covers the CLI slash dispatcher's vocabulary. Renderer-local affordances (CellForge's `/vim` leader toggle, `/panels`) are deliberately not in it — they are not dispatchable everywhere, so advertising them globally would reintroduce the drift this registry removed.
