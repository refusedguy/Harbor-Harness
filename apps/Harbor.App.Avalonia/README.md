# Harbor.App.Avalonia

Standalone desktop GUI for [Harbor](..) — the .NET 10 AI agent harness.
Built on [Avalonia UI 12](https://avaloniaui.net/) + Skia + [AvaloniaEdit 12](https://github.com/AvaloniaUI/AvaloniaEdit/) +
[CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) +
[Markdig](https://github.com/xoofx/markdig) (AST projected to Avalonia controls, see `ChatMarkdown.cs`). Same Harbor engine (AgentLoop, EventBus, `UiStore`/`TuiEffectHost` from `Harbor.Ui.Framework.State`) as the CLI — different shell.

This is a **standalone WinExe app** that boots its own host, constructs the registries,
wires `IAgent`, and shows a 1280×800 desktop window with sidebar + chat + code editor + status bar. It is not an in-CLI renderer (`HARBOR_TUI=avalonia` is not wired).

## Run

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet run --project apps/Harbor.App.Avalonia
```

Environment variables (all optional):

| Variable          | Default                     | Notes                                                 |
|-------------------|-----------------------------|-------------------------------------------------------|
| `HARBOR_MODEL`    | `kilocode/tencent/hy3:free` | `provider/model` — picked up at startup (`Program.cs:31`). |
| `HARBOR_STORAGE`  | `memory`                    | `memory` (ephemeral) or `jsonl` (~/.harbor/sessions).  |
| `HARBOR_LOGLEVEL` | Info-level console default  | `Trace`/`Debug`/`Information`/`Warning`/`Error`.       |

## What's implemented

### Shell

- **1280×800 main window** with top menu bar (File / Edit / View / Settings / Help),
  left sidebar, central view tab strip, status bar.
- **Catppuccin-Mocha dark theme** by default + Catppuccin-Latte light theme
  (toggle with `Ctrl+Shift+T`).
- **Status bar** shows agent / model / provider / token-in / token-out / cost / session
  count with status dot colored by state (idle / running / compacting / error).
- **Sidebar collapse** (`Ctrl+B`) with smooth transition.
- **Onboarding window** (`OnboardingWindow.axaml` + `OnboardingViewModel`) using the
  provider preset catalog: preset picker with connection health check
  (`IProviderHealthCheck`) and live model lists — mirrors PROD-UI-0 onboarding UX.

### Chat

- Streaming chat with role-colored lines (user / assistant / tool / tool_result /
  thinking / error / system). Color set from Catppuccin-Mocha.
- Markdown rendered via Markdig AST → Avalonia control projection (`ChatMarkdown.cs`).
- "● streaming" indicator with live text buffer preview when the assistant is generating.
- Multi-line input (`Shift+Enter` for newline, `Enter` to send).
- Stop button visible during agent runs.
- Clear chat (`Ctrl+L`).

### Session manager (sidebar)

- List of sessions (title, agent, relative time, message count).
- Fuzzy search filter.
- New session, Branch active (creates a child session with copied messages),
  Open, Delete, Rename.
- Active session highlighted with a green dot.

### Board view (`Views/Board/`)

- Kanban-style session board (`BoardView` + `SessionCardView`).

### Focus session view

- Dedicated full view for the active session (`FocusSessionView.axaml`).

### Code editor (AvaloniaEdit)

- Multi-tab editor with file open (`Ctrl+O`) and save (`Ctrl+S`).
- Syntax highlighting via `HighlightingManager.Instance.GetDefinition(...)`.
- Line numbers.
- Dirty indicator (●) on tab title when content has unsaved changes.

### Command palette (`Ctrl+P`)

- cmdk-style fuzzy search popup across commands and views.
- Keyboard navigation: ↑/↓ to move, `Enter` to invoke, `Esc` to close.

### Diff view

- Side-by-side line-by-line diff with before/after text inputs.
- Row coloring: added (green), removed (red), modified (blue), unchanged (dim).

### Token usage chart

- Per-turn bar chart (input tokens in cyan, output tokens in peach).
- Summary tiles: total input, total output, cumulative cost in USD.
- Capped at last 50 turns; "Clear" button resets the chart.

### Provider browser & settings

- Provider browser modal lists registered providers and their live models with metadata
  (display name, id, features tools/vision/reasoning, pricing per 1M tokens).
- `ProviderConfigViewModel` / `ProviderModelPickerViewModel` wrap provider/model picking
  backed by the preset catalog and health-checked connections.
- Settings dialog with Model / Storage / LogLevel / Theme controls.

### Toast notifications

- Bottom-right toast container with auto-dismiss; Info/Success/Warning/Error kinds.

### Plugin panel host

- Right-dock slot for panel contributions (`PluginPanelHostView`).

## Architecture

```
apps/Harbor.App.Avalonia/
├── Program.cs                  # async Main → AppHost.BuildAsync → Avalonia lifetime
├── AppHost.cs                  # DI composition root ([Exposes] declarations; ToolSet = Standard10)
├── App.axaml / .cs             # Application, theme, DI hand-off
├── Hosting/                    # Registration split: ConfigRegistration, ServiceRegistration,
│                               #   ViewModelRegistration, UiEventRouter, LoggingConfiguration
├── Navigation/AvaloniaContentHost.cs  # central content host for views
├── Views/
│   ├── MainWindow.axaml(.cs)   # shell + global keyboard shortcuts
│   ├── ChatView / CodeEditorView / DiffView / TokenUsageView / CommandPaletteView /
│   │   ToastNotificationsView / ProviderBrowserView / SettingsView / PluginPanelHostView /
│   │   OnboardingWindow / FocusSessionView
│   ├── Board/                  # BoardView + SessionCardView (kanban board)
│   ├── Chrome|Shell|Components|Controls|Dev|Overlays  # shared visual pieces
│   └── Converters.cs           # value converters
├── ViewModels/
│   ├── MainViewModel.cs        # shell VM: active view, sidebar, status bar
│   ├── ChatViewModel.cs        # streaming, message list, send
│   ├── SessionListViewModel.cs # sessions, search, branch
│   ├── OnboardingViewModel.cs  # wizard (+ IProviderHealthCheck test connection)
│   ├── ProviderConfigViewModel.cs / ProviderModelPickerViewModel.cs
│   ├── SettingsViewModel.cs / ThemeSettingsViewModel.cs
│   ├── CodeEditorViewModel.cs / DiffViewModel.cs / TokenUsageViewModel.cs /
│   │   CommandPaletteViewModel.cs / FocusSessionViewModel.cs
│   └── Board/                  # BoardViewModel + SessionCardViewModel
├── Services/                   # AvaloniaDispatcherAdapter, AvaloniaChatViewBinder,
│                               #   AvaloniaUiViewport, AvaloniaWorkspaceCommands,
│                               #   CommonConfigAuthResolver (AuthStore-backed keys),
│                               #   DialogService, DiffPreviewHelper, KeyboardShortcutService,
│                               #   ThemeService, UiRenderEngine, WindowChromeService
└── Themes/                     # Dark/Light palettes + styles
```

### Composition root

DI registration is split under `Hosting/` (`ConfigRegistration`, `ServiceRegistration`,
`ViewModelRegistration`, `LoggingConfiguration`) with `AppHost.cs` declaring the
`[Exposes(typeof(...))]` surface (`IEventBus`, `ISessionStore`, `IToolRegistry`,
`IProviderRegistry`, `IAgentRegistry`, `UiStore`, `TuiEffectHost`). The tool registry is
built through `Harbor.Hosting`'s `ToolsCatalog.CreateToolRegistry` with
`HarborToolSetKind.Standard10` (`AppHost.cs:100`); provider discovery registers the JSON
provider presets via `JsonProviderDiscovery.RegisterJsonProviders`
(`src/Harbor.Hosting/Modules/ProviderFactories.cs:61`), Ollama always available, API keys
resolved through `AuthStore`. The built `IServiceProvider` reaches `MainViewModel` during
`App.OnFrameworkInitializationCompleted`.

### MVVM wiring

- ViewModels use `[ObservableProperty]` + `[RelayCommand]` source generators from
  `CommunityToolkit.Mvvm`.
- All VMs are `sealed partial class`-es — no INPC boilerplate.
- `MainViewModel` holds child VMs; Views bind via `DataContext="{Binding ...}"`.

### UiStore marshalling

The agent loop runs on the thread pool; `UiStore.Changed` events arrive from any thread.
`AvaloniaDispatcherAdapter` re-raises them on the UI thread via `Dispatcher.UIThread.Post`;
ViewModels update their collections there.

### Desktop event routing

`Hosting/UiEventRouter.cs` routes `AgentEvent`s into the shared UI framework reducers so
the desktop shell and terminal TUIs consume identical state transitions.

## Quality bar

| Feature                   | Status                                        |
|---------------------------|-----------------------------------------------|
| Code editor               | ✅ AvaloniaEdit 12, tabs                       |
| Streaming chat            | ✅ Role colors, markdown projection, stop/clear|
| Onboarding                | ✅ Preset catalog, health check, live models   |
| Session manager           | ✅ Search, new, branch, open, delete          |
| Board / focus-session     | ✅ `Views/Board/`, `FocusSessionView`         |
| Command palette           | ✅ Fuzzy search, keyboard nav                 |
| Token usage chart         | ✅ Per-turn bars + summary tiles              |
| Diff view                 | ✅ Side-by-side, color-coded rows            |
| Provider browser/settings | ✅ Live models, config VMs                    |
| Toasts                    | ✅ Auto-dismiss, 4 kinds                      |
| Dark/light themes         | ✅ Catppuccin-Mocha + Latte                   |
| Plugin panel host         | ⚠️ Placeholder wiring                         |

## Build

```bash
dotnet build apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj
# → Build succeeded. 0 Warning(s), 0 Error(s).
```

Verified on .NET 10 + Avalonia 12.1.x (`Directory.Packages.props:109`) + Avalonia.AvaloniaEdit 12.0.0 +
Markdig + CommunityToolkit.Mvvm 8.4.x + Microsoft.Extensions.Hosting 10.

### Known caveats

- `UiStore.Transition` stays `internal` (TEA purity guard). Desktop GUIs access it via
  `<InternalsVisibleTo Include="Harbor.App.Avalonia"/>` declared in
  `src/Harbor.Ui.Framework.State/Harbor.Ui.Framework.State.csproj:23`.
- Minimal/no-AOT builds only: Avalonia UI assemblies require JIT.
- The `Harbor.Tui.Abstractions` project referenced by older docs is a deprecated facade;
  the app depends on `Harbor.Ui.Framework` directly (`Harbor.App.Avalonia.csproj:90`).

## License

MIT, same as the rest of Harbor.
