# Plan — Harbor.Tui.TerminalGui

## Status: Competitive with SpectreTui (TEA bridge active)

## Done — 13 common features

- [x] **Streaming chat with role colors** — `TerminalGuiColorMapper` maps ChatRole → Terminal.Gui v2 `Color` enum. User=bright-green, assistant=white, thinking=dark-gray, tool=bright-blue, tool-result=gray, error=bright-red, system=gray.
- [x] **Role headers** — `─ user ─`, `─ assistant ─` etc. via `TerminalGuiMarkdownRenderer.RenderHeader`.
- [x] **Markdown rendering** — headings, bold, italic, inline code, bullet/numbered lists, horizontal rule. Inline markers stripped (Terminal.Gui TextView applies color per-Attribute, not per-char).
- [x] **GFM table rendering** — pipe-separated tables with alignment via shared `GfmTableParser`/`GfmTableFormatter`.
- [x] **Scrollable history** — `ScrollHandler.VisibleSlice` + `TextView.CurrentRow`/`MoveEnd` for smart scroll.
- [x] **Input box** — `InputView` projects `InputModel`; multi-line, history navigation (Alt+↑/↓), slash autocomplete (Tab).
- [x] **Status bar** — `StatusBarView` shows provider/model/agent/tokens-in/tokens-out/$cost/status/scroll%.
- [x] **Stream bar** — `ChatView.StreamBar` returns "▌ generating... N chars" or "▌ thinking... N chars".
- [x] **Hotkeys** — Enter/Shift+Enter/Esc/Ctrl+L/Ctrl+C/F2/↑↓/PgUp/PgDn/Home/End/Alt+↑↓/Tab via `KeyHandler`.
- [x] **Session sidebar** — `SessionSidebarView` lists `PanelRegistry.All` with `UiState.PanelStates` visibility.
- [x] **Command palette** — `CommandPaletteView` Ctrl+P popup over slash commands + panels + sessions.
- [x] **Toast notifications** — `TerminalGuiTeaBridge.Toast` / `DequeueToast` queue with auto-dismiss contract.
- [x] **TEA integration** — `TerminalGuiTeaBridge` wraps `UiStore`/`UiReducer`/`TuiEffectHost`; renderer dispatches `UiMsg.KeyInput` and runs returned effects via the host.

## Done — Terminal.Gui-specific stretch (partial)

- [x] **Dark theme enforcement** — `SchemeManager.AddScheme("dark", …)` with `Color.White`/`Color.Black` palette overrides Terminal.Gui's default blue-on-cyan. The `Window`, `TextView`, and `Label` all inherit this scheme.
- [x] **20-FPS throttle** — `TerminalGuiScreen._renderTimer` ticks every 50 ms; `_isDirty` flag coalesces streaming deltas so the `TextView.Text` reassignment happens at most 20×/sec.
- [x] **Smart scroll** — `_wasAtBottom` tracks whether the user was reading history; only auto-scrolls to bottom if so.

## TODO

- [ ] **Dim/percent layouts** — replace `Dim.Fill(5)` magic numbers with a `LayoutBuilder` that takes percent specs (`40%` / `60%` splits).
- [ ] **Sub-windows** — pop up a `Dialog` for `/permissions`, `/sessions`, `/tui` instead of inline rendering.
- [ ] **MenuBar** — top-of-screen `MenuBar` with File / Edit / Session / Help menus exposing every slash command.
- [ ] **TextView → UiStore projection** — replace `_finalizedText` StringBuilder with a `TextView` that reads `UiStore.State.Lines` directly + applies role colors via `Attribute` runs.
- [ ] **Mouse support** — Terminal.Gui v2 ships mouse handling out of the box; wire scroll-wheel → `UiMsg.ScrollUpLine` / `ScrollDownLine`, click-on-panel → `UiMsg.FocusPanel(id)`.
- [ ] **Toast timer** — render-side 4s timer that calls `DequeueToast` and renders bottom-right.

## Known issues

- `TerminalGuiScreen` keeps its own `_chat`/`_streamBuffer`/`_thinkBuffer` for the live `TextView`. This is duplicate state that violates the TEA contract — slated for removal in a follow-up.
- `Application.Create().Init()` blocks the calling thread; the renderer works around this with `IApplication.Invoke` for agent-event marshaling.
- Terminal.Gui v2's `Key` is a struct, not an enum — `KeyHandler` accepts `ConsoleKeyInfo` (BCL) and the renderer converts, sidestepping the v2 API complexity.
