# Harbor.Tui.TerminalGui

Harbor TUI renderer built on **Terminal.Gui v2.4** — Miguel de Icaza's mainstream .NET TUI framework. Mature widget set (Window, FrameView, TextView, ListView, Menu, Dialog), event-driven Application.Init() lifecycle, cross-platform key/mouse handling. Competitive with `Harbor.Tui.SpectreTui` for everyday chat; pull it in when you need a classic full-screen TUI like `tig`, `htop`, or `lazygit`.

## When to use this renderer?

- You want the **most mature** .NET TUI framework — Terminal.Gui has been in production since 2014, has the largest widget zoo, and ships with a built-in theme manager, mouse support, clipboard integration, and accessibility hooks.
- You're targeting **legacy/conhost.exe** or any terminal that does NOT support the Kitty keyboard protocol / DCS synchronized output. Terminal.Gui's v2 CursesDriver + WinDriver fall back gracefully on every terminal ever shipped.
- You need **sub-windows, dialogs, and menus** — Terminal.Gui's `Dialog`, `MessageBox`, `MenuBar` are battle-tested; SpectreTui has nothing comparable.
- You're porting a **classic full-screen TUI** (file manager, process viewer, Git client) — Terminal.Gui's `Application.Run(Toplevel)` model matches that pattern exactly.

## Killer feature: complete widget set

Terminal.Gui v2 ships `Window`, `FrameView`, `TextView`, `ListView`, `TreeView`, `Menu`, `MenuBar`, `Dialog`, `FileDialog`, `ComboBox`, `TextField`, `Button`, `Label`, `ProgressBar`, `SpinnerView`, `ScrollBarView`, `TileView`, `TabView`, `Graph`, `BarChart`, `Wizard`, … — the SpectreTui renderer has none of these as builtin widgets. The Harbor renderer uses `TextView` for the chat history + input box (with `WordWrap` and `ReadOnly` toggles), `Label` for the status bar, and `Window` as the top-level chrome. Stretch goal: dim/percent layouts + sub-windows + a `MenuBar` exposing the slash-command surface.

## Trade-off

Heavier. Terminal.Gui v2.4 is ~2 MB of IL and pulls in `System.Memory`, `NStack`, `Microsoft.Extensions.Logging.Abstractions` — significantly heavier than the SpectreTui stack. Its application model (`Application.Init()` / `Application.Run()` / `Application.Shutdown()`) is **synchronous** and **blocks the calling thread**, which fights with Harbor's async `IAgent.PromptAsync` pipeline. The renderer works around this by running the agent on the thread pool and using `IApplication.Invoke` to marshal UI updates back to the main thread — workable, but not as clean as SpectreTui's `Application.Create().RunAsync(screen)`.

## Hotkey table

| Key               | Action                              |
|-------------------|-------------------------------------|
| `Enter`           | Submit prompt                       |
| `Shift+Enter`     | Newline (multi-line input)          |
| `Esc`             | Abort running prompt / quit if idle |
| `Ctrl+C`          | Abort running prompt                |
| `Ctrl+L`          | Clear transcript                    |
| `F2`              | Toggle focus (Input ↔ Chat)         |
| `↑` / `↓`         | Scroll one line                     |
| `PgUp` / `PgDn`   | Scroll one viewport                 |
| `Home` / `End`    | Jump to top / bottom                |
| `Alt+↑` / `Alt+↓` | Navigate input history              |
| `Tab`             | Autocomplete slash command          |
| `Ctrl+P`          | Command palette                     |
| `Alt+1..9`        | Toggle Nth registered panel         |
| `Ctrl+Tab`        | Cycle panel focus                   |
| `?`               | Toggle help panel                   |

## How to run

```bash
export HARBOR_TUI=terminal-gui
dotnet run --project apps/Harbor.App.Cli
```

## Architecture

```
src/Harbor.Tui.TerminalGui/
├── Harbor.Tui.TerminalGui.csproj
├── TerminalGuiRenderer.cs         ← main IInteractiveTuiRenderer impl (Application.Init + Window chrome + 20-FPS throttle)
├── TerminalGuiTeaBridge.cs        ← UiStore + TuiEffectHost + KeyHandler wrapper (TEA path)
├── Views/
│   ├── ChatView.cs                ← UiState → role-colored transcript (with markdown + GFM tables)
│   ├── InputView.cs               ← input box with caret + slash autocomplete
│   ├── StatusBarView.cs           ← provider/model/agent/tokens/cost/status/scroll%
│   ├── SessionSidebarView.cs      ← panel list (uses IPanelRegistry + UiState.PanelStates)
│   └── CommandPaletteView.cs      ← Ctrl+P fuzzy search popup
├── Handlers/
│   ├── KeyHandler.cs              ← ConsoleKeyInfo → UiKey → ChatAction → UiMsg dispatch
│   └── ScrollHandler.cs           ← pure scroll math (rows-from-bottom)
└── Rendering/
    ├── TerminalGuiColorMapper.cs  ← ChatRole → Terminal.Gui Color + label
    └── TerminalGuiMarkdownRenderer.cs ← MD headings/bold/italic/code/lists/hr + GFM tables
```

## TEA integration

`TerminalGuiTeaBridge` wraps `UiStore` + `TuiEffectHost` + `KeyHandler`. The renderer's `RenderAsync` dual-dispatches every `AgentEvent` to BOTH the legacy `TerminalGuiScreen` (drives the live `TextView` with 20-FPS throttle) AND the new `TeaBridge` (drives the 13-feature surface via `UiStore`). No duplicate state — `Tea` is the single source of truth for the new Views/Handlers.

The legacy `TerminalGuiScreen` keeps its own `_chat` list / `_streamBuffer` / `_thinkBuffer` for the live `TextView` projection; that's slated for removal once the `TextView` learns to project from `UiStore.Lines` directly.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full 4-way TUI/GUI comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
- [PLAN.md](PLAN.md) — what's done, what's TODO
