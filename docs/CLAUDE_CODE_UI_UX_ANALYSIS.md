# Claude Code (Anthropic) — Detailed UI/UX Analysis

**Analysis date:** 2026-08-27  
**Subject:** Claude Code terminal UI/UX, visual design, interaction patterns, accessibility, and killer features  
**Sources:** Official documentation, source-code reverse engineering (claude-code-from-source, TUICommander, AILmanac), community implementations, and architectural deep-dives.

---

## 1. Terminal UI Layout and Components

### 1.1 Overall Layout Anatomy (bottom-to-top)

Claude Code uses a **retained-mode terminal UI** built on a heavily forked version of Ink (React for terminals). The screen is organized in a strict vertical stack:

```
[agent output / response text]
[empty line(s)]
✶ Undulating… (1m 32s · ↓ 2.2k tokens)   ← spinner + timer while working
───────────────────────────────────────────  ← upper separator (box-drawing line)
❯ [user input]                               ← prompt line (bottom-most interactive row)
───────────────────────────────────────────  ← lower separator
[status line(s)] (0-N lines, indented 2 spaces)
[mode line] (last row, indented 2 spaces)
```

**Key observations:**
- The prompt is **always the last interactive row**, mimicking a chat-app feel inside a terminal.
- Separators use box-drawing characters (`─`, `━`, `═`, `╌`, `╍`) and may contain embedded labels (e.g., `─── extractor ───`).
- Empty lines between content and spinner create breathing room — a deliberate pacing choice.

### 1.2 Core Components

| Component | Role |
|-----------|------|
| `ink-box` | Flexbox container (terminal `<div>`). Supports padding, margin, gap, flex-grow/shrink, alignment, and wrapping. |
| `ink-text` | Text node with Yoga measure function for word wrapping. Handles Unicode grapheme clusters, CJK double-width, emoji sequences, and embedded ANSI codes. |
| `ink-virtual-text` | Nested styled text inside another text node (auto-promoted from `ink-text`). |
| `ink-link` | Hyperlinks rendered via OSC 8 escape sequences. |
| `ink-progress` | Progress indicator. |
| `ink-raw-ansi` | Pre-rendered ANSI content with known dimensions, used for syntax-highlighted code blocks. |
| `Static` (Ink concept) | Permanent output that never re-renders. Used for logs and completed items. |

### 1.3 Renderer Modes

Claude Code supports two renderer modes:

| Mode | Screen ownership | Operational difference |
|------|------------------|------------------------|
| `default` / classic | Terminal’s normal scrollback buffer | Output grows in the main screen; renderer patches the visible tail. |
| `fullscreen` | DEC alternate screen (`?1049h`) | Claude Code owns a terminal-sized viewport, can use sticky scrolling/focus view, and restores the prior screen on exit. |

- **Screen-reader mode forces classic** and uses a separate accessibility-tree diff.
- **Reduced motion** can stop animations without necessarily changing every other accessibility behavior.
- **Focus view** needs fullscreen because it depends on an owned viewport; `/focus` directs classic users to `/tui fullscreen`.
- **Background attachment** forces fullscreen independently of the renderer chosen for sessions launched directly with `claude`.
- Auto-detection disables fullscreen on known-incompatible paths: `tmux -CC`, Windows-over-SSH (ConPTY re-rendering), unless explicitly forced.

---

## 2. Visual Design (Colors, Typography, Spacing, Animations)

### 2.1 Colors and Theme System

Claude Code ships with a **typed Theme contract** exposing 80+ semantic color tokens:

```typescript
// utils/theme.ts (excerpt)
export type Theme = {
  claude: string          // Claude orange
  claudeShimmer: string   // lighter variant for shimmer animations
  permission: string      // permission confirmation
  green: string           // success
  error: string           // red
  warning: string         // amber
  // Diff colors (4 levels + 2 word-level highlights)
  diffAdded: string
  diffRemoved: string
  diffAddedDimmed: string
  diffRemovedDimmed: string
  diffAddedWord: string
  diffRemovedWord: string
  // Agent color palette (8 colors for parallel agents)
  red_FOR_SUBAGENTS_ONLY: string
  blue_FOR_SUBAGENTS_ONLY: string
  // ... green, yellow, purple, orange, pink, cyan
  // Rainbow colors (ultrathink keyword animation)
  rainbow_red: string
  rainbow_red_shimmer: string
  // ...
}
```

**Built-in themes (v2.1.118+):**
- `auto` — detects terminal background via `$COLORFGBG` or OSC 11 query; switches with OS appearance.
- `dark` / `light` — 24-bit RGB true color.
- `dark-daltonized` / `light-daltonized` — optimized for deuteranopia (red-green color blindness), replacing red/green contrasts with blue/yellow.
- `dark-ansi` / `light-ansi` — uses only the 16 standard ANSI colors for older terminals.

**Custom themes:** JSON files in `~/.claude/themes/` with `name`, `base`, and `overrides`. Live-reloading is supported. Example tokens: `claude`, `planMode`, `diffAdded`, `diffRemoved`, `userMessageBackground`.

**Apple Terminal fallback:** Automatically degrades to 256-color mode because Apple Terminal doesn’t handle 24-bit RGB escape sequences correctly.

### 2.2 Status Line and HUD

The status line is **fully customizable via hooks**. Anthropic provides a JSON payload after every turn containing:
- `model.display_name`
- `workspace.current_dir`
- `context_window.used_percentage`
- `cost.total_cost_usd`, `cost.total_duration_ms`
- `cost.total_lines_added`, `cost.total_lines_removed`
- Reasoning effort level (`low`, `medium`, `high`, `xhigh`, `max`) — available in Claude Code 2.1.119+

The default status line format includes:
- Model name + reasoning effort
- Git branch / worktree
- Context usage progress bar (block characters `█` / `░`)
- Session cost, daily cost, 5-hour block cost
- Rate limit countdown ("2h 45m left")

Community statusline tools (e.g., `claude-pulse`, `cc-monitor`) extend this with:
- Color-coded context bars (green → yellow → red)
- Code velocity (+lines in green, −lines in red)
- Burn rate (output tokens/minute)
- ETA to quota exhaustion

### 2.3 Typography and Spacing

- **Monospaced font** is assumed throughout.
- **Indentation:** Status lines are indented with 2 spaces (`\033[2C`).
- **Separator padding:** Empty lines between content/spinner/input create vertical rhythm.
- **Prompt marker:** `❯` (U+276F) colored blue (`rgb 177,185,249`) in the default dark theme.
- **Mode line:** Uses `⏵⏵` / `⏸` dingbat glyphs for permission state.

### 2.4 Animations and Micro-Interactions

| Animation | Implementation |
|-----------|----------------|
| **Spinner** | Undulating glyphs: `✶` (U+2736), `✻` (U+273B), `✳` (U+2733), `✢` (U+2722), `·` (U+00B7). Detected by `is_chrome_row` (✻ check) and `parse_status_line` (dingbat range U+2720–U+273F). |
| **Shimmer** | `claudeShimmer` color token used for subtle animated highlights. |
| **Rainbow / Ultrathink** | 7 rainbow colors + 7 shimmer variants used for keyword animation during extended thinking. |
| **Progress bars** | Block characters (`█` / `░`) for context usage, with gradient coloring (green → yellow → red). |
| **Streaming** | Tokens stream frame-by-frame at 60fps via a custom diff engine (see §3). |

**Micro-interactions:**
- Permission prompts show `Tab to amend` hint.
- Mode cycling (`Shift+Tab`) updates the mode line in real time.
- Terminal bell rings on: Claude finishing a reply, permission prompt appearing, and tools running >5s completing.

---

## 3. Interaction Patterns

### 3.1 Keyboard Shortcuts

| Action | Default | Notes |
|--------|---------|-------|
| `chat:cancel` | `Escape` | Cancel current input |
| `chat:clearInput` | `Ctrl+L` | Force full screen redraw, preserving input and conversation |
| `chat:clearScreen` | `Cmd+K` | Force full screen redraw (iTerm2/Terminal.app aware) |
| `chat:killAgents` | `Ctrl+X Ctrl+K` | Stop all running background subagents + turn off artifact auto-replies |
| `chat:cycleMode` | `Shift+Tab` | Cycle permission modes |
| `chat:modelPicker` | `Meta+P` | Open model picker |
| `chat:fastMode` | `Meta+O` | Toggle fast mode |
| `chat:thinkingToggle` | `Meta+T` | Toggle extended thinking |
| `chat:submit` | `Enter` | Submit message |
| `chat:newline` | `Ctrl+J` | Insert newline without submitting |
| `chat:undo` | `Ctrl+\_`, `Ctrl+Shift+-` | Undo last action |
| `chat:externalEditor` | `Ctrl+G`, `Ctrl+X Ctrl+E` | Open in external editor |
| `chat:stash` | `Ctrl+S` | Stash current prompt |
| `chat:imagePaste` | `Ctrl+V` (Alt+V on Windows/WSL) | Paste image from clipboard |

**Confirmation prompts:**
| Action | Default |
|--------|---------|
| `confirm:yes` | `Y`, `Enter` |
| `confirm:no` | `N`, `Escape` |
| `confirm:previous` | `Up` |
| `confirm:next` | `Down` |
| `confirm:nextField` | `Tab` |
| `confirm:toggle` | `Space` |
| `confirm:cycleMode` | `Shift+Tab` |
| `confirm:toggleExplanation` | `Ctrl+E` |

**Tabs:**
| Action | Default |
|--------|---------|
| `tabs:next` | `Tab`, `Right` |
| `tabs:previous` | `Shift+Tab`, `Left` |

### 3.2 Mouse Support

- **Clickable permission prompts:** Fullscreen menus now accept mouse clicks, including multi-select menus and "Other" input rows.
- **Mouse drag selection:** Supported in fullscreen mode for text selection.
- **Scroll wheel:** Supported with configurable acceleration.
- **Steadier scrolling / no accidental clicks:**
  ```json
  { "wheelScrollAccelerationEnabled": false }
  ```
  ```bash
  CLAUDE_CODE_DISABLE_MOUSE_CLICKS=1
  ```
- **Focus tracking:** Terminal focus/blur sequences are parsed and routed to UI events.
- **Known issues:** Mouse text selection can become erratic in split-pane terminals (e.g., Ghostty) when Claude Code is running in one pane, due to global mouse state leakage.

### 3.3 Vim Mode

Claude Code supports **full vim mode** for the input area:
- **Normal mode:** `Esc` to enter
- **Insert mode:** `i`, `I`, `a`, `A`, `o`, `O`
- **Visual selection:** `v` (character-wise), `V` (line-wise)
- **Operators:** `d` (delete), `c` (change), `y` (yank), `>` (indent), `<` (dedent)
- **Navigation:** `hjkl`, `gg`, `G`, `Ctrl+d`, `Ctrl+u`
- **Paste:** `p`, `Ctrl+Y` (paste deleted text), `Alt+Y` (cycle paste history)

Vim mode applies **only to the input area**; the rest of the TUI keeps its own navigation keys.

### 3.4 Streaming

Claude Code renders streaming responses at **60fps** using a custom rendering pipeline:
1. **React commit + Yoga layout** — reconciler processes state updates, Yoga computes flexbox tree in one pass.
2. **DOM-to-screen** — depth-first walk writes characters and styles into a `Screen` buffer.
3. **Overlay** — text selection and search highlighting modify the screen buffer in-place (inverse video, inverse+yellow+bold+underline).
4. **Diff** — cell-by-cell comparison against the front frame. On steady-state frames (spinner ticking), only 3 cells out of 24,000 produce patches.
5. **Optimize** — adjacent patches merged, redundant cursor moves eliminated, style transitions pre-serialized via `StylePool.transition()` cache (30–50% byte reduction).
6. **Write** — serialized to ANSI escape sequences in a single `write()` call, wrapped in **BSU/ESU** (Begin/End Synchronized Update, `ESC [ ? 2026 h/l`) for atomic frame presentation.

**Blit optimization:** When a node is not dirty and position unchanged, cells copy directly from `prevScreen` to current screen. Steady-state frames are extremely cheap — blit covers 99% of the screen; only the spinner’s 3–4 cells re-render.

### 3.5 Input Path

- **Raw mode** is **reference-counted** rather than a one-way global toggle.
- **`Ctrl+Z`** triggers suspend: releases raw/terminal modes, sends `SIGTSTP`, restores on `SIGCONT`.
- **Bracketed paste** is supported with a 2-second completion window for split input chunks.
- **Mouse packets** are parsed for selection, hover, click, wheel, hyperlink, and middle-click paste.
- **Incomplete escape sequences** wait ~50ms; paste sequences wait ~2s.

---

## 4. Panel Animations and Micro-Interactions

### 4.1 Spinner and Status Indicators

- **Working spinner:** `✶ Undulating…` or `✻ Sautéed for 1m 19s` with elapsed time and token rate (`↓ 2.2k tokens`).
- **Background subagent indicator:** `● low · /ef…` or `1 local agent still running`.
- **Context bar:** `█░░░░░░░░░ 5%` — block-based progress bar in status lines.
- **Mode indicator:** `⏵⏵ accept edits on`, `⏸ manual mode on`, `⏸ plan mode on`.

### 4.2 Permission Prompt Animations

- **Normal prompt:** Gray separator (`─`, `rgb 136,136,136`).
- **Plan mode / special prompt:** Blue separator (`─`, `rgb 177,185,249`) with dotted content separator (`╌`, U+254C).
- **Selection highlight:** Blue prompt marker (`❯`, `rgb 177,185,249`).
- **Mode line update:** Animated transition when cycling modes.

### 4.3 Diff and Code Rendering

- **Syntax highlighting:** Rendered via `ink-raw-ansi` with known dimensions.
- **Word-level diff:** `diffAddedWord` / `diffRemovedWord` tokens highlight specific changed words.
- **Search highlighting:** Inverse + yellow foreground + bold + underline for current match; inverse only for other matches.
- **Selection:** Inverse video for familiar "highlighted text" appearance.

### 4.4 Frame Scheduling

- Render scheduling uses **lodash `throttle` at 16ms** (~60fps), with leading and trailing edges enabled.
- **Double-buffer rendering:** Front/back `Screen` buffers swapped via pointer assignment (zero allocation).
- **BSU/ESU:** Entire frame appears atomically on terminals that support synchronized updates.

---

## 5. Accessibility

### 5.1 Screen Reader Mode (v2.1.181+)

Claude Code ships an **opt-in screen reader mode** that replaces visual terminal UI with flat, linear, labeled text.

**Activation methods (precedence order):**
1. `--ax-screen-reader` flag (single session)
2. `CLAUDE_AX_SCREEN_READER=1` environment variable (shell scope)
3. `axScreenReader: true` in user settings (all sessions)

**What it does:**
- Strips box-drawing characters, spinners, and in-place redraws.
- Writes flat, linear text with labeled prefixes: `you:`, `claude:`, `tool:`, `Permission Required:`.
- Tables reformat into `Header: value` sentences.
- Menus become **numbered lists** you answer by typing a number.
- Output accumulates in the scrollback buffer for easy review.
- Every transcript section receives a **searchable label**.
- Terminal bell rings when: Claude finishes a reply, permission prompt appears, or tools running >5s complete.

**Advanced accessibility (v2.1.198+):**
- **Table reformatting** for longer sessions.
- **OSC 133 shell-integration markers** at turn boundaries — enables jump-to-previous-prompt navigation in iTerm2, VS Code integrated terminal, and Windows Terminal.
- **Fullscreen menus** accept mouse clicks, including multi-select menus and "Other" input rows.
- **Permission mode announcements:** Cycling modes with `Shift+Tab` speaks the new mode aloud (`plan`, `accept-edits`, `auto`) so users always know the current state without reading the status line.

**Other accessibility settings:**
- `CLAUDE_CODE_DISABLE_MOUSE_CLICKS=1` — disables mouse clicks for users with motion sensitivity.
- `wheelScrollAccelerationEnabled: false` — steadies scrolling.
- Theme choices include **colorblind-friendly Daltonized** variants.
- Reduced motion settings can stop animations.
- Output Styles: `Explanatory` and `Learning` styles are more verbose; `Concise` (v2.1.237+) keeps responses short by default.

### 5.2 Vim Ergonomics

For users who prefer keyboard-only navigation, vim mode provides:
- Full visual selection (`v`, `V`)
- Yank/paste (`y`, `p`, `Ctrl+Y`)
- Indent/dedent (`>`, `<`)
- Word navigation (`Alt+B`, `Alt+F`)

### 5.3 External Editor

- `Ctrl+G` / `Ctrl+X Ctrl+E` opens the current prompt in the user’s `$EDITOR`, enabling full-screen editing with familiar tools.

---

## 6. Unique Killer Features

### 6.1 Permission Mode Cycling (`Shift+Tab`)

One of the most distinctive UX patterns. Claude Code has **six permission modes**:

| Mode | Behavior | Best for |
|------|----------|----------|
| `default` (Manual) | Prompts on first use of each tool | Everyday work |
| `acceptEdits` | Auto-accepts file edits + common filesystem commands | Well-scoped tasks in a clean tree |
| `plan` | Reads and runs read-only shell commands; edits nothing | Unfamiliar or large tasks |
| `auto` | Background classifier (Sonnet 4.6) approves/denies each action | Long tasks where prompting has stopped being read (Team/Enterprise) |
| `dontAsk` | Auto-denies anything not pre-approved | Locked-down CI |
| `bypassPermissions` | Skips prompts entirely | Containers and worktrees only |

**Cycle:** `default → acceptEdits → plan → bypassPermissions → auto → default`

This is more useful than it sounds: dropping into plan mode the moment a task turns out to be bigger than expected is the cheapest correction available.

### 6.2 Subagents and Background Agents

- **Subagents:** Delegated workers inside one session that do side tasks in their own context and return a summary. Runs in the background by default (v2.1.198+). Up to 200 subagents per session, 3 layers deep by default.
- **Background sessions (`claude agents`):** One screen to dispatch and monitor sessions running in the background. Each session is a full Claude Code conversation that survives terminal closure.
- **Fork (`/subtask`):** Inherits the full conversation context instead of starting fresh. Reuses parent’s prompt cache, making it cheaper for context-heavy tasks.
- **Cross-session messaging:** Sessions can message each other over local sockets — a session can hand off a summary, ask another session a question, or proactively flag that a change affects work happening somewhere else.
- **Agent teams:** Coordinated team of sessions Claude spawns and supervises (experimental).

### 6.3 Slash Commands

| Command | Purpose |
|---------|---------|
| `/init` | Scaffold project memory (CLAUDE.md) |
| `/clear` | Clear the conversation |
| `/compact` | Compress context automatically (160k–170k tokens) |
| `/config` | Interactive settings menu (theme, output style, permissions) |
| `/theme` | Switch themes (dark, light, ANSI, Daltonized) |
| `/statusline` | Configure custom status bar |
| `/diff` | Interactive change viewer — per-turn diffs reconstructed from conversation, not git |
| `/subtask` | Fork into a new background subagent inheriting full conversation |
| `/fork` | Copy session into a new background session (v2.1.212+) |
| `/background` / `/bg` | Move current session into background |
| `/tasks` | List running/background tasks |
| `/agents` | List named subagents (v2.1.198+; no longer opens a panel) |
| `/stats` | Detailed token usage and cost breakdown |
| `/usage` | Session block with API token usage |
| `/context` | Inspect and manage context window |

### 6.4 Output Styles

Output styles modify the **system prompt** to set role, tone, and output format:

- **Default:** Standard software-engineering system prompt.
- **Proactive:** Executes immediately, makes reasonable assumptions, prefers action over planning.
- **Concise (v2.1.237+):** Leads with the result, skips preamble and narration, keeps responses short by default while still doing thorough engineering work.
- **Explanatory:** Adds educational "Insights" between tasks.
- **Learning:** Collaborative mode; inserts `TODO(human)` markers for the user to implement.
- **Custom:** Markdown files in `~/.claude/output-styles/` or `.claude/output-styles/`.

### 6.5 Context Engineering Features

- **CLAUDE.md:** Project-level memory file loaded at session start.
- **Prompt caching:** Reuses parent’s cache across subagents and forks.
- **Auto-compact:** Triggered at 85–90% context usage; compresses conversation automatically.
- **Subagent context isolation:** Intermediate tool calls stay inside subagent context; only the final summary returns to the main conversation.
- **Memory field:** Subagents can have persistent directories (`user`, `project`, `local` scope) that survive across conversations.

### 6.6 Plan Mode

- Enters plan mode via `Shift+Tab` or `/plan` prefix.
- Claude explores the codebase and produces a plan **without editing source files**.
- Read-only tools run normally; file edits never auto-approve.
- On completion, Claude calls `ExitPlanMode` and optionally waits for user approval before switching to execution.

### 6.7 Extended Thinking Toggle (`Meta+T`)

- Toggles extended thinking (deep reasoning) on/off.
- Subagents inherit the main session’s extended thinking configuration.
- Supported on Sonnet 4.6 and Opus 4.6 models.

### 6.8 Custom Status Line Hooks

After every turn, Claude Code runs a user-defined script and hands it a JSON payload. Whatever the script prints appears at the bottom of the terminal. This enables:
- Real-time context bars with RGB gradients
- Cost tracking (session, daily, weekly, monthly)
- Code velocity metrics (+lines/−lines)
- Git branch / worktree info
- Rate limit countdowns
- Custom emojis and icons

### 6.9 Artifacts

- Publishes HTML or Markdown files as private, interactive pages on `claude.ai`.
- Shareable via public link or organization-internal link.
- Auto-replies to comments on artifacts can be toggled.

### 6.10 Inline Diff Review (`/diff`)

- Interactive diff viewer in the terminal.
- Switch between **Current** (git diff HEAD) and **per-turn diffs** (T1, T2, …) reconstructed from the conversation — survives manual edits and `git add`.
- Browse files with `↑/↓`, switch source with `←/→`.

### 6.11 Skills and Plugins

- **Skills:** Task-specific instructions loaded when invoked or relevant.
- **Plugins:** Extend Claude Code with hooks, custom commands, and output styles. The `explanatory-output-style` and `learning-output-style` plugins recreate deprecated output styles as `SessionStart` hooks.
- **Hooks:** `SessionStart`, `SessionEnd`, `PreToolUse`, `PostToolUse`, `UserPromptSubmit`, etc.

### 6.12 Worktrees

- Each background session moves into an isolated git worktree under `.claude/worktrees/`.
- Parallel sessions read the same checkout but each writes to its own.
- Prevents file conflicts across concurrent agents.

---

## 7. Architectural Insights (Why the UI Feels So Good)

### 7.1 Custom Ink Fork

Claude Code started with Ink and **forked it beyond recognition**:

| Aspect | Stock Ink | Claude Code Fork |
|--------|-----------|------------------|
| React version | LegacyRoot | **ConcurrentRoot** (React 19) |
| Event system | Basic `useInput` | **W3D capture/bubble dispatcher** |
| Screen mode | Normal scrollback | **Alt-screen + mouse tracking** |
| Rendering | Single buffer | **Double-buffered + packed Int32 screens** |
| Text selection | None | **Mouse drag selection +** |
| Memory | Object-per-cell (24k objects/frame) | **Packed typed arrays + interning pools** |

### 7.2 Rendering Pipeline (6 stages)

1. **React commit + Yoga layout** — flexbox tree computed in one pass.
2. **DOM-to-screen** — depth-first walk writes into `Screen` buffer.
3. **Overlay** — selection/search highlighting in-place.
4. **Diff** — cell-by-cell comparison; walks damage rectangle, not full screen.
5. **Optimize** — merge adjacent patches, eliminate redundant cursor moves, pre-serialize style transitions.
6. **Write** — single `write()` call with BSU/ESU for atomic updates.

### 7.3 Pool-Based Memory

Three interning pools eliminate per-frame allocations:
- **Char pool** — interns characters and strings.
- **Style pool** — interns ANSI style sequences.
- **Hyperlink pool** — interns OSC 8 hyperlink sequences.

Result: **zero per-frame object allocations** for the cell buffer. The only allocations are amortized pool entries and unavoidable patch strings.

### 7.4 Resize and Suspend Handling

- **Resize:** Updates dimensions, resets affected frames, schedules another render. No flicker due to double buffering + BSU/ESU.
- **`SIGCONT`:** Resets or re-enters the active screen after process resume.
- **Emergency restore:** Synchronous reset sequences for process-exit paths that cannot await React/Ink cleanup.

---

## 8. Summary of Killer UX Patterns

1. **Shift+Tab permission cycling** — instant, context-aware safety control.
2. **Background subagents by default** — true parallel work without context flooding.
3. **60fps streaming with zero flicker** — custom double-buffered renderer makes long outputs feel instant.
4. **Screen reader mode as a first-class citizen** — not an afterthought, but a parallel rendering path with labeled linear output.
5. **Custom status line hooks** — users control their own HUD with any script.
6. **Plan mode** — separates thinking from doing without leaving the terminal.
7. **Worktree isolation for parallel sessions** — prevents file conflicts automatically.
8. **Vim mode + external editor** — meets power users where they are.
9. **Theme system with Daltonized variants** — accessibility baked into visual design.
10. **OSC 11 theme detection** — Claude Code follows system light/dark mode automatically.

---

## 9. Files Referenced / Created

- **No files modified** in the Harbor-Harness repo for this analysis.
- This document is the primary deliverable: `docs/CLAUDE_CODE_UI_UX_ANALYSIS.md`.

---

*End of analysis.*
