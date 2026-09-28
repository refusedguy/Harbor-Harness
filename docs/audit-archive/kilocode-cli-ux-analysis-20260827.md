# Kilo Code CLI — UI/UX Competitive Analysis
> Source: `Kilo-Org/kilocode` (main branch, shallow clone at `/tmp/kilocode-analysis/repo`)  
> Focus packages: `packages/tui`, `packages/opencode`, `packages/kilo-ui`, `packages/ui`, `packages/session-ui`  
> Date: 2026-08-27

---

## 1. Terminal UI Layout & Components

### 1.1 Overall Architecture
The TUI is built on **OpenTUI** (`@opentui/core` + `@opentui/solid`) with **SolidJS** for reactivity. The main entry point is `packages/tui/src/app.tsx`, which wraps the app in providers and renders either `Home` or `Session` routes inside a flex column:

```
box (flex column, flexGrow=1)
├── Switch
│   ├── Home
│   └── Session
├── box (app_bottom plugin slot)
└── pluginRuntime.Slot (name="app")
```

### 1.2 Session Layout (`packages/tui/src/routes/session/index.tsx`)
The session view is a **flex row** with main content + conditional sidebar:

```
box (flex row, flexGrow=1, minHeight=0)
├── box (flexGrow=1, paddingBottom=1, paddingLeft=2, paddingRight=2, gap=1)
│   ├── scrollbox (sticky bottom, scroll acceleration)
│   │   └── For each message
│   │       ├── Revert banner (if reverted)
│   │       ├── UserMessage
│   │       └── AssistantMessage
│   │           └── For each part (Dynamic)
│   │               ├── TextPart
│   │               ├── ReasoningPart
│   │               ├── StepFinishPart
│   │               └── ToolPart → Shell/Edit/Read/.../GenericTool
│   ├── PermissionPrompt (if permissions pending)
│   ├── TerminalPrompt (if interactive terminal)
│   ├── QuestionPrompt (if blocking question)
│   ├── SuggestPrompt (if blocking suggestion)
│   ├── NetworkPrompt (if network wait)
│   ├── SubagentFooter (if parentID exists)
│   └── Prompt (composer, when visible)
└── Sidebar (when sidebarVisible)
```

**Key detail:** There is **no monolithic status bar**. Status is distributed:
- **Home footer plugin slot** (`home_footer`): directory + MCP count + version
- **Sidebar footer plugin slot** (`sidebar_footer`): directory path + Kilo version
- **Prompt status area**: spinner, retry countdown, interrupt hint, workspace notice, editor context
- **SubagentFooter**: subagent navigation + token usage + cost

### 1.3 Sidebar (`packages/tui/src/routes/session/sidebar.tsx`)
- Fixed width **42px** (collapsed) or full-width overlay on narrow terminals
- Shows session title, workspace status, share URL
- Scrollable with custom scrollbar (`theme.borderActive`)
- Plugin slots: `sidebar_title`, `sidebar_content`, `sidebar_footer`

### 1.4 Status Bar / Chrome
Kilo replaces the traditional VS Code-like status bar with **contextual chrome**:
- **Bottom-left of prompt area**: live spinner, retry timer, `esc to interrupt`
- **Bottom-right of prompt area**: agent shortcut, model shortcut, palette shortcut, variant shortcut
- **Home footer**: `directory · MCP count /status · version`
- **Sidebar footer**: `path · • Kilo version`

### 1.5 Chat Timeline
- Rendered in a `scrollbox` with `stickyScroll={true}` and `stickyStart="bottom"`
- Messages separated by `marginTop=1` (except first)
- User messages: left border colored by agent color, hover highlights `theme.backgroundElement`
- Assistant messages: no border, parts rendered inline via `Dynamic` component mapping
- Revert banner: clickable with redo shortcut hint
- Compaction: horizontal border line with "Compaction" title

### 1.6 Input Composer (`packages/tui/src/component/prompt/index.tsx`)
The `Prompt` component is a rich multi-line composer:

```
box (width=100%)
├── box (border=left, borderColor=borderHighlight)
│   └── box (paddingLeft=2, paddingRight=2, paddingTop=1, backgroundColor=theme.backgroundElement)
│       ├── textarea (width=100%, maxHeight=6, placeholder, syntax highlighting)
│       └── box (paddingTop=1, flex row, space-between)
│           ├── box (flex row, gap=1)
│           │   ├── agent name + vim mode indicator + auto/shell mode
│           │   ├── model + provider + variant
│           │   └── editor context file label
│           └── right slot (plugin content)
├── box (border=left, height=1, custom bottom char ▀)
└── box (width=100%, flex row, space-between)
    ├── status area (spinner / retry / workspace / move / interrupt)
    └── hints area (variant shortcut, agent shortcut, palette shortcut)
```

### 1.7 Tool Cards
Two tiers of tool rendering:

**InlineTool** (`InlineToolRow`):
- Used for quick operations (Read, Grep, Glob, WebSearch, etc.)
- Icon (2 chars wide) + status text + approval badge
- States: pending (`~ pending...`), complete (icon + text), failed (icon + text + expandable error)
- Hover: color change, click to expand error or navigate

**BlockTool**:
- Used for complex operations (Edit, ApplyPatch, Shell, Write, Task)
- Left border, padding, hover highlight (`theme.backgroundMenu`)
- Title line with spinner + routed model meta + approval badge
- Children: diff viewer, diagnostics, code blocks, etc.
- Clickable to open diff viewer or external editor

### 1.8 Approval Gates (`packages/tui/src/routes/session/permission.tsx`)
`PermissionPrompt` is a full-screen blocking UI with three stages:
1. **permission**: Shows tool-specific body
   - Edit: split diff with hunk collapse (`...` between hunks)
   - Bash: command list + description
   - Read/Glob/Grep/List: path/pattern metadata
2. **always**: "Always allow" confirmation with pattern list
3. **reject**: Reject prompt with optional message

**Key pattern:** The approval UI shows the exact diff or command before asking, with keyboard shortcuts bound (`y` to confirm, `n` to reject, `a` for always).

### 1.9 Command Palette (`packages/tui/src/component/command-palette.tsx`)
- Triggered by `ctrl+p`
- Implemented as `DialogSelect` with `title="Commands"`
- Shows **Suggested** category first, then all reachable commands
- Each entry shows: title, description, category, footer keybindings
- Fuzzy search via `fuzzysort`
- Supports command aliases and slash commands

### 1.10 Tab Strip / Session Switching
Kilo does **not** use a traditional tab strip. Instead:
- **Quick switch**: `<leader>1` through `<leader>9` for pinned sessions
- **Session list**: `<leader>l` opens `DialogSessionList` with search, pinning, global/project scope
- **Sidebar**: always shows current session title
- **Subagent navigation**: `<leader>down/left/right/up` for child/parent sessions
- **Timeline**: `<leader>g` jumps to message in timeline

---

## 2. Visual Design

### 2.1 Color System
Kilo uses a **semantic theme token** system defined in JSON (`packages/tui/src/theme/assets/kilo.json`):

**Dark palette (Kilo theme):**
- `darkBase`: `#0c0a09` (warm black)
- `darkPanel`: `#1c1917` (stone-900)
- `darkElement`: `#292524` (stone-800)
- `darkBorder`: `#44403c` (stone-700)
- `darkBorderActive`: `#f9f76f` (yellow-300, high contrast)
- `darkText`: `#fafaf9` (stone-50)
- `darkTextMuted`: `#a6a09b` (stone-400)
- `darkPrimary`: `#f9f76f` (yellow-300)
- `darkSuccess`: `#89d185` (green-300)
- `darkError`: `#ff6467` (red-400)
- `darkWarning`: `#cca700` (yellow-600)
- `darkInfo`: `#3794ff` (blue-500)

**Diff colors:**
- Added: `#89d185` / bg `#122318`
- Removed: `#ff6467` / bg `#2a1214`
- Highlight added: `#b8db87`
- Highlight removed: `#ff8587`

### 2.2 Typography
- **Font family**: `JetBrains Mono`, `Fira Code`, `Cascadia Code`, `ui-monospace`, `SFMono-Regular`, `Menlo`, `Consolas`
- **Font size**: 13px body, 12px terminal mockup, 11px hints, 10px badges
- **Line height**: 1.5 body, 1.4 terminal
- **Weight**: Bold for titles, selected items, agent names; normal for body

### 2.3 Spacing & Borders
- **Padding**: consistent `paddingLeft=2, paddingRight=2`, `paddingTop=1, paddingBottom=1`
- **Gap**: `gap=1` for tight stacks, `gap=2` for sections
- **Margin**: `marginTop=1` between messages/tools
- **Borders**: left-border on messages (colored by agent), `SplitBorder` custom chars (`╹`, `▀`)
- **Scrollbar**: 1px wide, `theme.border` track, `theme.borderActive` thumb

### 2.4 Markdown & Syntax
- **Renderer**: Shiki-based syntax highlighting with custom theme mappings
- **Markdown tokens**: heading, link, code, blockquote, emphasis, strong, hr, list, image
- **Tables**: custom `formatMarkdownTables` with fixed-width columns and alignment
- **Code blocks**: `markdownCodeBlock` token, syntax style per language
- **Thinking blocks**: rendered at `thinkingOpacity: 0.72` (slightly dimmed)

### 2.5 Agent Colors
Each agent gets a distinct left-border color on user messages:
- `local.agent.color(props.message.agent)` returns a theme-aware color
- Queued messages get a bold badge with contrasting foreground (`selectedForeground`)

---

## 3. Interaction Patterns

### 3.1 Keyboard Shortcuts (`packages/tui/src/config/keybind.ts`)
Kilo uses a **leader-key modal system** (`ctrl+x` default):

| Category | Shortcut | Action |
|----------|----------|--------|
| App | `ctrl+x q` | Exit |
| App | `ctrl+p` | Command palette |
| Session | `ctrl+x l` | Session list |
| Session | `ctrl+x n` | New session |
| Session | `ctrl+x g` | Timeline |
| Session | `ctrl+x c` | Compact |
| Session | `ctrl+r` | Rename |
| Session | `ctrl+x 1-9` | Quick switch |
| Model | `ctrl+x m` | Model list |
| Model | `f2` | Next recent model |
| Agent | `ctrl+x a` | Agent list |
| Agent | `tab` | Next agent |
| Diff | `[`/`]` | Prev/next hunk |
| Diff | `n`/`p` | Prev/next file |
| Messages | `ctrl+x =/-` | Thumbs up/down feedback |
| Prompt | `return` | Submit |
| Prompt | `shift+return` | Newline |

**Input editing:** Full Emacs-style bindings (`ctrl+a/e/k/u`, `ctrl+b/f`, etc.) plus vim mode option.

### 3.2 Mouse Support
- `useMouse: !Flag.KILO_DISABLE_MOUSE && input.config.mouse`
- **Hover states**: `onMouseOver`/`onMouseOut` on messages, tools, revert banner
- **Click actions**:
  - User message: open `DialogMessage` (copy/edit)
  - Failed tool: expand error text inline
  - Retry banner: click to expand long error
  - Diff viewer: click file tree items
- **Selection**: `renderer.getSelection()` for copy-on-select, `ctrl+c` copies, `escape` clears

### 3.3 Streaming Effects
- **Spinner**: 10-frame braille cycle (`⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏`) at 80ms interval
- **Progressive rendering**: text appears as it streams via `onContentChange`
- **Retry countdown**: live `setInterval` updating seconds until retry
- **Status indicator**: `[⋯]` fallback when animations disabled

### 3.4 Markdown Rendering
- Parsed via `@opentui/core` parsers + custom `markdown.ts` utilities
- Tables: fixed-width columns with CJK-aware `getDisplayWidth` (2 cols for wide chars)
- Code: Shiki syntax with language-specific styles
- Links: underlined or colored via theme tokens

### 3.5 Image Preview
- **Pasting images**: `pasteAttachment` inserts virtual text `[Image N]` or `[PDF N]` with extmark
- **SVG**: inlined as text `[SVG: filename]`
- **Binary images**: base64 data URL stored as `FilePart.url`
- **Web UI**: `ImagePreview` component (`packages/ui/src/components/image-preview.tsx`) with `<img>` in a Kobalte dialog, `aria-label` on close button
- **Terminal TUI**: no inline image protocol rendering — images are placeholders in chat, preview happens in desktop/web UI

---

## 4. Panel Animations & Micro-interactions

### 4.1 Fade-in Animation (`packages/tui/src/util/signal.ts`)
```ts
export function createFadeIn(show, enabled) {
  // 160ms ease-in-out (quadratic in-out)
  const start = performance.now()
  setAlpha(0)
  const timer = setInterval(() => {
    const progress = Math.min((performance.now() - start) / 160, 1)
    setAlpha(progress * progress * (3 - 2 * progress)) // smoothstep
    if (progress >= 1) clearInterval(timer)
  }, 16)
}
```
Used for UI element reveals. Disabled when `animations_enabled` KV flag is false.

### 4.2 Spinner Animation
- Braille frames at 80ms interval
- Fallback: static `⋯` text when animations disabled
- Configurable color via theme

### 4.3 Background Pulse / Logo Animation (`packages/tui/src/component/bg-pulse-render.ts`)
- 4600ms period, 3 concentric rings
- Emitted from logo center with phase offset
- Breathing amplitude + tail trail
- RGB color mixing for theme-aware rendering

### 4.4 Scroll Acceleration (`packages/tui/src/util/scroll.ts`)
- `getScrollAcceleration(config)` returns acceleration factor
- Applied to scrollbox for smooth momentum scrolling

### 4.5 Hover Micro-interactions
- Messages: `backgroundColor` transitions between `theme.backgroundPanel` and `theme.backgroundElement`
- Tools: same hover highlight, cursor change on clickable items
- Dialog backdrop: `RGBA.fromInts(0,0,0,150)` with click-to-dismiss

### 4.6 Toast Notifications (`packages/tui/src/ui/toast.tsx`)
- Variant-based border colors (success/error/warning/info)
- Auto-dismiss with configurable duration

---

## 5. Accessibility Features

### 5.1 Keyboard-First Design
- Every action has a keybinding
- `which-key` plugin (`packages/tui/src/feature-plugins/system/which-key.tsx`) shows pending keybindings in an overlay panel
- Leader-key sequences with configurable timeout

### 5.2 Focus Management
- Dialog system pushes `modal` mode onto mode stack
- `refocus()` after dialog close restores focus to previous element
- `renderer.getSelection()` + `renderer.currentFocusedRenderable` for focus tracking

### 5.3 Clipboard & Selection
- `ctrl+c` copies selection to clipboard with toast confirmation
- `escape` clears selection
- Selection-aware dialog dismiss (clicking backdrop with active selection does not close)

### 5.4 Screen Reader Support (Web/Desktop)
- `ImagePreview` has `aria-label` on close button
- `alt` text on preview images
- Semantic HTML in web components (`data-slot` attributes)

### 5.5 TUI Limitations
- Terminal UIs inherently lack ARIA roles
- No explicit `role=` attributes in TUI renderables
- Color contrast relies on theme tokens (high-contrast `borderActive` yellow on stone)

---

## 6. Unique Killer Features

### 6.1 Vim Modal Editing in Prompt
- `useVim` hook routes keys through vim layer when enabled
- `VimModeIndicator` shows current mode (normal/insert/visual) with color-coded alpha
- Toggle via config or command

### 6.2 Model Routing & Provenance
- `RoutedModelMeta` shows which sub-model/provider actually responded
- Displayed in assistant footer and tool titles
- Badge pattern: `▣ mode · model · routed-model · duration`

### 6.3 Approval Provenance Tracking
- Every tool call shows inline approval note: `· approved by you` / `· auto-approved by global config (matched bash `*`)` / `· denied`
- Source labels: agent, global, project, yolo, session, default

### 6.4 Background Subagents
- Toggle via `ctrl+b`
- Subagent footer shows sibling navigation (`parent`, `prev`, `next`)
- Token usage + cost per subagent
- Foreground vs background task distinction

### 6.5 Network Resilience
- `NetworkPrompt` shows countdown timer for reconnect
- Enter to resume, Esc to stop
- Restored state detection

### 6.6 Paste Intelligence
- Large pastes (>5 lines or >800 chars) collapse to `[Pasted ~N lines]`
- Second identical paste expands collapsed placeholder
- File path pastes trigger `readLocalAttachment` (SVG inlined, images/PDFs attached)

### 6.7 Editor Context Integration
- Shows selected file + line range in prompt
- Dismissable with click
- Auto-cleared when selection changes

### 6.8 Cost Alert System
- `cost_alert` command sets spending threshold
- Live token/cost display in prompt hints

### 6.9 Diff Viewer with File Tree
- Full-screen diff viewer plugin (`diff-viewer.tsx`)
- File tree with expand/collapse, single-patch view
- Split/unified toggle, hunk navigation, keyboard shortcuts
- Persists preferences in KV store

### 6.10 Session Mentions & Slash Commands
- `@` triggers session mention autocomplete
- `/` triggers command palette with aliases
- `slashMatches` for command display formatting

### 6.11 Plugin Architecture
- `pluginRuntime.Slot` system for extensibility
- Builtin plugins: HomeFooter, SidebarContext, SidebarMcp, SidebarLsp, SidebarTodo, SidebarFiles, Notifications, PluginManager, WhichKey, DiffViewer
- `TuiPluginApi` exposes state, theme, client, slots, kv

---

## 7. Concrete Patterns Harbor Should Adopt

| Pattern | Source File | Why It Matters |
|---------|-------------|----------------|
| **Distributed status chrome** | `packages/tui/src/feature-plugins/home/footer.tsx`, `session/subagent-footer.tsx` | Avoids crowded bottom bar; context-sensitive info near the action |
| **Approval provenance badges** | `packages/tui/src/kilocode/tool-approval.tsx`, `InlineToolRow` | Users understand *why* a tool ran without opening settings |
| **Inline diff hunk collapse** | `packages/tui/src/routes/session/index.tsx` Edit/ApplyPatch | Keeps long diffs scannable; `...` between hunks |
| **Fade-in micro-animation** | `packages/tui/src/util/signal.ts` `createFadeIn` | 160ms smoothstep makes UI feel responsive without being distracting |
| **Keyboard-first command palette** | `packages/tui/src/component/command-palette.tsx` | Suggested commands + fuzzy search + footer keybindings = discoverable power |
| **Vim mode as opt-in layer** | `packages/tui/src/component/prompt/index.tsx` `useVim` | Caters to advanced users without cluttering default UX |
| **Extmark-based prompt parts** | `packages/tui/src/component/prompt/index.tsx` | Virtual text for images/attachments that stays editable and searchable |
| **Plugin slot architecture** | `packages/tui/src/plugin/runtime.ts` | Harbor can add custom sidebars, prompts, and footers without forking core |
| **Scroll acceleration** | `packages/tui/src/util/scroll.ts` | Makes long transcripts feel native, not sluggish |
| **Retry UX with countdown** | `packages/tui/src/component/prompt/index.tsx` | `retrying in 5s · attempt #3` is far better than silent retries |

---

## 8. Gaps & Opportunities vs. Harbor

| Harbor Strength | Kilo Equivalent | Gap / Opportunity |
|-----------------|-----------------|-------------------|
| Dark theme with JetBrains Mono | Kilo theme (stone palette) | Harbor's `#39bae6` accent is more distinctive; Kilo's yellow is aggressive |
| Approval gate with diff preview | Kilo `PermissionPrompt` | Harbor could add hunk-level collapse like Kilo |
| Tool cards with icons | Kilo `InlineTool`/`BlockTool` | Harbor's cards are richer; Kilo's are more compact |
| Status bar | Kilo distributed chrome | Harbor's unified bar is simpler; Kilo's is more contextual |
| Session timeline | Kilo `DialogTimeline` | Harbor lacks a visual timeline jumper |
| Image preview | Kilo TUI placeholders + web preview | Harbor could add terminal image protocol support (kitty/iTerm) |

---

## 9. Recommended Sprint Actions

1. **Adopt `createFadeIn`** for panel reveals in Harbor TUI (160ms smoothstep, toggleable)
2. **Add approval provenance badges** to Harbor tool cards ("auto-approved by global config")
3. **Implement hunk collapse** in edit diffs (`...` between non-contiguous hunks)
4. **Build a which-key overlay** for Harbor keyboard shortcuts
5. **Add a command palette** (`ctrl+p`) with suggested + fuzzy search
6. **Introduce quick-switch slots** for top 9 sessions
7. **Add vim-mode toggle** for the prompt composer
8. **Persist diff viewer prefs** (split/unified, file tree visibility) in KV
9. **Add cost/token display** in prompt footer (if not present)
10. **Explore terminal image protocol** (kitty graphics) for inline image preview

---

*Analysis based on source code inspection of `Kilo-Org/kilocode` main branch, commit `15c3db7`.*
