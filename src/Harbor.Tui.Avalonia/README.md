# Harbor.Tui.Avalonia

Cross-platform desktop renderer for Harbor built on [Avalonia UI 11](https://avaloniaui.net/).
Single XAML+codebase runs natively on Windows (Win32), Linux (X11/Wayland GTK), and
macOS (AppKit). Uses Skia for rendering so look-and-feel is identical across platforms.

## When to use

- You want a real desktop GUI on every OS, not just Windows.
- You want the WPF-style XAML + data-binding + designer + hot reload, but cross-platform.
- You can afford ~60 MB RSS (heavier than terminal renderers but lighter than WPF).
- You're already using Avalonia elsewhere in your stack.

## Platform support

| OS       | Renderer backend        | Supported |
|----------|-------------------------|-----------|
| Windows  | Win32 + Skia            | ✅        |
| Linux    | X11 / Wayland + GTK     | ✅        |
| macOS    | AppKit + Skia           | ✅        |

## Dependencies

- `Harbor.Abstractions`
- `Harbor.Tui.Abstractions`
- `Avalonia` 11.2+
- `Avalonia.Desktop`
- `Avalonia.Themes.Fluent`
- `Avalonia.Fonts.Inter` (for IBM Plex Mono fallback)
- `Avalonia.Diagnostics` (Debug only — F12 dev tools)
- `CommunityToolkit.Mvvm` (optional, for source-gen VMs)
- `Microsoft.Extensions.Logging.Abstractions`

## Files

- `Harbor.Tui.Avalonia.csproj` — `net10.0`, `OutputType=WinExe`, Avalonia 11.2 packages.
- `AvaloniaTuiRenderer.cs` — sealed `ITuiRenderer`/`IInteractiveTuiRenderer`. Spawns a
  dedicated UI thread that runs Avalonia's `ClassicDesktopStyleApplicationLifetime`,
  marshals `UiStore.Changed` events to the UI thread via `Dispatcher.UIThread.Post`,
  binds history to an `ObservableCollection<ChatLineViewModel>`.
- `App.axaml` + `App.axaml.cs` — Avalonia `Application` subclass with Fluent dark theme.
- `MainWindow.axaml` + `MainWindow.axaml.cs` — main window: header, history `ListBox`,
  streaming indicator, multi-line input `TextBox`, status bar. Catppuccin-Mocha colors.
- `Program.cs` — standalone Avalonia entry (for `dotnet run` designer preview only).
  Production entry is via `Harbor.Cli` with `HARBOR_TUI=avalonia`.

## How it reads from UiStore

```csharp
_store.Changed += OnStoreChanged;

void OnStoreChanged(object? _, UiStateChangedEventArgs e)
{
    Dispatcher.UIThread.Post(() =>
    {
        while (_lines.Count < state.Lines.Length)
            _lines.Add(new ChatLineViewModel(state.Lines[_lines.Count].Role,
                                             state.Lines[_lines.Count].Text));
        _window.UpdateStreaming(state);
    });
}
```

Same MVU contract as `SpectreTuiRenderer` — only the projection (Skia scene graph vs
Spectre widgets) differs.

## Build

```bash
# Restore + build (cross-platform)
dotnet build src/Harbor.Tui.Avalonia/Harbor.Tui.Avalonia.csproj -c Release

# Run with the Avalonia renderer
HARBOR_TUI=avalonia dotnet run --project src/Harbor.Cli/Harbor.Cli.csproj
```

## Selecting this renderer

Set `HARBOR_TUI=avalonia` in your environment, or add `tui: "avalonia"` to
`~/.harbor/config.json`.

## Memory footprint

Approx. 60 MB RSS idle (vs ~80 MB for WPF, ~1 MB for `Ansi`, ~50 MB for
`SpectreTui`). Skia scene graph + composition + text shaping dominate.

## Limitations / TODO

- The skeleton wires the lifetime and store plumbing. Production bring-up
  requires validating the Avalonia 11.2 `AppBuilder.AfterSetup` callback signature
  (it changed across minor versions).
- No markdown rendering — use `AvaloniaEdit` or a `Markdig` → `FormattedText` pipeline.
- No diff overlay panel.
- For mobile, see `Harbor.Tui.Maui` instead (Avalonia also has a mobile target but
  MAUI is the Microsoft-blessed path).
