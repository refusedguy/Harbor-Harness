# Harbor.Tui.Wpf

Native Windows WPF renderer for Harbor. Spins up a real desktop window with XAML
designer, hot reload, monospace fonts, role-colored chat history, and a streaming
indicator bar — at the cost of Windows-only runtime and ~80 MB RSS.

## When to use

- You want a real desktop GUI: mouse, full keyboard editing, copy/paste.
- You need DPI scaling, multi-monitor, or accessibility tooling (screen readers).
- You want XAML designer + Hot Reload during development.
- You're on Windows. (For cross-platform desktop use `Harbor.Tui.Avalonia`.)

## Platform support

| OS       | Supported | Notes                                   |
|----------|-----------|-----------------------------------------|
| Windows  | ✅        | Requires .NET 10 Desktop Runtime        |
| Linux    | ❌        | Use `Harbor.Tui.Avalonia` instead       |
| macOS    | ❌        | Use `Harbor.Tui.Avalonia` instead       |

## Dependencies

- `Harbor.Abstractions`
- `Harbor.Tui.Abstractions`
- `CommunityToolkit.Mvvm` (optional, for source-gen VMs)
- `Microsoft.Extensions.Logging.Abstractions`
- .NET 10 Desktop Runtime (WPF ships with the SDK)

## Files

- `Harbor.Tui.Wpf.csproj` — `net10.0-windows`, `UseWPF=true`, `OutputType=WinExe`
- `WpfTuiRenderer.cs` — sealed `ITuiRenderer`/`IInteractiveTuiRenderer`. Spawns
  an STA thread that runs `System.Windows.Application`, marshals
  `UiStore.Changed` events to the UI thread via `Dispatcher.BeginInvoke`, binds
  the chat history to an `ObservableCollection<ChatLineViewModel>`.
- `ChatWindow.xaml` — main window: header (agent/model/provider), scrollable
  chat history `ListBox`, streaming indicator, multi-line input `TextBox`,
  status bar with token cost. Dark Catppuccin-Mocha theme, Cascadia Code font.
- `ChatWindow.xaml.cs` — code-behind. Enter = submit, Shift+Enter = newline,
  Ctrl+Enter = submit. Calls `_effects.Run(TuiEffect.PromptAgent/RunSlash/QuitApp)`.
- `ChatLineViewModel` — observable line item with `Role`, `Text`, `Foreground`.

## How it reads from UiStore

```csharp
_store.Changed += OnStoreChanged;

void OnStoreChanged(object? _, UiStateChangedEventArgs e)
{
    var state = e.State;
    _app.Dispatcher.BeginInvoke(() =>
    {
        while (_lines.Count < state.Lines.Length)
            _lines.Add(new ChatLineViewModel(state.Lines[_lines.Count].Role,
                                             state.Lines[_lines.Count].Text));
        _window.UpdateStreaming(state);
    });
}
```

The reducer stays the single source of truth — the WPF renderer only projects
state into the window, exactly like `SpectreTuiRenderer` projects into widgets.

## Build

```bash
# Restore + build (Windows-only target)
dotnet build src/Harbor.Tui.Wpf/Harbor.Tui.Wpf.csproj -c Release

# Run with the WPF renderer
HARBOR_TUI=wpf dotnet run --project src/Harbor.Cli/Harbor.Cli.csproj
```

If you want it added to the solution, run:

```bash
dotnet sln Harbor.slnx add src/Harbor.Tui.Wpf/Harbor.Tui.Wpf.csproj
```

## Selecting this renderer

Set `HARBOR_TUI=wpf` in your environment, or add `tui: "wpf"` to
`~/.harbor/config.json`.

## Memory footprint

Approx. 80 MB RSS idle (vs ~1 MB for `Ansi`, ~50 MB for `SpectreTui`). The WPF
rendering stack (composition, DPI, text shaping) is the bulk of the cost.

## Limitations / TODO

- Markdown rendering is plain text today. Wire a `FlowDocument` converter for
  rich text (code blocks, lists, inline code, links).
- No diff overlay panel. The `DiffPreviewView` placement is currently unused.
- No theming hook. Hard-coded Catppuccin-Mocha colors; would need a `Theme`
  property on the renderer.
