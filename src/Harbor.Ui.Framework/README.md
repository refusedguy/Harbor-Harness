# Harbor.Ui.Framework

Meta-package that references all `Harbor.Ui.Framework.*` sub-modules. Produces no code of its own — consumers reference this single project to get the full TEA/ELM UI framework (State, ViewModels, Services, Projection, Sessions, Abstractions).

## Layer

**Presentation (framework meta-package).** Aggregator only; the real code lives in sibling projects.

## What's in it

| ProjectReference | Purpose |
|------------------|---------|
| `Harbor.Ui.Framework.Abstractions` | Contracts: config reader, diagnostics panel, shell chrome, navigation |
| `Harbor.Ui.Framework.State` | TEA state machine: `UiStore`, `UiState`, `UiReducer`, `UiMsg`, panels |
| `Harbor.Ui.Framework.Services` | Platform services: dispatcher, dialogs, theme, toast, file picker, overlays, git |
| `Harbor.Ui.Framework.ViewModels` | Shared VMs: chat lines, tool calls, diff, session rows, token usage |
| `Harbor.Ui.Framework.Projection` | Renderer-agnostic projection of `UiState` → `UiScreenModel` |
| `Harbor.Ui.Framework.Sessions` | Session orchestration: factory, manager, switcher, git tracker |

## Public API summary

None of its own. Exposes the combined public surface of the sub-modules listed above.

## Dependencies

Inherited from the sub-modules. See individual project READMEs for details.

## Tests

`tests/Harbor.Ui.Framework.Tests/` — integration tests for the framework surface.

## Build

```bash
dotnet build src/Harbor.Ui.Framework/Harbor.Ui.Framework.csproj
```

## Known limitations

- This is a pure aggregation project; adding/removing sub-modules requires updating the `.csproj` ProjectReferences and this README.
- All sub-modules share `InternalsVisibleTo` with the same set of app assemblies (`Avalonia`, `Wpf`, `Maui`, `Blazor`, `Cli`, terminal hosts).
