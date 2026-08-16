# Harbor.Tui.Termina

Harbor TUI renderer built on **Termina 0.15** — a modern R3-reactive MVVM terminal framework with fine-grained ANSI control, true color (24-bit), Kitty keyboard protocol, and synchronized output (DCS). Competitive with `Harbor.Tui.SpectreTui` for everyday chat; pull it in when you need pixel-perfect ANSI escape control.

## When to use this renderer?

- You want a **reactive MVVM** programming model (R3 observables, `ReactiveViewModel`, `ReactivePage`).
- You need **24-bit true color** with explicit ANSI escape sequences (no library color quantization).
- You're targeting modern terminals (Kitty, WezTerm, Ghostty, iTerm2, foot) that support the **Kitty keyboard protocol** and DCS synchronized-output — Termina's differential renderer tears less under fast streaming.
- You want **ratatui-style** differential rendering where only changed cells repaint.

## Killer feature: ANSI precision

Termina gives you direct control over the byte stream — escape sequences are first-class values, not abstracted behind a `Color` enum. The renderer's `TerminaMarkdownRenderer.Ansi(Color, string, bold, italic)` helper emits a 24-bit `<ESC>[38;2;R;G;Bm` sequence with optional bold/italic, so role colors and inline markdown stay perceptually identical across terminals that honor true color. The SpectreTui renderer, by contrast, quantizes to its 256-color palette even on true-color terminals.

Stretch goal: an **inline viewport mode** (like `grok-build`) that pins the UI to the bottom N rows of the native scrollback so the chat history flows into the terminal's own scroll buffer — useful for `tmux`/`screen` users who want to use their terminal's search and copy machinery on the transcript.

## Trade-off

More code for less. Termina's reactive MVVM model is powerful, but it doesn't ship the Spectre widget zoo (`Table`, `TreeView`, `Calendar`, `BarChart`). You write more rendering code yourself — every panel, every dialog. The framework's R3 dependency adds ~2 MB of IL and pulls in a separate reactive runtime alongside `System.Linq.Expressions`.

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
export HARBOR_TUI=termina
dotnet run --project apps/Harbor.App.Cli
```

## Architecture

```
src/Harbor.Tui.Termina/
├── Harbor.Tui.Termina.csproj
├── TerminaRenderer.cs            ← main IInteractiveTuiRenderer impl (dual-dispatch: legacy ChatBridge + new TeaBridge)
├── TerminaTeaBridge.cs           ← UiStore + TuiEffectHost + KeyHandler wrapper (TEA path)
├── ChatPage.cs                   ← legacy R3-reactive page (kept for backward compat)
├── Views/
│   ├── ChatView.cs               ← UiState → role-colored transcript (with markdown + GFM tables)
│   ├── InputView.cs              ← input box with caret + slash autocomplete
│   ├── StatusBarView.cs          ← provider/model/agent/tokens/cost/status/scroll%
│   ├── SessionSidebarView.cs     ← panel list (uses IPanelRegistry + UiState.PanelStates)
│   └── CommandPaletteView.cs     ← Ctrl+P fuzzy search popup
├── Handlers/
│   ├── KeyHandler.cs             ← ConsoleKeyInfo → UiKey → ChatAction → UiMsg dispatch
│   └── ScrollHandler.cs          ← pure scroll math (rows-from-bottom)
└── Rendering/
    ├── TerminaColorMapper.cs     ← ChatRole → Termina Color + label
    └── TerminaMarkdownRenderer.cs← MD headings/bold/italic/code/lists/hr + GFM tables (via shared GfmTableParser/Formatter)
```

## TEA integration

`TerminaTeaBridge` wraps `UiStore` + `TuiEffectHost` + `KeyHandler`. The renderer's `RenderAsync` dual-dispatches every `AgentEvent` to BOTH the legacy `ChatBridge` (drives the existing `ChatPage`'s R3 `StreamingTextNode`) AND the new `TeaBridge` (drives the 13-feature surface via `UiStore`). There is **no duplicate state** — the `Tea` bridge is the single source of truth for the new Views/Handlers; the legacy bridge is a backward-compat facade that will be retired once the reactive page is migrated.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full 4-way TUI/GUI comparison
- [../../docs/SPECTRE_TUI_DEEP_DIVE.md](../../docs/SPECTRE_TUI_DEEP_DIVE.md)
- [PLAN.md](PLAN.md) — what's done, what's TODO
