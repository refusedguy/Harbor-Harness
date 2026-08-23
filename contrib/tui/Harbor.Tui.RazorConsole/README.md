# Harbor.Tui.RazorConsole

Harbor TUI renderer built on **RazorConsole.Core** — Blazor-for-the-terminal. Write `.razor` files with C# + Spectre markup and get hot-reloadable, component-composed TUI views. Competitive with `Harbor.Tui.SpectreTui` for everyday chat; pull it in when you want React-like component composition for TUI authoring.

## When to use this renderer?

- You're a **web developer** moving to TUI — the `.razor` syntax (`@inject`, `@code`, `@bind-Value`) is identical to Blazor, so your component-writing muscle transfers directly.
- You want **hot reload** during TUI development — edit `ChatTui.razor`, hit save, see the change without restarting the app.
- You want **reusable components** — `ChatMessage.razor`, `InputBox.razor`, `StatusBar.razor` compose just like React components, with parameters and child content.
- You're building a **TUI design system** — RazorConsole's component model lets you ship a NuGet package of `.razor` widgets that other Harbor renderers can consume.

## Killer feature: component model

RazorConsole gives you `.razor` files compiled to C# component classes. The existing `ChatTui.razor` already demonstrates the pattern: `@inject ChatBridge Harbor` for DI, `@foreach (var m in Harbor.Messages)` for list rendering, `<Markup Content="@text" Foreground="@color"/>` for Spectre widgets, `@code { … }` for state. The `Message` RenderFragment shows how to factor a single chat bubble into a reusable fragment. The renderer's `Views/ChatView.cs` (in this package) is the C#-only equivalent for callers who don't want to write `.razor` — both produce the same Spectre markup output.

Stretch goal: a `Components/` directory with `ChatMessage.razor`, `InputBox.razor`, `StatusBar.razor` as standalone reusable components that downstream apps can `<ChatMessage Role="user" Text="…"/>` into their own `.razor` layouts.

## Trade-off

Compile-time dependency. RazorConsole pulls in the **Razor SDK** (`Microsoft.NET.Sdk.Razor`) + `Microsoft.AspNetCore.Razor.Language` + `Microsoft.CodeAnalysis.Razor` — ~6 MB of tooling that runs at build time. The Razor SDK also forces a slower build (Razor codegen runs before C# compilation). The framework is **stream-based** (each render clears the console and reprints the full frame), which fights with cursor-addressable TUI patterns — every frame is a full repaint, so per-cell differential updates aren't possible. May be removed in a future release if the cursor-addressable model wins.

## Hotkey table

| Key               | Action                              |
|-------------------|-------------------------------------|
| `Enter`           | Submit prompt                       |
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
export HARBOR_TUI=razor
dotnet run --project apps/Harbor.App.Cli
```

## Architecture

```
src/Harbor.Tui.RazorConsole/
├── Harbor.Tui.RazorConsole.csproj   ← Microsoft.NET.Sdk.Razor
├── RazorConsoleRenderer.cs          ← main IInteractiveTuiRenderer impl (HostBuilder.UseRazorConsole<ChatTui>)
├── RazorConsoleTeaBridge.cs         ← UiStore + TuiEffectHost + KeyHandler wrapper (TEA path)
├── ChatTui.razor                    ← root component (existing, R3-style ChatBridge)
├── Views/
│   ├── ChatView.cs                  ← UiState → Spectre markup strings (with markdown + GFM tables)
│   ├── InputView.cs                 ← input box with caret + slash autocomplete
│   ├── StatusBarView.cs             ← provider/model/agent/tokens/cost/status/scroll%
│   ├── SessionSidebarView.cs        ← panel list (uses IPanelRegistry + UiState.PanelStates)
│   └── CommandPaletteView.cs        ← Ctrl+P fuzzy search popup
├── Handlers/
│   ├── KeyHandler.cs                ← ConsoleKeyInfo → UiKey → ChatAction → UiMsg dispatch
│   └── ScrollHandler.cs             ← pure scroll math (rows-from-bottom)
└── Rendering/
    ├── RazorColorMapper.cs          ← ChatRole → Spectre Color + label + markup string
    └── RazorMarkdownRenderer.cs     ← MD headings/bold/italic/code/lists/hr + GFM tables (Spectre markup output)
```

## TEA integration

`RazorConsoleTeaBridge` wraps `UiStore` + `TuiEffectHost` + `KeyHandler`. The renderer's `RenderAsync` dual-dispatches every `AgentEvent` to BOTH the legacy `ChatBridge` (drives the `ChatTui.razor` component's `StateChanged` event) AND the new `TeaBridge` (drives the 13-feature surface via `UiStore`). No duplicate state — `Tea` is the single source of truth for the new Views/Handlers.

The legacy `ChatBridge` (with its `List<ChatLine>` + `StreamBuffer` + `ThinkBuffer` + `Status`) is the existing reactive store that `ChatTui.razor` subscribes to via `StateChanged`. It's slated for removal once `ChatTui.razor` learns to project from `UiStore.State` directly (which would make it a true SpectreTui-equivalent).

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full 4-way TUI/GUI comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
- [PLAN.md](PLAN.md) — what's done, what's TODO
