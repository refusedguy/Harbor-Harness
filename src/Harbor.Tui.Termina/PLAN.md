# Plan — Harbor.Tui.Termina

## Status: Competitive with SpectreTui (TEA bridge active)

## Done — 13 common features

- [x] **Streaming chat with role colors** — `TerminaColorMapper` + 24-bit ANSI escapes via `TerminaMarkdownRenderer.Ansi`. User=green, assistant=white, thinking=grey italic, tool=blue, tool-result=grey, error=red, system=grey.
- [x] **Role headers** — `─ user ─`, `─ assistant ─` etc. via `TerminaMarkdownRenderer.RenderHeader`.
- [x] **Markdown rendering** — headings (#/##/###), bold (**), italic (*), code (`), bullet/numbered lists, horizontal rule (---).
- [x] **GFM table rendering** — pipe-separated tables with alignment via shared `GfmTableParser`/`GfmTableFormatter`.
- [x] **Scrollable history** — `ScrollHandler.VisibleSlice` reads `UiState.ScrollOffset`; ↑/↓/PgUp/PgDn/Home/End all dispatched via `UiMsg.KeyInput`.
- [x] **Input box** — `InputView` projects `InputModel`; multi-line, history navigation (Alt+↑/↓), slash autocomplete (Tab) via `ChatCommands.Slash`.
- [x] **Status bar** — `StatusBarView` shows provider/model/agent/tokens-in/tokens-out/$cost/status/scroll%.
- [x] **Stream bar** — `ChatView.StreamBar` returns "▌ generating... N chars" or "▌ thinking... N chars".
- [x] **Hotkeys** — Enter/Esc/Ctrl+L/Ctrl+C/F2/↑↓/PgUp/PgDn/Home/End/Alt+↑↓/Tab via `KeyHandler` → `ChatKeyMap` → `UiMsg`.
- [x] **Session sidebar** — `SessionSidebarView` lists `PanelRegistry.All` with `UiState.PanelStates` visibility; new/branch/delete via slash commands.
- [x] **Command palette** — `CommandPaletteView` Ctrl+P popup over slash commands + panels + sessions with fuzzy `Contains` filter.
- [x] **Toast notifications** — `TerminaTeaBridge.Toast` / `DequeueToast` queue with auto-dismiss contract (renderer polls every 4s).
- [x] **TEA integration** — `TerminaTeaBridge` wraps `UiStore`/`UiReducer`/`TuiEffectHost`; renderer dispatches `UiMsg.KeyInput` and runs returned effects via the host — no direct state mutation.

## Done — Termina-specific stretch

- [x] **ANSI precision** — `TerminaMarkdownRenderer.Ansi` emits explicit `<ESC>[38;2;R;G;Bm` sequences (no Spectre-style quantization). Bold/italic encoded as `<ESC>[1m` / `<ESC>[3m`.

## TODO

- [ ] **Inline viewport mode** — pin UI to bottom N rows; history flows into native scrollback (grok-build style). Requires a custom `ReactivePage` that overrides `BuildLayout` to reserve bottom rows + prints history via raw `Console.Write` to native scrollback.
- [ ] **Kitty keyboard protocol** — query `CSI ? 2017 h` on startup, fall back to legacy escape sequences. Lets us distinguish Ctrl+Enter from Enter etc.
- [ ] **Synchronized output (DCS)** — wrap each repaint in `<ESC>[?2026h` … `<ESC>[?2026l` so partial frames never tear.
- [ ] **Migrate `ChatPage` to `TeaBridge`** — replace the R3 `Subject<ChatLine>` with `UiStore.State.Lines` projection; remove `ChatBridge`.
- [ ] **Toast timer** — render-side 4s timer that calls `DequeueToast` and renders bottom-right; today it's a queue with no auto-dismiss.

## Known issues

- The legacy `ChatBridge` (R3 `Subject<ChatLine>`) is still wired in parallel with the new `TeaBridge` — both receive every `AgentEvent`. Removing it requires migrating `ChatPage` to project from `UiStore`. Done in a follow-up.
- `Termina.Generators` source generator is removed from `CoreCompile` (existing workaround for .NET 10 conflict); design-time IntelliSense still works.
