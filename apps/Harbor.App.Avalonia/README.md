# Harbor.App.Avalonia

Standalone ORCA-level desktop GUI for [Harbor](..) — the .NET 10 AI agent harness.
Built on [Avalonia UI 11.2](https://avaloniaui.net/) + Skia + [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit/) +
[CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) +
[Markdig](https://github.com/xoofx/markdig). Same Harbor engine (AgentLoop, EventBus,
UiStore, TuiEffectHost) as the CLI — different shell.

This is **not** the legacy `src/Harbor.Tui.Avalonia/` skeleton (which is a 550-line
`ITuiRenderer` impl that runs inside `Harbor.Cli` via `HARBOR_TUI=avalonia`). This is
a **standalone WinExe app** that boots its own `Microsoft.Extensions.Hosting` host,
constructs the registries, wires `IAgent` + `UiStore`, and shows a 1280×800 desktop
window with sidebar + chat + code editor + status bar.

## Run

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet run --project apps/Harbor.App.Avalonia
```

Environment variables (all optional):

| Variable          | Default                   | Notes                                                 |
|-------------------|---------------------------|-------------------------------------------------------|
| `HARBOR_MODEL`    | `ollama/qwen2.5-coder:7b` | `provider/model` — picked up at startup.              |
| `HARBOR_STORAGE`  | `memory`                  | `memory` (ephemeral) or `jsonl` (~/.harbor/sessions). |
| `HARBOR_LOGLEVEL` | `Warning`                 | `Trace`/`Debug`/`Information`/`Warning`/`Error`.      |
| `OLLAMA_HOST`     | `http://localhost:11434`  | Ollama server URL.                                    |

## What's implemented

### Shell

- **1280×800 main window** with top menu bar (File / Edit / View / Settings / Help),
  left sidebar, central view tab strip, status bar.
- **Catppuccin-Mocha dark theme** by default + Catppuccin-Latte light theme
  (toggle with `Ctrl+Shift+T`).
- **Status bar** shows agent / model / provider / token-in / token-out / cost / session
  count with status dot colored by state (idle / running / compacting / error).
- **Sidebar collapse** (`Ctrl+B`) with smooth transition.

### Chat

- Streaming chat with role-colored lines (user / assistant / tool / tool_result /
  thinking / error / system). Color set from Catppuccin-Mocha.
- "● streaming" indicator with live text buffer preview when the assistant is generating.
- "🤔 thinking…" indicator when the agent is running but not streaming.
- Multi-line input (`Shift+Enter` for newline, `Enter` to send).
- Stop button visible during agent runs.
- Clear chat (`Ctrl+L`).

### Session manager (sidebar)

- List of sessions (title, agent, relative time, message count).
- Fuzzy search filter.
- New session, Branch active (creates a child session with copied messages),
  Open, Delete, Rename.
- Active session highlighted with a green dot.

### Code editor (AvaloniaEdit)

- Multi-tab editor with file open (`Ctrl+O`) and save (`Ctrl+S`).
- Syntax highlighting for **C#, JavaScript/TypeScript, JSON, Markdown, Python, Go,
  Rust, Java, C++, XML/AXAML/XAML, HTML, CSS, SQL, Bash** — 15+ languages via
  `HighlightingManager.Instance.GetDefinition(...)`.
- Line numbers.
- Dirty indicator (●) on tab title when content has unsaved changes.
- Each tab tracks its own file path, name, extension, content.

### Command palette (`Ctrl+P`)

- cmdk-style fuzzy search popup.
- Searches across: 14 view/agent commands + 12 slash commands.
- Keyboard navigation: ↑/↓ to move, `Enter` to invoke, `Esc` to close.
- Subsequence-match scoring (label matches ranked higher than hint matches).

### Diff view

- Side-by-side line-by-line diff with before/after text inputs.
- Row coloring: added (green), removed (red), modified (blue), unchanged (dim).
- "Compute" button to recompute the diff.

### Token usage chart

- Per-turn bar chart (input tokens in cyan, output tokens in peach).
- Summary tiles: total input, total output, cumulative cost in USD.
- Capped at last 50 turns.
- "Clear" button resets the chart.

### Provider browser

- Modal dialog listing registered providers (default: `ollama`).
- Select a provider to see its models with metadata: display name, id, features
  (tools/vision/reasoning), pricing per 1M tokens.

### Settings

- Modal dialog with Model / Storage / LogLevel / OllamaHost / Theme controls.
- Save persists to the in-process env vars (restart to apply model changes).
- Theme toggle takes effect immediately.

### Toast notifications

- Bottom-right toast container with 4-second auto-dismiss.
- 4 kinds: Info (blue), Success (green), Warning (peach), Error (red).
- Pushed by ViewModels via `ToastService.Show(message, kind)`.

### Plugin panel host

- Reserved right-dock slot for `IPanelProvider` contributions (placeholder in v0.4;
  full integration with `IPanelRegistry` planned for v0.5).

## Architecture

```
apps/Harbor.App.Avalonia/
├── Program.cs                          # async Main → AppHost.BuildAsync → Avalonia lifetime
├── AppHost.cs                          # DI composition root (mirrors Harbor.Cli/Hosting/HostBuilder)
├── App.axaml / .cs                     # Application, theme, DI hand-off
├── Views/
│   ├── MainWindow.axaml / .cs          # 1280×800 shell + global keyboard shortcuts
│   ├── ChatView.axaml / .cs            # streaming chat with role colors
│   ├── SessionListView.axaml*          # left sidebar (inlined in MainWindow.axaml)
│   ├── CodeEditorView.axaml / .cs      # AvaloniaEdit host + tab strip
│   ├── DiffView.axaml / .cs            # side-by-side diff
│   ├── TokenUsageView.axaml / .cs      # bar chart of per-turn tokens
│   ├── CommandPaletteView.axaml / .cs  # cmdk-style fuzzy popup
│   ├── ToastNotificationsView.axaml/.cs# bottom-right toast container
│   ├── ProviderBrowserView.axaml / .cs # provider+model browser modal
│   ├── SettingsView.axaml / .cs        # settings dialog
│   ├── PluginPanelHostView.axaml / .cs # right-dock placeholder
│   └── Converters.cs                   # BrushKeyConverter, EqualityConverter
├── ViewModels/
│   ├── MainViewModel.cs                # shell VM: active view, sidebar, status bar
│   ├── ChatViewModel.cs                # streaming, message list, send
│   ├── SessionListViewModel.cs         # sessions, search, branch
│   ├── ProviderBrowserViewModel.cs     # providers + models
│   ├── SettingsViewModel.cs            # editable settings
│   ├── CodeEditorViewModel.cs          # multi-tab editor VM
│   ├── DiffViewModel.cs                # diff rows
│   ├── TokenUsageViewModel.cs          # chart bars
│   └── CommandPaletteViewModel.cs      # fuzzy command search
├── Services/
│   ├── AvaloniaDispatcherAdapter.cs    # marshals UiStore.Changed → UI thread
│   ├── ThemeService.cs                 # dark/light theme switch
│   ├── AvaloniaFilePicker.cs           # IStorageProvider wrapper
│   ├── DialogService.cs                # ConfirmAsync, PromptAsync, ToastService
│   └── SessionManager.cs               # owns active session, binds IAgent
├── Themes/
│   ├── Dark.axaml                      # Catppuccin-Mocha palette + brushes
│   ├── Light.axaml                     # Catppuccin-Latte palette
│   ├── Typography.axaml                # FontFamily + control styles
│   └── AppStyles.axaml                 # Window/Border/StatusBar styles
└── README.md                           # this file
```

### Composition root

`AppHost.BuildAsync` mirrors `Harbor.Cli/Hosting/HostBuilder.Build`:

1. Constructs `ToolRegistry` eagerly (10 builtin tools: read, write, edit, bash,
   glob, grep, ls, patch, notebook, tree).
2. Constructs `ProviderRegistry` eagerly (just Ollama by default — add more in
   Settings).
3. Constructs `AgentRegistry` with `code` / `plan` / `explore` defaults.
4. Registers `IEventBus` (InMemoryEventBus), `IAgentLoop` (AgentLoop), `IAgent`
   (DefaultAgent), `ISessionStore` (Memory or Jsonl), `ICompactionService`,
   `IPermissionService`.
5. Constructs `UiStore` + `TuiEffectHost` (the same TEA store used by every
   other Harbor TUI).
6. Initializes the agent with a fresh default session so the user can start
   chatting immediately.
7. Hands the built `IServiceProvider` to `App.Services` via Avalonia's
   `AfterSetup` callback so `App.OnFrameworkInitializationCompleted` can resolve
   `MainViewModel`.

### MVVM wiring

- ViewModels use `[ObservableProperty]` + `[RelayCommand]` source generators from
  `CommunityToolkit.Mvvm`.
- All VMs are `sealed partial class`-es — no INPC boilerplate.
- `MainViewModel` holds all child VMs as getter properties; the Views bind to
  `MainViewModel.Chat`, `MainViewModel.Sessions`, etc. via `DataContext="{Binding Chat}"`.

### UiStore marshalling

The agent loop runs on the thread pool; the reducer emits `UiStore.Changed` events
from any thread. `AvaloniaDispatcherAdapter` subscribes to those events and re-raises
them on the UI thread via `Dispatcher.UIThread.Post`. The ViewModels subscribe to
`OnUiThread` and update their `ObservableCollection<>`s.

## Quality bar — ORCA-level

| Feature                   | Status                                                               |
|---------------------------|----------------------------------------------------------------------|
| Code editor               | ✅ AvaloniaEdit, 15+ syntaxes, tabs                                   |
| Streaming chat            | ✅ Role colors, thinking, stop, clear                                 |
| Session manager           | ✅ Search, new, branch, open, delete                                  |
| Command palette           | ✅ Fuzzy search, keyboard nav                                         |
| Token usage chart         | ✅ Per-turn bars + summary tiles                                      |
| Diff view                 | ✅ Side-by-side, color-coded rows                                     |
| Provider browser          | ✅ Lists providers + models                                           |
| Settings dialog           | ✅ Model/storage/loglevel/theme                                       |
| Toasts                    | ✅ 4-second auto-dismiss, 4 kinds                                     |
| Dark/light themes         | ✅ Catppuccin-Mocha + Latte                                           |
| Status bar                | ✅ Agent/model/cost/state/session count                               |
| Standalone runnable       | ✅ `dotnet run` — no Harbor.Cli needed                                |
| Plugin panel host         | ⚠️ Placeholder (v0.5 will wire IPanelRegistry)                       |
| Markdown rendering        | ⚠️ Markdig dependency is wired; full AST→Avalonia projection is v0.5 |
| Drag-drop session reorder | ⚠️ v0.5                                                              |
| Multi-select (Ctrl+click) | ⚠️ v0.5                                                              |

## Build

```bash
export PATH="$HOME/.dotnet:$PATH"
cd /path/to/harbor
dotnet restore apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj
dotnet build apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj
# → Build succeeded. 0 Warning(s), 0 Error(s).
```

Verified green on .NET 10.0.302 + Avalonia 11.2.7 + Avalonia.AvaloniaEdit 11.2.0 +
Markdig 0.31.0 + CommunityToolkit.Mvvm 8.4.0 + Microsoft.Extensions.Hosting 10.0.0.

### Known caveats

- Avalonia 11.2.7 transitively pulls `Tmds.DBus.Protocol` 0.20.0
  (GHSA-xrw6-gwf8-vvr9) on Linux. We can't pin a higher version of a transitive
  dep, so `NU1903` is suppressed in the project file. Upgrade Avalonia to 11.3+
  when it ships to clear this.
- The `Harbor.Tui.Abstractions.UiStore.Transition` method is `internal` by design
  (TEA purity guard). We added `<InternalsVisibleTo Include="Harbor.App.Avalonia"/>`
  to `Harbor.Tui.Abstractions.csproj` so the desktop GUIs (which need to fold
  user-input lines into the transcript before the agent emits a `UserMessage`
  event) can call it. Harbor.Cli doesn't need this — it goes through the agent.
- Ollama is the only provider wired by default. Add OpenAI/Anthropic in Settings
  (or extend `AppHost.BuildAsync` directly) to use other providers.

## License

MIT, same as the rest of Harbor.
