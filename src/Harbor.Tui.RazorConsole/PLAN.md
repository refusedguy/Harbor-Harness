# Plan — Harbor.Tui.RazorConsole

## Status: Competitive with SpectreTui (TEA bridge active)

## Done — 13 common features

- [x] **Streaming chat with role colors** — `RazorColorMapper` maps ChatRole → Spectre `Color` + markup string (e.g. `"green"`, `"grey italic"`). Output is Spectre markup so `ChatTui.razor`'s `<Markup>` component renders it natively.
- [x] **Role headers** — `─ user ─`, `─ assistant ─` etc. via `RazorMarkdownRenderer.RenderHeader` (Spectre markup).
- [x] **Markdown rendering** — headings, bold, italic, inline code, bullet/numbered lists, horizontal rule. Output is Spectre markup with proper `[color]…[/]` wrapping + `[[` / `]]` escaping for `[`/`]` in user text.
- [x] **GFM table rendering** — pipe-separated tables with alignment via shared `GfmTableParser`/`GfmTableFormatter`, each row wrapped in `[grey]…[/]`.
- [x] **Scrollable history** — `ScrollHandler.VisibleSlice` reads `UiState.ScrollOffset`; `ChatTui.razor` uses `<ViewHeightScrollable LinesToRender="20">` for the viewport.
- [x] **Input box** — `InputView` projects `InputModel`; the `ChatTui.razor` `<TextInput @bind-Value="_input" OnSubmit="Submit"/>` handles multi-line + submit. Slash autocomplete marker rendered inline.
- [x] **Status bar** — `StatusBarView` shows provider/model/agent/tokens-in/tokens-out/$cost/status/scroll% in Spectre markup.
- [x] **Stream bar** — `ChatView.StreamBar` returns "▌ generating... N chars" or "▌ thinking... N chars".
- [x] **Hotkeys** — Enter/Esc/Ctrl+L/Ctrl+C/F2/↑↓/PgUp/PgDn/Home/End/Alt+↑↓/Tab via `KeyHandler`.
- [x] **Session sidebar** — `SessionSidebarView` lists `PanelRegistry.All` with `UiState.PanelStates` visibility.
- [x] **Command palette** — `CommandPaletteView` Ctrl+P popup over slash commands + panels + sessions.
- [x] **Toast notifications** — `RazorConsoleTeaBridge.Toast` / `DequeueToast` queue with auto-dismiss contract.
- [x] **TEA integration** — `RazorConsoleTeaBridge` wraps `UiStore`/`UiReducer`/`TuiEffectHost`; renderer dispatches `UiMsg.KeyInput` and runs returned effects via the host.

## Done — RazorConsole-specific stretch (partial)

- [x] **Reusable component** — `ChatTui.razor` already factors the chat bubble into a `RenderFragment Message(string role, string text)` with a `RoleStyle` lookup. This is the canonical example of the component-model stretch.
- [x] **Auto-clear render** — `ConsoleAppOptions.AutoClearConsole = true` so each render repaints the full frame (no overlapping streamed output).
- [x] **Terminal resizing** — `ConsoleAppOptions.EnableTerminalResizing = true` so the layout reflows on `SIGWINCH`.

## TODO

- [ ] **`Components/ChatMessage.razor`** — extract the `Message` RenderFragment into a standalone `.razor` component with `[Parameter] string Role { get; set; }` and `[Parameter] string Text { get; set; }`.
- [ ] **`Components/InputBox.razor`** — wrap `<TextInput>` with the slash-autocomplete + history-navigation affordances.
- [ ] **`Components/StatusBar.razor`** — project `UiState` into the status bar markup.
- [ ] **Migrate `ChatTui.razor` to `TeaBridge`** — replace `@inject ChatBridge Harbor` with `@inject RazorConsoleTeaBridge Harbor`; bind to `Harbor.Store.State` instead of `Harbor.Messages`.
- [ ] **Hot reload** — verify `dotnet watch run` actually hot-reloads `.razor` edits without restarting the agent loop.
- [ ] **Toast timer** — render-side 4s timer that calls `DequeueToast` and renders bottom-right.

## Known issues

- `ChatBridge` (legacy) duplicates `UiStore` state — `Messages`, `StreamBuffer`, `ThinkBuffer`, `Status`, `IsRunning`. Slated for removal once `ChatTui.razor` projects from `UiStore.State`.
- `ConsoleAppOptions.AutoClearConsole = true` means each frame is a full repaint — no per-cell differential updates. Acceptable for chat, but a perf cliff at 1000+ lines.
- Razor SDK adds ~6 MB of tooling and slows the build by ~2-3 sec.
