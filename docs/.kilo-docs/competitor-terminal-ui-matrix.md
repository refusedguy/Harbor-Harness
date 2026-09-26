# Harbor-Harness: Competitor Feature Matrix (Terminal/UI Layer)

> **Scope:** Terminal rendering, input handling, themes, sidebar/composer UX, animations, keyboard navigation, accessibility.  
> **Competitors:** OpenHands (CLI), Claude Code, Codex CLI, Kilo Code CLI, OpenCode.  
> **Data sources:** `src/`, `tests/`, `.kilo-docs/`, `Harbor-Harness-Analysis/`, live primary-source audits (2026-08-25/26).

---

## 1. Feature Matrix

### 1.1 Terminal Rendering Capabilities

| Feature | Harbor | OpenHands | Claude Code | Codex CLI | Kilo Code | OpenCode |
|---|---|---|---|---|---|---|
| **Custom cell-diff renderer** | ✅ `DiffEngine` (ScreenBuffer front/back, atomic blit) | ❌ Web/IDE only; CLI is basic REPL | ✅ Forked Ink + packed Int32Array double-buffer + ANSI diff | ✅ ratatui `DiffEngine` with gutter + hunk highlighting | ✅ Inherits OpenCode TUI (Bubble Tea) | ❌ Bubble Tea only; no cell-level diff |
| **Zero-allocation steady-state render** | ✅ Explicit budget (0 allocs/key, mouse, resize) | ❌ N/A | ❌ React reconciler allocates per frame | ⚠️ Rust zero-copy where possible, but not audited for alloc budget | ⚠️ Go Bubble Tea allocations | ❌ Go allocations per frame |
| **Virtualized chat timeline** | ✅ `VirtualizedChatTimeline` + `TimelineLayoutCache` (3-case layout: rebuild/dirty-patch/no-op) | ❌ N/A | ✅ WeakMap height-cache by message identity; viewport ±1 screen | ✅ viewport-only render + lazygit-style list context | ⚠️ Bubble Tea viewport-only | ❌ Full re-render on scroll |
| **Streaming markdown (frozen tail)** | ✅ `StreamingMarkdownRenderer` — checkpoint on block boundary, O(tail) per token | ❌ N/A | ✅ Frozen tail with reference-identity height cache | ✅ MarkdownRenderCache (single-entry, keyed by width+theme) | ⚠️ glamour cache | ❌ Re-renders on each chunk |
| **Adaptive streaming pacer** | ✅ `CommitTickPacer` (Smooth 1 line/frame, CatchUp with hysteresis: ≥8 lines OR >120ms) | ❌ N/A | ✅ Throttle 16ms + batchRender coalescing | ✅ Adaptive chunking with EXIT_HOLD/REENTER_HOLD (250ms) | ⚠️ Basic Bubble Tea tick | ❌ Fixed tick |
| **QR code rendering** | ✅ `TerminalQrRenderer` | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Image blocks in chat** | ✅ `ImageBlock` + JPEG/PNG probe | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Table rendering (GFM)** | ✅ `GfmTable` + formatter/parser | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Word-diff rendering** | ✅ `WordDiff` block | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Native scrollback integration** | ❌ Not implemented (alt-screen only in Phase 2) | ❌ N/A | ✅ `[` dumps transcript to native scrollback (tmux copy-mode works) | ✅ `insert_history.rs` writes finalized history to terminal scrollback | ❌ Alt-screen only | ❌ Alt-screen only |
| **Fullscreen rendering mode** | ❌ Planned Phase 2 (`HARBOR_TUI_FULLSCREEN=1`) | ❌ | ✅ Documented fullscreen mode with `?` help overlay | ❌ | ❌ | ❌ |
| **Multi-pane layout engine** | ✅ `LayoutTree` + `SideBarLayout` (spring-based, conditional panels) | ❌ | ✅ Yoga Flexbox via React Ink | ✅ `bottom_pane` + `app` layout with resize reflow | ⚠️ Basic Bubble Tea split | ❌ Single pane |

### 1.2 Input Handling Sophistication

| Feature | Harbor | OpenHands | Claude Code | Codex CLI | Kilo Code | OpenCode |
|---|---|---|---|---|---|---|
| **Raw-mode stdin parser** | ✅ State-machine parser (ECMA-48 + kitty + SGR mouse + paste), zero-alloc on hot path | ❌ | ✅ Custom parse-keypress (xterm/VT) | ✅ Escape sequence state machine | ⚠️ Bubble Tea handles input | ⚠️ Bubble Tea handles input |
| **Kitty keyboard protocol** | ✅ Probe + push/pop flags (disambiguate), graceful legacy fallback, timeout configurable | ❌ | ❌ Not documented | ❌ | ❌ | ❌ |
| **SGR mouse (1000/1002/1006)** | ✅ Click + wheel; drag (1002) planned v2 | ❌ | ✅ Drag-selection + clipboard | ✅ Wheel + click tracking | ⚠️ Bubble Tea mouse zones | ⚠️ Limited mouse support |
| **Bracketed paste + injection protection** | ✅ `PasteAdapter` — newlines don't submit, ESC-in-paste blocked, 512B truncation, 10s timeout | ❌ | ✅ Bracketed paste (implied by multiline) | ✅ Paste handling | ⚠️ Basic paste | ⚠️ Basic paste |
| **Prompt buffer (grapheme-aware)** | ✅ `PromptBuffer` — grapheme/word/line navigation, kill/yank, stale-plan generation, `SingleLineViewport` | ❌ | ✅ Readline-style (Ctrl+A/E/K/U/W/Y) | ✅ TextArea with kill-ring + wrap-cache | ⚠️ Basic textarea | ⚠️ Basic textarea |
| **Vim editing mode** | ✅ `VimComposerMode` — h/l/w/b/0/$/x/A/I/a + j/k history | ❌ | ✅ Full vim mode (text objects, visual mode, remaps, `jj`→Esc) | ❌ | ❌ | ❌ |
| **Leader-key chords** | ✅ `LeaderKeyRouter` (ctrl+x, 1500ms timeout, single-char bindings) | ❌ | ❌ | ❌ | ✅ `ctrl+x` leader with JSON schema + 2000ms timeout | ✅ `ctrl+x` leader with JSON schema |
| **Quick-switch slots** | ✅ 9 MRU slots (`<leader>1..9`) | ❌ | ❌ | ❌ | ❌ | ❌ |
| **History search (Ctrl+R)** | ✅ Basic reverse search | ❌ | ✅ Inline + fullscreen dialog with scope cycling (session→project→all) | ❌ | ⚠️ Basic history | ❌ |
| **Stash/restore prompt** | ❌ Not implemented | ❌ | ✅ Ctrl+S stash + restore | ❌ | ❌ | ❌ |
| **Prompt suggestions** | ❌ Not implemented | ❌ | ✅ Git-history starter + next-prompt suggestions (reuses cache) | ❌ | ❌ | ❌ |
| **Queue messages with delivery semantics** | ❌ Not implemented | ❌ | ✅ Enter during work → queue; delivered after tool calls or next turn; Esc preserves queue | ❌ | ❌ | ❌ |
| **Side-channel Q&A (/btw)** | ❌ Not implemented | ❌ | ✅ `/btw` — answers from context only, never in history, overlay, fork with `f` | ❌ | ❌ | ❌ |
| **Esc-Esc rewind** | ❌ Not implemented | ❌ | ✅ Double-Esc clears draft or opens rewind menu | ❌ | ❌ | ❌ |
| **Emoji shortcodes** | ❌ Not implemented | ❌ | ✅ `:name:` completion + suggestion popup | ❌ | ❌ | ❌ |
| **Spellcheck** | ❌ Not implemented | ❌ | ✅ aspell/hunspell/ispell integration, underline misspellings | ❌ | ❌ | ❌ |
| **Voice dictation** | ❌ Not implemented | ❌ | ✅ Hold Space / tap-to-toggle | ❌ | ❌ | ❌ |
| **External editor (Ctrl+G)** | ❌ Not implemented | ❌ | ✅ Opens `$VISUAL/$EDITOR`, prepends Claude reply as `#` comments | ✅ `/editor` command | ⚠️ `/editor` via EDITOR env | ✅ `/editor` via EDITOR env |
| **Background shells** | ❌ Not implemented | ❌ | ✅ Ctrl+B background + task tracking, auto-cleanup on exit | ❌ | ❌ | ❌ |
| **Permission mode cycling** | ❌ Basic approval gate only | ❌ | ✅ Shift+Tab cycles: default → acceptEdits → plan → bypass → auto | ❌ | ❌ | ❌ |

### 1.3 Theme / Live-Reload Systems

| Feature | Harbor | OpenHands | Claude Code | Codex CLI | Kilo Code | OpenCode |
|---|---|---|---|---|---|---|
| **JSON theme loader** | ✅ `JsonThemeLoader` — 13-token HarborTheme (dark/light + overrides) | ❌ | ✅ `/theme` picker (classic renderer) | ❌ | ✅ Theme support | ✅ `/themes` + live preview |
| **Live theme reload** | ✅ `ThemeFileWatcher` — polls file every 500ms, applies on change, parse-failure safe | ❌ | ❌ | ❌ | ❌ | ❌ (requires restart) |
| **OSC 11 auto-theme detection** | ✅ `TerminalBackgroundProbe` — parses `rgb:RR/GG/BB`, WCAG luminance threshold | ❌ | ❌ | ❌ | ❌ | ❌ |
| **WCAG contrast math** | ✅ `Accessibility.cs` — relative luminance, AA/AAA thresholds (4.5:1, 7:1, 3:1 UI) | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Theme tokens (design system)** | ✅ `HarborTheme` record + `TerminalColorPalette.Apply` + `ThemeOverrideSet` | ❌ | ❌ | ❌ | ❌ | ❌ |

### 1.4 Sidebar / Composer UX

| Feature | Harbor | OpenHands | Claude Code | Codex CLI | Kilo Code | OpenCode |
|---|---|---|---|---|---|---|
| **Right sidebar (auto-show on wide terminals)** | ✅ `SideBarView` — 42-col context panel, plugin-extensible slots, MCP/LSP status | ❌ | ❌ | ❌ | ✅ Sidebar toggle (`<leader>b`) | ✅ Sidebar toggle |
| **MCP server connectivity status** | ✅ `McpServerStatus` per row (Connected/Connecting/Error) | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Token/cost HUD in sidebar** | ✅ `SideBarState` — TokensIn/Out, CostUsd, ModifiedFiles | ❌ | ❌ | ❌ | ✅ Latency in status bar | ❌ |
| **Composer with atomic elements** | ✅ `ComposerController` — file refs, slash commands, bash-mode toggle as atomic elements | ❌ | ✅ `/` commands, `@` mentions, `!` shell mode | ✅ Popup routing for slash/file-search | ✅ `/` commands, `@` mentions, `!` shell mode | ✅ `/` commands, `@` mentions, `!` shell mode |
| **Approval gate inline in stream** | ✅ `ApprovalGateView` — hotkeys y/n/e, typed request with rule trace | ❌ | ✅ Permission prompts with comment field | ✅ `ApprovalOverlay` queue + hotkeys | ✅ Permission prompts | ✅ Permission prompts |
| **Status bar with segments** | ✅ `StatusSegmentBar` — typed segments, None-semantics, priority truncation, mode machine (Idle/Running/Awaiting/Compacting) | ❌ | ✅ Mode indicator + status text | ✅ Footer state machine | ✅ Status bar | ✅ Status bar |
| **Spinner / busy indicator** | ✅ `SpinnerStrip` — ASCII+Unicode frames, tick-driven, distinct working vs awaiting rhythms | ❌ | ✅ Pulsing bullet vs wave | ✅ ASCII animation frames | ❌ | ❌ |
| **Command palette** | ✅ `CommandPaletteView` + `FuzzyMatcher` — filters commands, persists across restarts | ❌ | ❌ | ❌ | ✅ Command palette (`ctrl+p`) | ✅ Command palette (`ctrl+p`) |
| **Session timeline / tree navigation** | ❌ Not implemented | ❌ | ❌ | ❌ | ✅ `<leader>g` timeline + child/parent navigation | ✅ Session timeline + child/parent/sibling navigation |
| **Undo/redo via Git snapshots** | ❌ Not implemented | ❌ | ❌ | ❌ | ❌ | ✅ `/undo` + `/redo` reverts messages + files |
| **Export / share session** | ❌ Not implemented | ❌ | ❌ | ❌ | ✅ `/export` markdown, `/share` public link | ✅ `/export` markdown, `/share` public link |
| **Prompt stash/pop/list** | ❌ Not implemented | ❌ | ❌ | ❌ | ❌ | ✅ Prompt stash/pop/list |
| **Model favorites + cycling** | ❌ Not implemented | ❌ | ❌ | ❌ | ✅ Favorites + F2 cycle recent | ✅ Favorites + F2 cycle recent |

### 1.5 Animation Systems

| Feature | Harbor | OpenHands | Claude Code | Codex CLI | Kilo Code | OpenCode |
|---|---|---|---|---|---|---|
| **Declarative transitions** | ✅ `FadeTransition`, `SlideTransition`, `ScaleTransition` with easing curves | ❌ | ✅ React transitions + useSyncExternalStore | ❌ | ❌ | ❌ |
| **Spring physics** | ✅ `SpringFx` for panel layout animations | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Panel entrance/exit effects** | ✅ `PanelFx` — SlideMs, FadeMs, opt-in per panel | ❌ | ✅ Modal/palette transitions | ❌ | ❌ | ❌ |
| **Smooth scrolling** | ✅ `SmoothScroll` in `VirtualizedChatTimeline` | ❌ | ❌ | ❌ | ✅ Scroll acceleration (macOS-style) | ✅ Scroll acceleration |
| **Entrance animations for blocks** | ✅ HDS v1 entrance (slide-up + fade) for new chat blocks | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Frame-budget governor** | ❌ Not implemented | ❌ | ❌ | ❌ | ❌ | ❌ |

### 1.6 Keyboard-First Navigation

| Feature | Harbor | OpenHands | Claude Code | Codex CLI | Kilo Code | OpenCode |
|---|---|---|---|---|---|---|
| **Leader-key system** | ✅ `LeaderKeyRouter` + `QuickSwitchSlots` | ❌ | ❌ | ❌ | ✅ JSON-schema leader keybindings | ✅ JSON-schema leader keybindings |
| **Named command registry for bindings** | ❌ Not centralized | ❌ | ✅ Keybindings JSON with conflict detection + autogen docs | ❌ | ✅ Named commands + autogen docs | ✅ Named commands + JSON schema |
| **Which-key overlay** | ❌ Not implemented | ❌ | ❌ | ❌ | ❌ | ✅ `which_key_*` bindings + overlay |
| **Focus router (panel focus)** | ✅ `FocusRouter` — click focuses panel, keyboard focus management | ❌ | ✅ Capture/bubble dispatcher for overlapping contexts | ❌ | ❌ | ❌ |
| **Vim normal mode (composer)** | ✅ `VimComposerMode` | ❌ | ✅ Full vim mode (text objects, visual, remaps) | ❌ | ❌ | ❌ |
| **Readline emacs mode** | ✅ Ctrl+A/E/K/U/W/Y in composer | ❌ | ✅ Classic + readline flavor toggle | ❌ | ❌ | ❌ |
| **Multiline input (Shift+Enter)** | ✅ Kitty protocol splits Shift+Enter from Enter; fallback Ctrl+J | ❌ | ✅ Shift+Enter (native in modern terminals) + Ctrl+J fallback | ❌ | ⚠️ Terminal config required | ⚠️ Terminal config required |
| **History per working directory** | ✅ `PromptHistory` | ❌ | ✅ Per-cwd history across sessions | ❌ | ❌ | ❌ |

### 1.7 Accessibility Features

| Feature | Harbor | OpenHands | Claude Code | Codex CLI | Kilo Code | OpenCode |
|---|---|---|---|---|---|---|
| **WCAG contrast validation** | ✅ `Accessibility.cs` — AA/AAA ratio math, used by every Harbor surface | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Auto light/dark theme detection** | ✅ `TerminalBackgroundProbe` (OSC 11 → luminance → theme pick) | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Screen-reader awareness** | ⚠️ Design tokens exist; no explicit screen-reader mode documented | ❌ | ✅ Spellcheck skipped in screen-reader mode | ❌ | ❌ | ❌ |
| **Focus indicators** | ✅ HDS tokens for focus rings, validated against WCAG 3:1 | ❌ | ✅ Focus management in dialogs | ❌ | ❌ | ❌ |

---

## 2. What Harbor Has That Competitors DON'T

### Unique to Harbor (not found in any listed competitor)

| # | Feature | Why It's Unique |
|---|---|---|
| 1 | **Cell-grid diff renderer (`DiffEngine`)** | Only Harbor has a true cell-buffer diff engine with atomic blit; competitors use line-based or widget-based diffs. |
| 2 | **Explicit zero-allocation budget** | Harbor is the only one with a documented alloc budget per input event and `PerfBudgetTests`. |
| 3 | **Ambient mascot (Petdex-style)** | `AmbientMascot` — tick-driven ASCII cat reflecting agent state; zero alloc, deterministic. |
| 4 | **QR code rendering in terminal** | `TerminalQrRenderer` for pairing codes / deep links; no competitor has this built-in. |
| 5 | **Spring-physics panel layout** | `SpringFx` + `LayoutTree` with conditional panels; competitors use static splits or Yoga flexbox. |
| 6 | **Live theme file watcher** | `ThemeFileWatcher` polls JSON theme file at 500ms and hot-swaps; competitors require restart for theme changes. |
| 7 | **OSC 11 terminal background probe** | Auto-detects terminal background color and picks light/dark theme; unique among CLI agents. |
| 8 | **WCAG contrast math built into design system** | `Accessibility.cs` with AA/AAA thresholds used by every surface; competitors have no programmatic contrast validation. |
| 9 | **Leader-key router + quick-switch slots** | `<leader>1..9` MRU session slots + chord router; OpenCode has leader chords but not the quick-switch slots. |
| 10 | **Bracketed paste injection protection** | Explicit anti-injection invariant (newlines don't submit, ESC-in-paste blocked, truncation); competitors handle paste but don't document injection protection. |
| 11 | **Kitty keyboard protocol with probe + fallback** | Only Harbor (and gemini-cli, per analysis) has an explicit capability probe for kitty protocol with graceful fallback. |
| 12 | **Approval gate with rule trace** | Explains *why* permission was asked (which rule matched/missed); competitors have black-box permission prompts. |
| 13 | **Virtualized chat timeline with 3-case layout** | `TimelineLayoutCache` with Case1 rebuild / Case2 dirty-patch / Case3 no-op; inspired by grok/lazygit but uniquely implemented in C# with zero steady-state allocs. |

---

## 3. What Competitors Have That Harbor Lacks

| # | Feature | Competitor(s) | Notes |
|---|---|---|---|
| 1 | **Queue messages with delivery semantics** | Claude Code | Enter during work queues messages; delivered after tool calls or next turn. |
| 2 | **Virtual scroll with identity-based height cache** | Claude Code | WeakMap keyed by message reference; resize doesn't re-measure unchanged messages. |
| 3 | **Dump transcript to native scrollback** | Claude Code | `[` writes ANSI transcript to stdout scrollback; enables tmux copy-mode / Cmd+F. |
| 4 | **Scope-cycling reverse history search** | Claude Code | Ctrl+R scope: session → project → all projects; Ctrl+S cycles. |
| 5 | **Side-channel Q&A (`/btw`)** | Claude Code | Answers from context only, never in history, overlay, forkable. |
| 6 | **Esc-Esc rewind / draft restore** | Claude Code | Double-Esc clears draft or opens rewind menu. |
| 7 | **Prompt suggestions (git history + next-turn)** | Claude Code | Starter from git history; next-prompt suggestions reusing prompt cache. |
| 8 | **Session recap (auto + `/recap`)** | Claude Code | Auto-generated when terminal unfocused for 3+ min; ≤400 chars. |
| 9 | **Background task management** | Claude Code | Ctrl+B backgrounds Bash; task IDs, output files, auto-cleanup, memory-pressure reap. |
| 10 | **Full vim mode (text objects + visual + remaps)** | Claude Code | i/a/A/I/o/O/v/V, text objects (iw/aw/i"/a"/i(/a(…), `.` repeat. |
| 11 | **Spellcheck integration** | Claude Code | aspell/hunspell/ispell; underlines misspellings in prompt. |
| 12 | **Voice dictation** | Claude Code | Hold Space / tap-to-toggle. |
| 13 | **Session tree navigation (child/parent/sibling)** | OpenCode | `<leader>down/right/left/up` navigates subagent sessions. |
| 14 | **Undo/redo via Git snapshots** | OpenCode | `/undo` reverts messages + files; `/redo` restores. |
| 15 | **Export / share session** | OpenCode | `/export` markdown + editor; `/share` public link. |
| 16 | **Prompt stash/pop/list** | OpenCode | Stash prompts without submitting; pop later. |
| 17 | **Model favorites + cycling** | OpenCode | Favorites + F2 cycle recent. |
| 18 | **JSON-schema leader keybindings with conflict detection** | OpenCode | `tui.json` with `$schema`, named commands, `preventDefault`, `fallthrough`. |
| 19 | **Which-key overlay** | OpenCode | `ctrl+alt+k` shows pending leader chords. |
| 20 | **Scroll acceleration (macOS-style)** | OpenCode | Smooth, natural scrolling with speed increasing on rapid gestures. |
| 21 | **Attention system (sounds + desktop notifications)** | OpenCode | Configurable sound packs + OS notifications when terminal blurred. |
| 22 | **Live theme preview in picker** | OpenCode | `/themes` shows real-time preview on hover, reverts if not confirmed. |
| 23 | **Deferred onboarding (free immediately)** | Kilo Code | Works on free models without auth; `/connect` asks for keys contextually. |
| 24 | **Auto Model tiers (frontier/efficient/free)** | Kilo Code | Server-side routing with benchmark-proven pools; difficulty classification. |
| 25 | **Rival session import** | Kilo Code | `/resume-claude` and `/resume-codex` import transcripts. |
| 26 | **Two-phase streaming (stream-cell → consolidated)** | Codex CLI | Live tail = temporary stream-cell; finalized = source-backed cell with cache. |
| 27 | **Approval queue with hotkeys** | Codex CLI | `ApprovalOverlay` with current request + queue, dismiss filter, advance. |
| 28 | **History written to terminal scrollback** | Codex CLI | `insert_history.rs` writes finalized history to terminal; alt-screen only for overlays. |
| 29 | **Trust directory step in onboarding** | Codex CLI | Explicit trust decision before working in a folder. |
| 30 | **Config profiles as separate files** | Codex CLI | `$CODEX_HOME/<name>.config.toml`; project config cannot override provider/auth. |
| 31 | **Web-based terminal interface** | OpenHands | `openhands web` runs same TUI in browser; useful for remote/sharing. |
| 32 | **Multi-runner (IDE + cloud + CI)** | OpenHands | Runs in VS Code, JetBrains, browser, CI, OpenHands Cloud. |

---

## 4. "Wow" Shortlist — Harbor's True Differentiators

These are features where Harbor is **ahead of the field** or **unique** in the terminal/UI layer.

### 1. Cell-Grid Diff Engine with Atomic Blit
Harbor's `DiffEngine` operates on a `ScreenBuffer` (front/back) and diffs at the cell level, not line level. This enables flicker-free updates, partial-region invalidation, and zero-flicker resize — something no competitor has in a CLI agent. Codex and grok do line-based diffs; Claude Code does React VDOM diffing. Harbor's cell-grid approach is architecturally superior for terminal rendering.

### 2. Explicit Zero-Allocation Budget + AOT Safety
Harbor is the only agent with a documented alloc budget per frame (`PerfBudgetTests`) and a renderer built for NativeAOT (`PublishAot=true`, `AllowUnsafeBlocks=false`, source-generated `LibraryImport`). The entire ConsoleEx pipeline is designed around `ReadOnlySpan<char>` and `Memory<byte>` — no string building in hot paths. This matters for long-running terminal sessions.

### 3. Kitty Keyboard Protocol Probe + Graceful Fallback
Only Harbor and gemini-cli have an explicit capability probe for kitty keyboard protocol. Harbor's implementation is more complete: push/pop flags, timeout configuration, graceful degradation to legacy SS3/CSI codes, and integration with the `ComposerController` so Shift+Enter is properly distinguished from Enter. This enables true multiline editing without terminal hacks.

### 4. Semantic Bracketed Paste with Injection Protection
Harbor's paste handling is security-conscious: newlines inside paste never trigger submit, ESC sequences inside paste are not interpreted, large pastes truncate with a placeholder, and there's a hard timeout on unclosed paste states. No competitor documents this level of paste-injection defense.

### 5. Design System with WCAG Contrast Validation
Harbor is the only CLI agent with a programmatic design system (`HarborTheme`, `TerminalColorPalette`, `Accessibility.cs`) that validates WCAG 2.x contrast ratios (AA/AAA) and auto-detects terminal background via OSC 11. This isn't just "having themes" — it's having a *validated token system* that ensures focus indicators, text, and borders meet accessibility thresholds.

---

## 5. Summary

- **Harbor's strongest areas:** rendering architecture (cell-diff, virtualized timeline, frozen-tail markdown), input sophistication (kitty probe, grapheme-aware buffer, vim mode, leader chords), design-system rigor (themes, WCAG, live reload), and animation infrastructure (spring physics, declarative transitions).
- **Biggest gaps vs competitors:** message queueing with delivery semantics, native scrollback dump, session undo/redo via Git, session tree navigation, background shell management, prompt suggestions, and attention system (sounds/notifications).
- **Competitor benchmarks to watch:** Claude Code sets the bar for interactive polish (queue, rewind, `/btw`, spellcheck, voice); OpenCode sets the bar for keyboard ergonomics (leader-key schema, which-key, session tree, undo/redo); Codex CLI sets the bar for streaming discipline (two-phase commit, adaptive chunking); Kilo Code sets the bar for onboarding friction (deferred auth, auto-model tiers).
