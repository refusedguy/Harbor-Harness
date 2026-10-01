# Harbor.Terminal.Abstractions

Terminal UI contracts — renderer, view, view-model, plugin, and navigation abstractions. Used **only** by terminal renderers (Ansi, Spectre, Plain, TerminalGui, RazorConsole, Termina, Sixel, Notifications, SpectreTui, Spectre.Fullscreen). Desktop GUIs (Avalonia/WPF/MAUI/Blazor) consume `Harbor.Ui.Framework` directly and do **not** reference this project.

## Layer

**Presentation (terminal abstraction).** Sits alongside `Harbor.Ui.Framework` (generic UI) but is terminal-specific. Depends on `Harbor.Abstractions` and `CommunityToolkit.Mvvm` only.

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `Views/` | `ITuiView`, `TuiViewBase<TVm>`, `TuiViewPlacement`; built-in views (`StatusBarView`, `ChatHistoryView`, `InputView`, `DiffPreviewView`) |
| `ViewModels/` | `ITuiViewModel`, builtin view models (`StatusBarViewModel`, `ChatHistoryViewModel`, `InputViewModel`, `DiffPreviewViewModel`) |
| `Renderers/` | `ITuiRenderer`, `BaseTuiRenderer`, `ITuiRenderContext` (`TuiColor`, `TuiStyle`), GFM table renderer |
| `Plugins/` | `ITuiPlugin` — terminal-side plugin contract |
| `Navigation/` | `ViewRegistry`, `ViewModelRegistry` |
| `Rendering/` | `StyledSpan`, `RgbColor`, table-cell formatting helpers |

## Public API summary

- **Renderer**: `ITuiRenderer` (initialize, render, readline, write, clear, dispose) and `BaseTuiRenderer` partial implementation.
- **Views/ViewModels**: `TuiViewBase<T>` with `Id`, `DisplayName`, `Placement`, `RenderAsync`; builtin VMs via CommunityToolkit.Mvvm `ObservableObject`.
- **Plugin**: `ITuiPlugin` — a **closed seam (#564)**: `Name`, `Version`, `Description`, `RegisterTui`, and nothing ever calls `RegisterTui`, so it is collected and never rendered. It has no `RegisterPanels`. For a plugin-contributed panel the contract is `ITuiPanelPlugin.RegisterPanels(IPanelRegistry)`, which lives in `Harbor.Ui.Framework.State` — a different assembly.
- **Render context**: `ITuiRenderContext` with color/style abstractions (renderer-specific implementations).

## Dependencies

| Package | Purpose |
|---------|---------|
| `CommunityToolkit.Mvvm` | `ObservableObject` for builtin view models |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | `AgentEvent`, `KeyPress`, domain types |

## Tests

No dedicated test project. Validated by `tests/Harbor.Tui.Tests/`, `tests/Harbor.Tui.CellForge.Tests/`, and per-renderer E2E tests.

## Build

```bash
dotnet build src/Harbor.Terminal.Abstractions/Harbor.Terminal.Abstractions.csproj
```

## Known limitations

- Breaking namespace change from `Harbor.Tui.Abstractions` → `Harbor.Terminal.Abstractions` (Task A2, R6). Consumers must update `using` directives.
- NO Spectre.Console / Terminal.Gui / GUI framework types — renderers add those themselves.
