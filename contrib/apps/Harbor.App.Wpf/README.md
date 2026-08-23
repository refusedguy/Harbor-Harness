# Harbor.App.Wpf

Standalone production-ready WPF desktop GUI for the Harbor AI coding agent harness.

> This is **not** a TUI renderer — it's a self-contained desktop application
> built directly on `Harbor.Abstractions` + `Harbor.Core`, designed to ship as
> a Windows `.exe` that anyone can double-click. The Avalonia-based renderer
> in `src/Harbor.Tui.Avalonia/` remains the cross-platform renderer; this WPF
> shell is the Windows-native alternative.

## Architecture

```
apps/Harbor.App.Wpf/
├── App.xaml / App.xaml.cs          # Microsoft.Extensions.Hosting bootstrap
├── app.manifest                   # PerMonitorV2 DPI awareness
├── appsettings.json               # Default agent/model/theme
├── Views/                         # XAML + code-behind (10 views)
├── ViewModels/                    # Sealed VMs, CommunityToolkit.Mvvm
├── Services/                      # Dispatcher, Theme, FilePicker, Dialog, Markdown renderer
├── Themes/                        # Catppuccin Mocha/Latte, Typography, Animations
└── README.md                      # This file
```

### Dependency graph

```
Harbor.App.Wpf
   ├── Harbor.Abstractions         (domain contracts)
   ├── Harbor.Core                 (AgentLoop, ProviderRegistry, AgentRegistry)
   ├── Harbor.Tui.Abstractions     (reused for state types — not as a renderer)
   ├── Harbor.Storage.Memory       (in-memory session store — swap for Jsonl/Sqlite)
   ├── CommunityToolkit.Mvvm       ([ObservableProperty], [RelayCommand])
   ├── Microsoft.Extensions.Hosting (DI host)
   ├── Markdig                     (markdown → FlowDocument renderer)
   ├── AvalonEdit 6.x              (code editor)
   └── Dirkster.AvalonDock 4.x     (dockable panels)
```

> **LiveCharts2 is intentionally omitted.** The prebuilt
> `LiveChartsCore.SkiaSharpView.WPF` package targets `net8.0-windows` only and
> trips the WPF markup compiler under `net10.0-windows` with the cryptic
> `MC1000: Value cannot be null. (Parameter 'key')` error. Token-usage charts
> are implemented natively with `ItemsControl` + `Border` bars instead — no
> third-party chart dependency. Swap back to LiveCharts2 when a
> `net10.0-windows` build is published.

## Run

```bash
# Windows:
dotnet run --project apps/Harbor.App.Wpf

# Linux/macOS (will compile but won't launch a window):
dotnet build apps/Harbor.App.Wpf
```

The csproj sets `<EnableWindowsTargeting>true</EnableWindowsTargeting>` so the
project still restores + builds on non-Windows hosts for design-time
verification. To launch the window you need a Windows desktop with the
.NET 10 Desktop Runtime.

## Features

| Feature                 | Notes                                                                      |
|-------------------------|----------------------------------------------------------------------------|
| **Streaming chat**      | Markdown transcript via `Markdig` → `FlowDocument`; auto-scroll            |
| **Session manager**     | Sidebar with search, new/fork/delete; sample data                          |
| **Provider browser**    | Modal listing Anthropic / OpenAI / OpenRouter / Ollama / Groq              |
| **Code editor**         | `AvalonEdit` with syntax highlighting (C#, JSON, XML, HTML, JS)            |
| **Side-by-side diff**   | Hunks with color-coded added/removed lines                                 |
| **Token usage charts**  | Native WPF Shapes bar chart (no third-party chart lib)                     |
| **Command palette**     | `Ctrl+P` fuzzy search popup                                                |
| **Toast notifications** | Top-right slide-in overlay, auto-dismiss after 4–8 s                       |
| **Dockable panels**     | `AvalonDock` `DockingManager`                                              |
| **Themes**              | Catppuccin Mocha (default) + Latte, persisted to `~/.harbor/wpf-theme.txt` |
| **Animations**          | WPF `Storyboard` (toast slide-in, page transition, spinner)                |
| **Status bar**          | Provider / model / token count / cost / activity dot                       |

## Keyboard shortcuts

| Shortcut      | Action                  |
|---------------|-------------------------|
| `Ctrl+Enter`  | Send chat prompt        |
| `Ctrl+P`      | Open command palette    |
| `Ctrl+T`      | Toggle dark/light theme |
| `Shift+Enter` | Newline in chat input   |
| `Esc`         | Close command palette   |
| `↑` / `↓`     | Navigate palette items  |
| `Enter`       | Invoke palette command  |

## Architecture decisions

### Why a separate WPF app (not a renderer)

`Harbor.Tui.Wpf` already exists as a TUI renderer that runs the chat inside a
WPF window. That project inherits the `ITuiRenderer` contract and is meant to
be selected via `HARBOR_TUI=wpf`. `Harbor.App.Wpf` is different: it's a
**standalone shell** that owns its own window lifecycle, navigation, dialogs,
and panel layout — closer to VS Code's electron app than to a terminal
renderer. The two coexist; pick the one that fits your deployment target.

### Why in-memory session storage

The default `MemorySessionStore` keeps everything in process memory so the
shell launches instantly without touching disk. To enable durable sessions,
add a `<ProjectReference>` to `Harbor.Storage.Jsonl` (file-based) or
`Harbor.Storage.Sqlite` (SQLite db) and swap the registration in
`App.xaml.cs:RegisterHarbor`.

### Why `[ObservableProperty]` + `[RelayCommand]` source generators

CommunityToolkit.Mvvm 8.x emits `INotifyPropertyChanged` glue at compile time
via source generators. This keeps the VM body clean (just `partial` fields)
while still being fully observable. Generated code lives in
`obj/Generated/CommunityToolkit.Mvvm.SourceGenerators/`.

### Why per-monitor DPI

`app.manifest` declares `PerMonitorV2` DPI awareness so the app renders
crisply on 4K + multi-monitor Windows setups. WPF handles the scaling
automatically once the manifest opts in.

### ConfigureAwait(false) in services

All `await` calls in service-layer code (`WpfDispatcherAdapter.PostAsync`,
agent-loop callbacks) use `ConfigureAwait(false)` to avoid capturing the UI
sync context. The Dispatcher explicitly marshals back to the UI thread when
needed.

## See also

- `docs/ALTERNATIVE_UIS.md` — overview of every UI surface (TUI + GUI)
- `src/Harbor.Tui.Avalonia/README.md` — the Avalonia cross-platform renderer
- `src/Harbor.Tui.Wpf/README.md` — the WPF TUI renderer (chat window only)
- `docs/ARCHITECTURE.md` — overall Harbor architecture
