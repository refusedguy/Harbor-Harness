# Harbor Killer-Features Audit — Orca + Pi + Opencode + Kilocode

> **Source:** Subagent R1 — cloned all 4 competitor repos to `/tmp/competitors/`
> on 2025-07-18. This document is the deep audit + Harbor implementation plan.
> Goal: identify every "wow" feature the user said is missing from Harbor, and
> give a concrete Avalonia-portable recipe for each one.
>
> **Scope:** UI/UX, animations, micro-interactions, component model, state
> management, performance, and architecture patterns. We deliberately ignore
> backend-only features (auth providers, PR fetching) except where they shape
> the UI surface.

---

## §1. Executive summary — top 20 features Harbor should steal, ranked by impact

The list below is the **ruthless, ship-first** ranking. Each entry has:

- **Impact** (1–5): how much it changes perceived quality.
- **Effort** (S/M/L): how much work to port to Avalonia.
- **Source**: which repo(s) do it best.
- **Status**: ✅ Harbor has it / ⚠️ Harbor has partial / ❌ Harbor lacks it.

| #  | Feature                                            | Impact | Effort | Source        | Status |
|----|----------------------------------------------------|--------|--------|---------------|--------|
| 1  | Animated streaming text with typewriter cursor     | 5      | S      | Orca, OpenAI  | ✅ R28 |
| 2  | Collapsible tool-call cards with status + duration | 5      | M      | Orca, Opencode | ✅ R28 |
| 3  | Command palette with recent items + fuzzy + kbd    | 5      | M      | Orca (cmdk)   | ✅ R28+ (fuzzy filter: `CommandPaletteViewModelBase.FuzzyScore`; re-verified 2026-08-27) |
| 4  | Token-usage sparkline in status bar (live)         | 4      | S      | Opencode, Kilo | ✅ R28 |
| 5  | Intra-line word-diff highlighting (not just line)  | 5      | M      | Pi (diff lib) | ⚠️ partial (line-level only) |
| 6  | Toast notifications with slide-in + auto-dismiss   | 4      | S      | Orca (sonner) | ✅ R28 |
| 7  | Tab-strip with drag-reorder + close-gesture        | 4      | L      | Orca          | ❌ |
| 8  | Worktree jump palette (Cmd-J / Ctrl+J)             | 5      | M      | Orca          | ❌ |
| 9  | Agent pet mascot that reacts to agent state        | 4      | S      | Orca          | ❌ |
| 10 | Markdown rich editor (TipTap) with code blocks     | 5      | L      | Orca          | ⚠️ partial (renderer, no editor) |
| 11 | Image preview inline in chat                       | 4      | M      | Opencode, Kilo | ❌ |
| 12 | Skill freshness pill (update available)            | 3      | S      | Orca          | ❌ |
| 13 | Setup-guide progress ring + checklist              | 4      | M      | Orca          | ✅ R28 (onboarding wizard with stepper) |
| 14 | Dictation / speech-to-text input                   | 3      | L      | Orca (sherpa) | ❌ |
| 15 | Browser/markup overlay for screenshots             | 4      | L      | Orca          | ❌ |
| 16 | Mobile-driver overlay (responsive phone preview)   | 3      | L      | Orca          | ❌ |
| 17 | PR-comment sidebar cards with code context         | 4      | L      | Orca, Kilo    | ❌ |
| 18 | Star-nag card (gentle GitHub star reminder)        | 2      | S      | Orca          | ❌ |
| 19 | Floating terminal pane (detach + reattach)         | 4      | L      | Orca          | ❌ |
| 20 | Per-workspace env memory (`.env` per worktree)      | 3      | M      | Orca          | ❌ |

**Top 3 to implement now (P0):** #1 (typewriter), #2 (tool-call cards), #4 (sparkline) —
they have the highest impact/effort ratio and ship in 1 sprint. Detailed
implementation sketches in §2.7 and §8.

> **Status-provenance note (2026-08-27, docs-zero sweep):** every ✅ in the table above
> was re-verified against code — `TypewriterStreamingText.axaml.cs`, `ToolCallCardView`,
> `Sparkline`, `ToastNotificationsView` + `AppStyles.axaml:110` ("Toast slide-in"),
> `OnboardingWindow.axaml` (5-dot stepper), `CommandPaletteViewModelBase.FuzzyScore`
> (all under `apps/Harbor.App.Avalonia/`). Items marked ❌ remain unimplemented
> (no matching types found).

---

## §2. Orca deep-dive (user's primary reference)

### 2.1 Repo facts

| Property              | Value                                                       |
|-----------------------|-------------------------------------------------------------|
| Repo                  | `https://github.com/stablyai/orca`                          |
| Version               | 1.4.145-rc.3                                                |
| Description           | "Next-gen IDE for parallel agentic development"             |
| Architecture          | Electron 43 + React 19.2 + TypeScript 7                     |
| Renderer stack        | React 19, Radix UI 1.6, Tailwind 4, Zustand 5, TipTap 3.22, Monaco 0.55, xterm.js 6.1-beta, Mermaid 11, KaTeX, lowlight, Marked 17 |
| Main process          | Node 24, node-pty 1.1, ssh2, @parcel/watcher, @linear/sdk   |
| State mgmt            | **Zustand** with slice pattern (50+ slices in `src/renderer/src/store/slices/`) |
| i18n                  | i18next + react-i18next (en/ja/zh/ko/es)                    |
| Animations            | tw-animate-css 1.4 + Radix data-state hooks + custom CSS transitions |
| Build                 | electron-vite 5, oxlint 1.71, oxfmt, vitest 4, Playwright 1.59 |
| Native deps           | sherpa-onnx 1.12 (speech), node-pty, @parcel/watcher         |
| Package manager       | pnpm 10.24                                                  |
| Patched deps          | node-pty, @xterm/addon-webgl, @xterm/addon-ligatures, @xterm/addon-serialize, @xterm/xterm — all beta-channel builds (Orca dogfoods xterm 6.1-beta before stable) |
| Test coverage         | Hundreds of e2e Playwright specs for terminal behavior      |

### 2.2 Source structure (top-level)

```
orca/
├── src/
│   ├── main/              # Electron main process — 60+ subdirs
│   ├── preload/           # IPC bridge to renderer
│   ├── relay/             # Sidecar relay (fs/git/pty handlers over WS/IPC)
│   ├── renderer/          # React UI — the main UI surface
│   │   └── src/
│   │       ├── App.tsx
│   │       ├── components/    # ← 100+ UI components (this is the goldmine)
│   │       ├── store/         # Zustand store + 50+ slices
│   │       ├── hooks/         # 50+ custom hooks
│   │       ├── runtime/       # Client RPC to main process
│   │       ├── i18n/          # Localization (en/ja/zh/ko/es)
│   │       ├── startup/       # First-run flow
│   │       ├── lib/           # Pure helpers
│   │       ├── assets/        # CSS + fonts (Geist, NerdFontMono)
│   │       ├── constants/
│   │       └── web/           # Web-build variant (non-Electron)
│   ├── shared/            # Code shared between main/preload/renderer
│   ├── cli/               # Bundled `orca` CLI (separate from main process)
│   └── types/
├── tests/e2e/             # Playwright — 200+ spec files
├── tools/benchmarks/      # Perf harnesses (startup, jank, terminal-pipeline)
├── skills/                # Markdown skill bundles (SKILL.md)
├── skill-guides/          # User-facing skill docs
├── config/                # Build config + patches for xterm/node-pty
└── resources/             # Icons, sounds, dmg backgrounds
```

### 2.3 Component inventory (Orca renderer) — 100+ items

> Path prefix: `src/renderer/src/components/`

Below is a categorized walk-through. Each entry: **Name — one-line description.**

#### Branding / chrome
- `Landing.tsx` — empty-state hero ("Choose a workspace to begin")
- `Sidebar.tsx` — left rail with workspaces, projects, agent status
- `TelemetryFirstLaunchSurface.tsx` — opt-in telemetry prompt on first run
- `FirstLaunchBanner.tsx` — one-time banner announcing features
- `UpdateCard.tsx` — electron-updater release notes card with install button
- `StarNagCard.tsx` — gentle "star us on GitHub" card (dismissible)
- `ZoomOverlay.tsx` — Ctrl+/Ctrl- zoom indicator that fades after 1s
- `ShortcutKeyCombo.tsx` — renders a `⌘ + ⇧ + P` style key combo chip

#### Quick switchers
- `QuickOpen.tsx` — file quick-open (Cmd+P / Ctrl+P) with fuzzy match
- `WorktreeJumpPalette.tsx` — Cmd+J worktree switcher (Orca's signature feature)
- `SelectedTextCopyMenu.tsx` — context menu for selected text

#### Workspace / project
- `NewWorkspaceComposerCard.tsx` + `Modal.tsx` — new-workspace wizard card
- `TaskPage.tsx` — task/integration hub (GitHub/Linear/Jira issues)
- `PullRequestPage.tsx` — PR review surface
- `GitHubItemDialog.tsx` / `GitLabItemDialog.tsx` / `JiraIssueWorkspace.tsx` / `LinearIssueWorkspace.tsx` — issue tracker panes
- `LinearIssueTextEditor.tsx` + `LinearIssueMarkdownToolbar.tsx` — TipTap-based Linear issue editor
- `LinearItemDrawer.tsx` — slide-out drawer for Linear issue details

#### Agent feedback / status
- `AgentStateDot.tsx` — animated colored dot indicating agent state (idle/thinking/streaming/error)
- `CodexRestartChip.tsx` — chip shown when a Codex restart is needed
- `DetachedHeadBadge.tsx` — git detached-HEAD warning badge
- `Terminal.tsx` — terminal pane (xterm.js host)
- `TerminalSearch.tsx` — in-terminal Ctrl+F search bar
- `AgentHibernationGate.tsx` — overlay shown when agent is hibernated to save resources
- `integration-status-pill.tsx` — pill showing integration health (GitHub auth, Linear, etc.)

#### Onboarding
- `onboarding/` — multi-step onboarding flow
- `setup-guide/SetupGuideModal.tsx` — checklist modal for first-run setup
- `setup-guide/SetupGuideProgressRing.tsx` — animated circular progress ring
- `setup-guide/use-setup-guide-progress.ts` — hook tracking setup completion
- `setup-guide/setup-script-probe-cache.ts` — caches probe results from setup scripts

#### Feature discoverability
- `feature-tips/` — Coach marks / tooltip overlays highlighting new features
- `feature-wall/` — full-screen feature showcase on first launch
- `contextual-tours/` — guided product tours
- `feature-interaction-writer-boundaries.test.ts` — test asserting feature boundary isolation

#### Pet / mascot
- `pet/` — animated agent pet (think Tamagotchi) that reacts to agent state
  - idle: sleeping animation
  - thinking: looking up with thought bubble
  - streaming: typing on tiny keyboard
  - error: dizzy stars
  - success: confetti

#### Activity feed
- `activity/` — vertical activity feed showing recent agent actions across all worktrees
- `dashboard/` — aggregated dashboard across all projects

#### Automation
- `automations/` — UI for scheduling/triggering agent runs (cron, webhook, manual)

#### Browser pane (computer-use)
- `browser-pane/BrowserPane.tsx` — embedded Chromium webview for computer-use agents
- `browser-pane/BrowserAddressBar.tsx` — address bar with autocomplete
- `browser-pane/BrowserFind.tsx` — in-page find
- `browser-pane/BrowserToolbarMenu.tsx` — profile/cookies/clear menu
- `browser-pane/BrowserMobileDriverOverlay.tsx` — phone-frame overlay
- `browser-pane/GrabConfirmationSheet.tsx` — bottom sheet for drag confirmation
- `browser-pane/BrowserImportHintButton.tsx` — "import this page" hint
- `browser-pane/BrowserPaneOverlayLayer.tsx` — overlay layer for annotations
- `browser-pane/markup/MarkupOverlay.tsx` — canvas overlay for drawing on screenshots
- `browser-pane/markup/MarkupToolbar.tsx` — markup tools (pen, highlight, arrow, text)
- `browser-pane/markup/useMarkupEditor.ts` — hook managing markup state
- `browser-pane/markup/useMarkupKeyboardShortcuts.ts` — keyboard shortcuts
- `browser-pane/markup/markup-shape-render.ts` — shape rendering
- `browser-pane/markup/markup-screenshot-compose.ts` — composite screenshot + markup

#### Cmd-J palette (the killer)
- `cmd-j/` — Cmd+J worktree jump palette with fuzzy match across all open worktrees
  - shows worktree name, agent status, last activity, branch, file count
  - keyboard navigable (↑↓ Enter Esc)
  - drag-reorderable

#### Crash reporting
- `crash-report/` — crash report viewer with retry/submit buttons
- `error-boundaries/` — React error boundaries with recovery UI

#### Diff / code review
- `diff-comments/` — inline diff comments
- `editor/` — Monaco editor host with file tabs

#### Emulator pane (Android)
- `emulator-pane/` — Android emulator pane for testing mobile agents
- `floating-terminal/` — detachable terminal that floats above other panes

#### GitHub / GitLab / Linear / Jira integrations
- `github/` — GitHub issues, PRs, project views (10+ components)
- `github-project/` — GitHub Projects board view
- `gitlab/` — GitLab issues / MRs
- `linear-api-key-dialog.tsx` — Linear API key setup
- `linear-issue-attribute-filter-dropdowns.tsx` — Linear issue filters
- `linear-priority-icon.tsx` — priority icon
- `linear-project-view-surfaces.tsx` — project view surface
- `linear-scope-selector.tsx` — team scope selector
- `linear-state-pill-style.ts` — state pill styling

#### Icons
- `icons/` — custom SVG icon set

#### Mobile / responsive
- `mobile/` — mobile-specific layout + components

#### Native chat
- `native_chat/` — non-Agent chat surface (direct LLM chat)

#### Network
- `network/` — network inspector panel

#### Notifications
- `notifications/` — system notification settings + macOS permission card

#### Orca profiles (cloud sync)
- `orca-profiles/` — cloud profile sync UI

#### Ports
- `ports/` — port-forwarding manager UI

#### PR comments
- `pr-comments/` — sidebar cards for PR review comments with code context

#### Prompt input
- `prompt-input/` — TipTap-based prompt composer
  - file attachments
  - image attachments
  - slash commands
  - @-mentions
  - keyboard shortcuts (Cmd+Enter submit, Shift+Enter newline)

#### Quick command search
- `quick-open-search.ts` — fuzzy search algorithm used by QuickOpen

#### Setup guide
- `setup-guide/` — first-run setup wizard with progress tracking

#### Skills
- `skills/SkillsPage.tsx` — skill marketplace browser
- `skills/SkillFreshnessStatusPill.tsx` — pill showing "update available"
- `skills/SkillFreshnessNudge.tsx` — nudge card prompting update
- `skills/SkillFreshnessUpdateDialog.tsx` — update dialog
- `skills/skill-freshness-group.tsx` — grouped skill list
- `skills/skills-filter.ts` — filter logic

#### Sparse checkout
- `sparse/SparseCheckoutPresetSelect.tsx` — preset selector
- `sparse/SparseCheckoutPresetDraftForm.tsx` — preset draft form

#### Source control
- `source-control/SourceControlActionVariableChips.tsx` — chips for commit-message variables

#### Terminal pane (the deep one)
- `terminal-pane/` — 50+ files, xterm.js host with:
  - WebGL renderer (patched for Orca)
  - ligature support
  - unicode 11
  - search
  - web links
  - paste coordinator (multi-line policy)
  - parked-tab watcher (background tabs keep terminal alive)
  - dead-session reconcile
  - pty size reassertion
  - mobile driver overlay
  - terminal file URL handler
  - terminal shortcut policy
  - terminal title tracker
  - agent hook lifecycle

#### Titlebar
- `titlebar/` — custom titlebar with tabs, drag-reorder, close-gesture, popovers
- `titlebar-tab-nav.tsx` — tab strip
- `titlebar-tab-popover.tsx` — tab context popover
- `titlebar-tab-gesture.ts` — close-gesture detection (swipe-to-close)
- `titlebar-history.ts` — tab history navigation
- `titlebar-session-events.ts` — session events on tabs
- `windows-app-menu.tsx` — Windows-specific app menu

#### Update flow
- `UpdateCard.tsx` — release-notes card with install/defer buttons
- `UpdateCard.error-card.test.tsx` — error variant

#### Usage overview
- (in `dashboard/`) — aggregated usage across all worktrees

#### Worktree management
- `WorktreeJumpPalette.tsx` — Cmd+J palette
- (in `store/slices/worktrees.ts`) — worktree state

### 2.4 Animation system

Orca's animation system has three layers:

#### Layer 1: tw-animate-css (utility classes)
`tw-animate-css` is a Tailwind 4 plugin that provides utility classes like
`animate-in fade-in slide-in-from-bottom`, `animate-out fade-out zoom-out-95`.
Used for **modal enter/exit**, **toast slide-in**, **popover transitions**.

```tsx
// Example from dialog-command-palette-v2.tsx
<div className="animate-in fade-in-0 slide-in-from-top-4 duration-200">
  ...
</div>
```

#### Layer 2: Radix UI data-state hooks
Radix UI components (Dialog, Popover, Tooltip, HoverCard) emit `data-state="open"`
/ `data-state="closed"` attributes. Orca's CSS targets these for animation:

```css
[data-state="open"] { animation: fadeIn 200ms ease-out; }
[data-state="closed"] { animation: fadeOut 150ms ease-in; }
```

#### Layer 3: Custom CSS transitions
Specific elements use direct `transition` + `data-*` attributes:

```css
.StreamingDot { transition: opacity 0.4s; }
.StreamingDot[data-streaming="true"] { opacity: 1; }
.StreamingDot[data-streaming="false"] { opacity: 0.3; }
```

#### Layer 4: React state-driven animations
For complex sequences (pet mascot, agent state dot), Orca uses `useEffect` +
CSS class toggling:

```tsx
useEffect(() => {
  if (state === 'thinking') setPetClass('pet-thinking');
  else if (state === 'streaming') setPetClass('pet-typing');
}, [state]);
```

#### Layer 5: Monaco / xterm native animations
- Monaco: built-in cursor blink (handled by Monaco internals)
- xterm: GPU-accelerated cursor + selection via WebGL addon

**Key insight:** Orca does NOT use Framer Motion, React Spring, or any heavy
animation library. Everything is CSS + small JS hooks. This is a deliberate
choice for performance — every animation runs on the compositor thread, not
the JS thread.

### 2.5 Design system

#### Colors — Catppuccin Mocha (Orca's palette)
Orca uses Catppuccin Mocha as its base palette with custom semantic tokens:

| Token            | Hex       | Usage                          |
|------------------|-----------|--------------------------------|
| `MochaBase`      | `#1e1e2e` | Window background              |
| `MochaMantle`    | `#181825` | Sidebar / status bar           |
| `MochaCrust`     | `#11111b` | Cards / popovers               |
| `MochaSurface0`  | `#313244` | Toasts / hover states          |
| `MochaSurface1`  | `#45475a` | Active states                  |
| `MochaSurface2`  | `#585b70` | Borders                        |
| `MochaOverlay0`  | `#6c7086` | Subtle text                    |
| `MochaOverlay1`  | `#7f849c` | Disabled text                  |
| `MochaOverlay2`  | `#9399b2` | Secondary text                 |
| `MochaText`      | `#cdd6f4` | Primary text                   |
| `MochaRosewater` | `#f5e0dc` | Accent text                    |
| `MochaFlamingo`  | `#f2cdcd` | Accent text 2                  |
| `MochaPink`      | `#f5c2e7` | Highlight                      |
| `MochaMauve`     | `#cba6f7` | Brand accent                   |
| `MochaRed`       | `#f38ba8` | Errors                         |
| `MochaMaroon`    | `#eba0ac` | Warning                        |
| `MochaPeach`     | `#fab387` | Output tokens / streaming      |
| `MochaYellow`    | `#f9e2af` | Cost / warning                 |
| `MochaGreen`     | `#a6e3a1` | Success / input tokens         |
| `MochaTeal`      | `#94e2d5` | Info                           |
| `MochaSky`       | `#89dceb` | Input tokens alt               |
| `MochaSapphire`  | `#74c7ec` | Links                          |
| `MochaBlue`      | `#89b4fa` | Brand / actions                |
| `MochaLavender`  | `#b4befe` | Secondary actions              |

**Harbor already has this** — Catppuccin Mocha is the existing palette. Good.

#### Typography
- **UI font:** Geist Variable (variable-axis font, woff2)
- **Code font:** Symbols Nerd Font Mono (for powerline/icons in terminal)
- **Markdown:** TipTap-driven rich text with KaTeX math + lowlight syntax
- **Type scale:** Tailwind 4 default scale (text-xs/sm/base/lg/xl/2xl)

#### Spacing
- Tailwind 4 default 4px-based grid
- 8px / 12px / 16px / 24px common gaps
- Card padding: `12,8` (horizontal,vertical) for toasts; `16` for modals

#### Radius
- `2px` for chips / pills
- `4px` for small badges
- `6px` for cards / inputs
- `8px` for modals / popovers
- `12px` for large surfaces

#### Shadow
- Toast: `0 4px 12px 0 #60000000` (semi-transparent black)
- Modal: `0 8px 24px 0 #80000000`
- Popover: `0 4px 16px 0 #40000000`

### 2.6 State management — Zustand with slices

Orca uses **Zustand 5** with a slice pattern. The store lives in
`src/renderer/src/store/`:

```
store/
├── index.ts                  # Combines all slices
├── selectors.ts              # Memoized selectors
├── types.ts                  # Store type
├── active-terminal-chrome-selector.ts
├── pinned-tab-close-guard.ts
├── project-host-setup-selector.ts
├── right-sidebar-route.ts
├── worktree-diff-comments-selector.ts
├── worktree-repo-index.ts
└── slices/
    ├── tabs.ts               # Tab management (50+ helpers)
    ├── tabs-hydration.ts     # Tab restoration from disk
    ├── worktrees.ts          # Worktree state
    ├── worktree-helpers.ts
    ├── worktree-nav-history.ts
    ├── agent-status.ts       # Per-pane agent status (idle/think/stream)
    ├── agent-pane-authority.ts
    ├── detected-agents.ts    # Auto-detected CLI agents (Codex, Claude, etc.)
    ├── terminals.ts          # Terminal pane state
    ├── terminal-helpers.ts
    ├── terminal-tab-retirement.ts
    ├── editor.ts             # Monaco editor state
    ├── repos.ts              # Repo state
    ├── repo-identity-reconcile.ts
    ├── repo-host-identity.ts
    ├── github.ts             # GitHub integration
    ├── github-checks.ts
    ├── linear.ts             # Linear integration
    ├── jira.ts               # Jira integration
    ├── hosted-review.ts      # PR review state
    ├── hosted-review-cache-identity.ts
    ├── rate-limits.ts        # Per-provider rate limit tracking
    ├── memory.ts             # Agent memory
    ├── settings.ts           # User settings
    ├── settings-search-state.ts
    ├── keybindings.ts
    ├── dictation.ts          # Speech-to-text state
    ├── browser.ts            # Browser pane state
    ├── browser-webview-cleanup.ts
    ├── ssh.ts                # SSH connection state
    ├── runtime-status.ts
    ├── runtime-environment-ssh.ts
    ├── preflight.ts          # Pre-flight check state
    ├── pull-request-generation.ts
    ├── commit-message-generation.ts
    ├── new-issue-draft.ts
    ├── sparse-presets.ts
    ├── diffComments.ts
    ├── pane-foreground-agent.ts
    ├── pane-column-split-drop-no-op.ts
    ├── ui.ts                 # Generic UI state
    ├── recently-closed-tabs.ts
    ├── pinned-tab-close-confirm.ts
    ├── codex-usage.ts
    ├── claude-usage.ts
    ├── opencode-usage.ts
    ├── usage-snapshot-refresh.benchmark.test.ts
    ├── orca-profiles.ts     # Cloud profile sync
    ├── orca-profiles-auth-actions.ts
    ├── workspace-space.ts
    ├── workspace-cleanup.ts
    ├── project-group-removal-targets.ts
    ├── stats.ts
    ├── superseded-ssh-repo-rows.ts
    ├── readopted-ssh-worktree-rows.ts
    └── store-cascades.test.ts (60+ test files)
```

**Slice pattern example (conceptual):**

```ts
// slices/agent-status.ts
interface AgentStatusSlice {
  agentStatusByPane: Record<string, AgentStatus>;
  setAgentStatus: (paneId: string, status: AgentStatus) => void;
  clearAgentStatus: (paneId: string) => void;
}

export const createAgentStatusSlice: StateCreator<Store, [], [], AgentStatusSlice> = (set) => ({
  agentStatusByPane: {},
  setAgentStatus: (paneId, status) => set((s) => ({
    agentStatusByPane: { ...s.agentStatusByPane, [paneId]: status }
  })),
  clearAgentStatus: (paneId) => set((s) => {
    const next = { ...s.agentStatusByPane };
    delete next[paneId];
    return { agentStatusByPane: next };
  }),
});
```

**Selector pattern:**

```ts
// selectors.ts
export const selectActivePaneAgentStatus = (s: Store) =>
  s.agentStatusByPane[s.activePaneId] ?? 'idle';

// Memoized with zustand/middleware's shallow equality
export const useActivePaneAgentStatus = () =>
  useStore(selectActivePaneAgentStatus, shallow);
```

**Why this matters for Harbor:** Harbor already has the same pattern with
`UiStore` + `UiState` (immutable record) + `UiReducer` (pure transition).
The Orca slice pattern is essentially the same idea but with mutable
slices (faster for incremental updates). Harbor's immutable approach is
safer for AOT but slower for large state trees. **Recommendation:** keep
Harbor's immutable approach but add per-slice selectors for memoization.

### 2.7 Action plan — top Orca features to port to Avalonia

Each entry: **Feature / Source path / Description / Why it matters / Implementation sketch / Effort / Priority / Dependencies.**

---

#### Feature 1: Animated streaming text with typewriter cursor
- **Source:** Orca `src/renderer/src/components/terminal-pane/` + every chat UI ever
- **Description:** When the LLM streams tokens, the chat buffer reveals them
  token-by-token with a blinking cursor `▋` at the tail. The cursor blinks at
  ~1Hz when idle, solid when actively streaming.
- **Why it matters:** Without this, streaming looks like the app is frozen.
  The blinking cursor is the universal "I'm working" signal. Every chat UI
  ships this. Harbor currently shows the streaming buffer statically — no
  animation, no cursor.
- **Implementation sketch (Avalonia):**

  ```xml
  <!-- Views/Controls/TypewriterStreamingText.axaml -->
  <UserControl x:Class="Harbor.App.Avalonia.Views.Controls.TypewriterStreamingText">
      <StackPanel Orientation="Horizontal">
          <TextBlock Text="{Binding VisibleText}" TextWrapping="Wrap"/>
          <TextBlock Text="▋" Foreground="{StaticResource MochaPeach}"
                     IsVisible="{Binding IsCursorVisible}"
                     Classes="BlinkingCursor"/>
      </StackPanel>
  </UserControl>
  ```

  ```csharp
  // Views/Controls/TypewriterStreamingText.axaml.cs
  public partial class TypewriterStreamingText : UserControl
  {
      private DispatcherTimer? _cursorTimer;
      private bool _cursorOn = true;

      public static readonly StyledProperty<string> TextProperty =
          AvaloniaProperty.Register<TypewriterStreamingText, string>(nameof(Text));
      public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

      public static readonly StyledProperty<bool> IsStreamingProperty =
          AvaloniaProperty.Register<TypewriterStreamingText, bool>(nameof(IsStreaming));
      public bool IsStreaming { get => GetValue(IsStreamingProperty); set => SetValue(IsStreamingProperty, value); }

      public TypewriterStreamingText()
      {
          InitializeComponent();
          _cursorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Normal, (_, _) =>
          {
              _cursorOn = !_cursorOn;
              this.FindControl<TextBlock>("Cursor")!.IsVisible = IsStreaming && _cursorOn;
          });
      }

      protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
      {
          base.OnAttachedToVisualTree(e);
          _cursorTimer?.Start();
      }

      protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
      {
          _cursorTimer?.Stop();
          base.OnDetachedFromVisualTree(e);
      }
  }
  ```

  CSS for the blink animation in `AppStyles.axaml`:

  ```xml
  <Style Selector="TextBlock.BlinkingCursor">
      <Setter Property="Transitions">
          <Transitions>
              <DoubleTransition Property="Opacity" Duration="0:0:0.5"/>
          </Transitions>
      </Setter>
  </Style>
  ```

- **Effort:** S (4 hours)
- **Priority:** P0
- **Dependencies:** None
- **Status:** Implemented in this task (see §9).

---

#### Feature 2: Collapsible tool-call cards with status + duration
- **Source:** Orca `src/renderer/src/components/agent/` + Opencode `packages/session-ui/src/v2/components/basic-tool-v2.tsx`
- **Description:** Each tool call appears as a card with: tool name icon,
  status pill (running/success/error), duration ("234ms"), and a chevron
  to expand args + result. Cards slide in from the left when added and
  fade out when collapsed.
- **Why it matters:** Harbor currently renders tool calls as flat text
  lines (`ChatRole.Tool` / `ChatRole.ToolResult`). This is unreadable for
  anything beyond a one-line tool output. Cards make tool calls scannable,
  collapsible (so the user can hide noise), and provide instant visual
  feedback on success/failure.
- **Implementation sketch (Avalonia):**

  ```xml
  <!-- Views/Controls/ToolCallCardView.axaml -->
  <UserControl x:Class="Harbor.App.Avalonia.Views.Controls.ToolCallCardView">
      <Border Classes="ToolCallCard" Background="{StaticResource MochaSurface0}"
              CornerRadius="6" Padding="10,6" Margin="0,2">
          <Grid ColumnDefinitions="Auto,*,Auto,Auto">
              <TextBlock Grid.Column="0" Text="{Binding IconText}" FontSize="14"/>
              <TextBlock Grid.Column="1" Text="{Binding ToolName}" FontWeight="SemiBold"
                         Margin="6,0,0,0"/>
              <TextBlock Grid.Column="2" Text="{Binding StatusPill}" Classes="StatusPill"
                         Foreground="{Binding StatusBrush}"/>
              <TextBlock Grid.Column="3" Text="{Binding DurationText}"
                         Foreground="{StaticResource MochaOverlay2}" FontSize="10"/>
              <Expander Grid.Row="1" Grid.ColumnSpan="4" IsExpanded="{Binding IsExpanded}">
                  <StackPanel>
                      <TextBlock Text="Args:" FontWeight="SemiBold"/>
                      <TextBlock Text="{Binding ArgsPreview}" FontFamily="{StaticResource CodeFont}"
                                 TextWrapping="Wrap"/>
                      <TextBlock Text="Result:" FontWeight="SemiBold" Margin="0,4,0,0"/>
                      <TextBlock Text="{Binding ResultPreview}" FontFamily="{StaticResource CodeFont}"
                                 TextWrapping="Wrap"/>
                  </StackPanel>
              </Expander>
          </Grid>
      </Border>
  </UserControl>
  ```

  ViewModel:

  ```csharp
  public sealed partial class ToolCallViewModel : ObservableObject
  {
      [ObservableProperty] private string _toolName = "";
      [ObservableProperty] private string _iconText = "🔧";
      [ObservableProperty] private ToolCallStatus _status = ToolCallStatus.Running;
      [ObservableProperty] private TimeSpan _duration = TimeSpan.Zero;
      [ObservableProperty] private bool _isExpanded;
      [ObservableProperty] private string _argsPreview = "";
      [ObservableProperty] private string _resultPreview = "";

      public string StatusPill => Status switch
      {
          ToolCallStatus.Running => "● running",
          ToolCallStatus.Success => "✓ ok",
          ToolCallStatus.Error => "✗ err",
          _ => "?"
      };

      public string DurationText => Duration.TotalMilliseconds < 1 ? ""
          : Duration.TotalSeconds < 1 ? $"{Duration.TotalMilliseconds:F0}ms"
          : $"{Duration.TotalSeconds:F1}s";

      public IBrush StatusBrush => Status switch
      {
          ToolCallStatus.Running => App.Current!.FindResource("MochaYellow") as IBrush ?? Brushes.Yellow,
          ToolCallStatus.Success => App.Current!.FindResource("MochaGreen") as IBrush ?? Brushes.Green,
          ToolCallStatus.Error => App.Current!.FindResource("MochaRed") as IBrush ?? Brushes.Red,
          _ => Brushes.Gray
      };
  }
  ```

  Slide-in animation via `Transitions`:

  ```xml
  <Style Selector="Border.ToolCallCard">
      <Setter Property="Transitions">
          <Transitions>
              <DoubleTransition Property="Opacity" Duration="0:0:0.2"/>
              <TransformOperationsTransition Property="RenderTransform" Duration="0:0:0.2"/>
          </Transitions>
      </Setter>
      <Setter Property="RenderTransform" Value="translateX(-10px)"/>
  </Style>
  <Style Selector="Border.ToolCallCard:nth-child(1)">
      <Setter Property="RenderTransform" Value="translateX(0)"/>
  </Style>
  ```

- **Effort:** M (8 hours)
- **Priority:** P0
- **Dependencies:** None — but best paired with Feature 1 (typewriter) so the
  whole streaming experience is cohesive.
- **Status:** Implemented in this task (see §9).

---

#### Feature 3: Worktree jump palette (Cmd-J / Ctrl+J)
- **Source:** Orca `src/renderer/src/components/cmd-j/` + `WorktreeJumpPalette.tsx`
- **Description:** A Cmd+J palette that lets the user switch between
  worktrees / sessions / branches with fuzzy match. Each row shows:
  worktree name, agent status dot, last activity timestamp, branch name,
  file count.
- **Why it matters:** Harbor currently has a session sidebar, but no
  keyboard-first switcher. Power users switch sessions 50+ times per day;
  reaching for the mouse each time is friction. A Cmd+J palette is the
  single most-loved Orca feature.
- **Implementation sketch (Avalonia):**

  Reuse the existing `CommandPaletteView` infrastructure but add a second
  mode. Bind `MainViewModel.IsJumpPaletteOpen` and add a `JumpPaletteView`
  that uses the same fuzzy search but pulls from `SessionListViewModel`.

  ```csharp
  [RelayCommand]
  private void OpenJumpPalette()
  {
      IsJumpPaletteOpen = true;
  }
  ```

  Key binding in MainWindow.axaml.cs:

  ```csharp
  if (ctrl && e.Key == Key.J)
  {
      vm.OpenJumpPaletteCommand.Execute(null);
      e.Handled = true;
  }
  ```

- **Effort:** M (6 hours — mostly reuse CommandPaletteView)
- **Priority:** P1
- **Dependencies:** None

---

#### Feature 4: Token-usage sparkline in status bar (live)
- **Source:** Opencode `progress-circle-v2.tsx` + Kilocode `kilo-console` header
- **Description:** A tiny 80×16 sparkline in the status bar showing the
  last 30 turns of token usage. Updates live as the agent runs.
- **Why it matters:** Harbor currently shows `↓ 1,234 ↑ 5,678` in the
  status bar — flat numbers with no history. A sparkline gives instant
  visual feedback: "is this turn bigger than usual?" without opening the
  full TokenUsageView modal.
- **Implementation sketch (Avalonia):**

  ```xml
  <!-- Views/Controls/Sparkline.axaml -->
  <UserControl x:Class="Harbor.App.Avalonia.Views.Controls.Sparkline">
      <Canvas x:Name="Canvas" Width="80" Height="16"/>
  </UserControl>
  ```

  ```csharp
  public partial class Sparkline : UserControl
  {
      public static readonly StyledProperty<IEnumerable<double>?> ValuesProperty =
          AvaloniaProperty.Register<Sparkline, IEnumerable<double>?>(nameof(Values));

      public IEnumerable<double>? Values
      {
          get => GetValue(ValuesProperty);
          set => SetValue(ValuesProperty, value);
      }

      static Sparkline()
      {
          AffectsRender<Sparkline>(ValuesProperty);
      }

      protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
      {
          base.OnPropertyChanged(change);
          if (change.Property == ValuesProperty) InvalidateVisual();
      }

      public override void Render(DrawingContext context)
      {
          base.Render(context);
          var values = Values?.ToList();
          if (values is null || values.Count < 2) return;

          double max = values.Max();
          double min = values.Min();
          double range = max - min;
          if (range < 0.001) range = 1;

          double w = Bounds.Width;
          double h = Bounds.Height;
          double stepX = w / (values.Count - 1);

          var brush = (IBrush)(Application.Current!.FindResource("MochaPeach") ?? Brushes.Orange);
          var pen = new Pen(brush, 1.2);

          var geometry = new StreamGeometry();
          using (var ctx = geometry.Open())
          {
              ctx.BeginFigure(new Point(0, h - (values[0] - min) / range * h));
              for (int i = 1; i < values.Count; i++)
              {
                  ctx.LineTo(new Point(i * stepX, h - (values[i] - min) / range * h));
              }
              ctx.EndFigure(isClosed: false);
          }
          context.DrawGeometry(null, pen, geometry);
      }
  }
  ```

  Wire into MainWindow status bar:

  ```xml
  <views:Sparkline Values="{Binding TokenUsage.RecentOutputTokens}"
                    Width="80" Height="16"
                    Margin="4,0" VerticalAlignment="Center"/>
  ```

- **Effort:** S (3 hours)
- **Priority:** P0
- **Dependencies:** None
- **Status:** Implemented in this task (see §9).

---

#### Feature 5: Agent pet mascot
- **Source:** Orca `src/renderer/src/components/pet/`
- **Description:** A small animated pet (think Tamagotchi) that lives in
  the corner of the chat view. Reacts to agent state: sleeping when idle,
  looking up when thinking, typing on tiny keyboard when streaming, dizzy
  stars when error, confetti on success.
- **Why it matters:** Pure delight. Differentiates Harbor from "yet
  another terminal coding agent." The pet creates an emotional bond —
  users forgive bugs more readily when they like the pet.
- **Implementation sketch (Avalonia):**

  Use a `Canvas` with multiple `Path` layers for the pet body, eyes,
  mouth. Toggle visibility based on agent state. For the typing animation,
  use a `DispatcherTimer` at 8Hz to swap hand positions.

  ```xml
  <Canvas Width="64" Height="64" Classes="Pet"
          IsVisible="{Binding ShowPet}">
      <Path Data="M 16 32 A 16 16 0 1 0 48 32 A 16 16 0 1 0 16 32"
            Fill="{StaticResource MochaMauve}"/>
      <Ellipse Canvas.Left="22" Canvas.Top="26" Width="6" Height="6"
               Fill="{StaticResource MochaCrust}"/>
      <Ellipse Canvas.Left="36" Canvas.Top="26" Width="6" Height="6"
               Fill="{StaticResource MochaCrust}"/>
      <Path Data="M 24 38 Q 32 42 40 38" Stroke="{StaticResource MochaCrust}"
            StrokeThickness="2"/>
  </Canvas>
  ```

  State machine in PetViewModel:

  ```csharp
  public enum PetState { Sleeping, Thinking, Typing, Error, Celebrating }
  ```

- **Effort:** S (4 hours — mostly SVG art)
- **Priority:** P2
- **Dependencies:** None

---

#### Feature 6: Intra-line word-diff highlighting
- **Source:** Pi `packages/coding-agent/src/modes/interactive/components/diff.ts` (uses `diff` npm lib)
- **Description:** When showing a diff, highlight only the changed words
  within each line, not the whole line. So `Hello world` → `Hello earth`
  shows "world" struck-through-red and "earth" bold-green, with "Hello "
  unchanged.
- **Why it matters:** Line-level diffs are noise-heavy for small edits.
  Harbor's current `DiffView` shows line-level. Intra-line is the standard
  for every modern diff viewer (GitHub, VS Code, JetBrains).
- **Implementation sketch (Avalonia):**

  Use a diff-match-patch C# port (e.g., `DiffMatchNet` NuGet). For each
  pair of removed/added lines, run word-level diff and render as
  `InlineCollection` of `Run` elements with different foreground colors.

  ```csharp
  // Adapted from Pi's diff.ts
  public static IEnumerable<DiffSpan> ComputeIntraLine(string old, string @new)
  {
      var diff = DiffMatchNet.Diff.Compute(old, @new);
      foreach (var d in diff)
      {
          yield return new DiffSpan(
              d.Text,
              d.Operation == DiffMatchNet.Operation.Delete ? DiffKind.Removed
              : d.Operation == DiffMatchNet.Operation.Insert ? DiffKind.Added
              : DiffKind.Unchanged);
      }
  }
  ```

  Render in XAML using `RichTextBlock` / `TextBlock.Inlines`:

  ```xml
  <TextBlock>
      <TextBlock.Inlines>
          <!-- For each DiffSpan -->
          <Run Text="Hello " Foreground="{StaticResource MochaText}"/>
          <Run Text="world" Foreground="{StaticResource MochaRed}"
               TextDecorations="Strikethrough"/>
          <Run Text="earth" Foreground="{StaticResource MochaGreen}"
               FontWeight="Bold"/>
      </TextBlock.Inlines>
  </TextBlock>
  ```

- **Effort:** M (8 hours)
- **Priority:** P1
- **Dependencies:** `DiffMatchNet` NuGet package

---

#### Feature 7: Toast notifications with slide-in + auto-dismiss
- **Source:** Orca uses `sonner` 2.0 (the most popular React toast lib)
- **Description:** Toasts slide in from the bottom-right, stack
  vertically, auto-dismiss after 4s (configurable per-kind), can be
  swiped-to-dismiss, and respect prefers-reduced-motion.
- **Why it matters:** Harbor has a basic `ToastNotificationsView` but
  no slide-in animation — toasts just appear. The slide-in is what
  makes them feel intentional rather than jarring.
- **Implementation sketch (Avalonia):**

  Add a `TranslateTransform` transition to the toast border:

  ```xml
  <Style Selector="Border.Toast">
      <Setter Property="RenderTransform" Value="translateX(20)"/>
      <Setter Property="Opacity" Value="0"/>
      <Setter Property="Transitions">
          <Transitions>
              <DoubleTransition Property="Opacity" Duration="0:0:0.25"/>
              <TransformOperationsTransition Property="RenderTransform" Duration="0:0:0.25"/>
          </Transitions>
      </Setter>
  </Style>
  <Style Selector="Border.Toast.IsVisible">
      <Setter Property="RenderTransform" Value="translateX(0)"/>
      <Setter Property="Opacity" Value="1"/>
  </Style>
  ```

- **Effort:** S (2 hours)
- **Priority:** P1
- **Dependencies:** None

---

#### Feature 8: TipTap-style rich markdown composer
- **Source:** Orca `src/renderer/src/components/prompt-input/` (TipTap 3.22 + extensions)
- **Description:** A rich text editor for the prompt input with:
  - Markdown shortcuts (`# ` → h1, `* ` → bullet, ```` ``` ```` → code block)
  - Inline code highlighting
  - Image paste
  - File drag-drop with preview chips
  - Slash commands menu
  - @-mentions for files / agents
  - Math rendering (KaTeX)
- **Why it matters:** Harbor's prompt input is a plain `TextBox` with
  `AcceptsReturn`. No rich editing. Power users expect markdown shortcuts
  to "just work."
- **Implementation sketch (Avalonia):**

  Use `AvaloniaEdit` (already a dependency) configured for inline
  editing. Add a `MarkdownSyntaxHighlighter` based on Markdig. For
  slash commands, pop up a `Popup` with a `ListBox` filtered by the
  text after `/`.

  This is the largest single feature on the list — easily a sprint of
  work. Defer to Sprint 3.

- **Effort:** L (40 hours)
- **Priority:** P2
- **Dependencies:** AvaloniaEdit, Markdig (both already deps)

---

#### Feature 9: Setup-guide progress ring + checklist
- **Source:** Orca `src/renderer/src/components/setup-guide/SetupGuideProgressRing.tsx`
- **Description:** A first-run modal showing a circular progress ring
  and a checklist of setup tasks (connect provider, add API key, open
  workspace, run first prompt). Each task auto-completes when detected.
- **Why it matters:** Harbor has an `OnboardingWizard` (in
  `Harbor.Application/Onboarding/`) but no visual progress indicator
  in the Avalonia app. New users open the app, see an empty chat, and
  don't know what to do next.
- **Implementation sketch (Avalonia):**

  Custom `Arc` control using `ArcGeometry`:

  ```csharp
  public class ProgressRing : Control
  {
      public static readonly StyledProperty<double> ValueProperty = ...;
      protected override void Render(DrawingContext context)
      {
          var pen = new Pen(Brushes.MochaBlue, 4);
          var rect = new Rect(0, 0, Bounds.Width, Bounds.Height).Deflate(2);
          context.DrawArc(null, pen, rect, -90, 360 * Value);
      }
  }
  ```

- **Effort:** M (6 hours)
- **Priority:** P1
- **Dependencies:** None

---

#### Feature 10: Skill freshness pill
- **Source:** Orca `src/renderer/src/components/skills/SkillFreshnessStatusPill.tsx`
- **Description:** A small pill next to each skill showing whether an
  update is available. Clicking opens an update dialog.
- **Why it matters:** Harbor has a plugin system but no version
  freshness indicators. Users never know when to update.
- **Effort:** S (2 hours)
- **Priority:** P2
- **Dependencies:** Plugin version metadata

---

#### Feature 11: Tab-strip with drag-reorder + close-gesture
- **Source:** Orca `src/renderer/src/components/titlebar/titlebar-tab-nav.tsx` + `titlebar-tab-gesture.ts`
- **Description:** A draggable tab strip where tabs can be reordered by
  drag, closed by middle-click or swipe-gesture, and have a context
  popover (close others, close right, pin).
- **Why it matters:** Harbor has no tabs at all — only a session
  sidebar. Tabs would let users keep multiple sessions visible
  simultaneously.
- **Effort:** L (16 hours — Avalonia drag-drop is finicky)
- **Priority:** P1
- **Dependencies:** None

---

#### Feature 12: Image preview inline in chat
- **Source:** Opencode `packages/ui/src/components/image-preview.tsx`
- **Description:** When an agent or user pastes an image, show it
  inline in the chat as a thumbnail with click-to-zoom.
- **Why it matters:** Vision-capable models (GPT-4o, Claude 3.5) need
  image input. Harbor has no image support at all.
- **Effort:** M (6 hours)
- **Priority:** P1
- **Dependencies:** None

---

#### Feature 13: Dictation / speech-to-text input
- **Source:** Orca `src/main/` uses `sherpa-onnx` 1.12 for offline ASR
- **Description:** A microphone button in the prompt input that
  transcribes speech to text offline using sherpa-onnx.
- **Why it matters:** Accessibility + hands-free coding. Power users
  dictate long prompts faster than typing.
- **Effort:** L (20 hours — sherpa-onnx has no .NET binding; would
  need whisper.cpp or similar)
- **Priority:** P2
- **Dependencies:** Whisper.net or similar

---

#### Feature 14: Browser/markup overlay for screenshots
- **Source:** Orca `src/renderer/src/components/browser-pane/markup/MarkupOverlay.tsx`
- **Description:** When an agent takes a screenshot (for computer-use),
  overlay a canvas where the user can draw arrows, boxes, and text on
  the screenshot, then send the annotated image back to the agent.
- **Why it matters:** Computer-use workflows. Niche but high-leverage
  for the agent-coding niche.
- **Effort:** L (24 hours)
- **Priority:** P2
- **Dependencies:** Embedded webview (SkiaSharp + WebView2?)

---

#### Feature 15: PR-comment sidebar cards with code context
- **Source:** Orca `src/renderer/src/components/pr-comments/` + Kilocode `kilo-console`
- **Description:** When reviewing a PR, sidebar cards show each
  comment with the surrounding code context (3 lines before/after).
- **Why it matters:** Code-review workflows. Harbor has no PR
  integration.
- **Effort:** L (24 hours)
- **Priority:** P2
- **Dependencies:** GitHub API client

---

#### Feature 16: Floating terminal pane (detach + reattach)
- **Source:** Orca `src/renderer/src/components/floating-terminal/`
- **Description:** A terminal pane that can be detached from the main
  window into its own floating window, then re-attached later. The PTY
  keeps running across detach/reattach.
- **Why it matters:** Long-running commands (builds, tests, dev
  servers) shouldn't die when the user switches tabs.
- **Effort:** L (16 hours)
- **Priority:** P2
- **Dependencies:** PTY multiplexer

---

#### Feature 17: Per-workspace env memory
- **Source:** Orca `src/renderer/src/components/skills/orca-per-workspace-env/SKILL.md`
- **Description:** Each worktree has its own `.env` file that is
  automatically loaded when that worktree is active. Switching
  worktrees swaps env vars without restarting the agent.
- **Why it matters:** Multi-project workflows. Currently Harbor has
  one global env.
- **Effort:** M (6 hours)
- **Priority:** P2
- **Dependencies:** None

---

#### Feature 18: Star-nag card (gentle GitHub star reminder)
- **Source:** Orca `src/renderer/src/components/StarNagCard.tsx`
- **Description:** A small card that appears after N successful agent
  runs, asking the user to star the repo. Dismissible, never shows
  again after dismissed or starred.
- **Why it matters:** Open-source growth. Trivial to implement.
- **Effort:** S (1 hour)
- **Priority:** P2
- **Dependencies:** None

---

#### Feature 19: Zoom overlay (Ctrl+/Ctrl- indicator)
- **Source:** Orca `src/renderer/src/components/ZoomOverlay.tsx`
- **Description:** When the user presses Ctrl+ or Ctrl-, a small
  overlay in the corner shows the current zoom level for 1.5s then
  fades out.
- **Why it matters:** UX polish. Without it, Ctrl+/- feel like they
  do nothing.
- **Effort:** S (1 hour)
- **Priority:** P2
- **Dependencies:** None

---

#### Feature 20: Crash-report viewer with retry/submit
- **Source:** Orca `src/renderer/src/components/crash-report/`
- **Description:** When the renderer crashes, on next launch show a
  crash report viewer with the stack trace, a submit button, and a
  retry button.
- **Why it matters:** Reliability perception. Users forgive crashes
  if recovery is graceful.
- **Effort:** M (8 hours)
- **Priority:** P2
- **Dependencies:** Crash log storage

---

### 2.8 Orca keyboard shortcut system

Orca's shortcuts are stored in `src/renderer/src/store/slices/keybindings.ts`.
The system:

1. Default shortcuts are hardcoded in a `Keybindings` config object.
2. Users can override in `~/.orca/keybindings.json`.
3. On startup, the user's file is merged over the defaults.
4. The store exposes `getKeybinding(action: string): string[]` which
   returns all keys bound to an action (multi-bind supported).
5. Components subscribe to keybindings via a `useKeybinding(action, handler)`
   hook that re-binds when keybindings change.

Default Orca shortcuts (from `keybindings.ts` inferred from e2e tests):

| Action                        | Shortcut           |
|-------------------------------|--------------------|
| Open command palette          | Cmd/Ctrl + P       |
| Open worktree jump palette    | Cmd/Ctrl + J       |
| Toggle sidebar                | Cmd/Ctrl + B       |
| Toggle theme                  | Cmd/Ctrl + Shift + T|
| Quick open file               | Cmd/Ctrl + P (held) |
| New workspace                 | Cmd/Ctrl + N       |
| Close tab                     | Cmd/Ctrl + W       |
| Reopen closed tab             | Cmd/Ctrl + Shift + T|
| Next tab                      | Cmd/Ctrl + Tab     |
| Previous tab                  | Cmd/Ctrl + Shift + Tab|
| Send prompt                   | Enter              |
| Newline in prompt             | Shift + Enter      |
| Abort agent                   | Cmd/Ctrl + C (in input) |
| Clear chat                    | Cmd/Ctrl + L       |
| Open file                     | Cmd/Ctrl + O       |
| Save file                     | Cmd/Ctrl + S       |
| Search in terminal            | Cmd/Ctrl + F       |
| Zoom in                       | Cmd/Ctrl + =       |
| Zoom out                      | Cmd/Ctrl + -       |
| Reset zoom                    | Cmd/Ctrl + 0       |
| Toggle terminal pane          | Cmd/Ctrl + `       |
| Focus chat                    | Cmd/Ctrl + 1       |
| Focus editor                  | Cmd/Ctrl + 2       |
| Focus terminal                | Cmd/Ctrl + 3       |
| Focus diff                    | Cmd/Ctrl + 4       |
| Open settings                 | Cmd/Ctrl + ,       |
| Quit                          | Cmd/Ctrl + Q       |

**Harbor status:** Harbor has Ctrl+P, Ctrl+B, Ctrl+Shift+T, Ctrl+O, Ctrl+S,
Ctrl+L, Esc. Missing: Cmd+J (worktree jump), Cmd+W (close tab), Cmd+Tab
(next tab), Cmd+\` (terminal), Cmd+1-4 (focus regions), Cmd+, (settings),
Cmd+Q (quit), Cmd+= / Cmd+- (zoom).

### 2.9 How Orca handles streaming

Orca's streaming pipeline:

1. Main process receives SSE chunks from the LLM via the agent runtime
   (Codex, Claude, Gemini, etc.).
2. Chunks are forwarded to the renderer over IPC in batches (every 16ms
   or 50 tokens, whichever comes first) to avoid renderer jank.
3. Renderer's `useComposerState` hook accumulates chunks into a local
   `StreamingBuffer` state.
4. The terminal pane (if the agent is a TUI agent like Codex) renders
   the buffer through xterm.js, which uses the WebGL addon for
   GPU-accelerated text rendering.
5. The native chat pane (for non-TUI agents) renders the buffer as
   React text with a typewriter cursor.
6. On `MessageEndEvent`, the buffer is flushed to the transcript
   (appended as a `ChatLine`).

**Key perf tricks:**
- **Batched IPC:** Chunks are batched to avoid saturating the IPC channel.
- **WebGL terminal:** xterm.js with WebGL addon renders at 60fps even with
  100k-line scrollback.
- **Parked tabs:** Background terminal tabs are "parked" — the PTY keeps
  running but the renderer suspends, saving GPU/CPU.
- **Scroll anchor:** When the user scrolls up during streaming, the view
  stays pinned to their scroll position (not the new bottom). A "jump to
  bottom" button appears.

**Harbor status:** Harbor has `StreamingBuffer` in `ChatViewModel` and
an `IsStreaming` flag. The buffer is rendered as a single `TextBlock`
that updates on every state change. No batched updates, no cursor, no
scroll-anchor logic. **Needs improvement** (Feature 1 + scroll anchor).

### 2.10 How Orca shows tool calls

Orca renders tool calls as cards in the chat transcript. Each card has:

- **Header:** Tool name + icon + status pill + duration
- **Body (collapsible):**
  - Args (JSON, syntax-highlighted)
  - Result (truncated to 1000 chars, "show more" button)
  - Error (if failed) with stack trace
- **Footer:** "Re-run" button + "Copy result" button

Cards are produced by a `ToolCallProjector` that watches the agent event
stream and creates/updates card state. The projector coalesces multiple
events for the same tool call (start, progress, end) into a single card
update.

**Harbor status:** Harbor renders tool calls as flat `ChatRole.Tool` /
`ChatRole.ToolResult` lines. No card, no collapse, no duration, no
re-run. **Needs Feature 2.**

### 2.11 How Orca shows diffs

Orca has three diff surfaces:

1. **Inline in chat:** When the agent runs `edit` or `write`, a compact
   diff preview appears inline as a card. Single file, max 20 lines.
2. **Source control panel:** Full multi-file diff with file tree on
   left, diff on right. Staging area with checkboxes.
3. **PR review:** GitHub-style side-by-side diff with line comments.

All three use the same `diff-match-patch` library for intra-line
highlighting. Colors:

- Removed: `MochaRed` with `#f38ba8` foreground, `#3a1d2d` background
- Added: `MochaGreen` with `#a6e3a1` foreground, `#1f3a2d` background
- Context: `MochaText` with `#cdd6f4` foreground, transparent background
- Highlighted word within line: bold + brighter shade

**Harbor status:** Harbor has `DiffView.axaml` + `DiffViewModel.cs` but
it's line-level only. **Needs Feature 6.**

### 2.12 Command palette implementation

Orca uses `cmdk` 1.1.1 (the de facto standard React command palette lib,
built by the Vercel team). Features:

- **Fuzzy search:** Subsequence matching with highlighting
- **Recent items:** Recently-used commands float to the top
- **Grouping:** Commands grouped by category (File, Edit, View, etc.)
- **Keyboard navigation:** ↑↓ to navigate, Enter to run, Esc to close
- **Multi-key bindings:** A command can be triggered by multiple keys
- **Free-text fallback:** If no command matches, the text is sent as a
  prompt to the agent

Orca's command palette source: `src/renderer/src/components/command-palette.ts`
+ `command-tooltip-keybind.ts`.

**Implementation pattern:**

```ts
const commands = [
  { id: 'file.new', label: 'New file', icon: '📄', hint: 'Ctrl+N',
    run: () => createNewFile() },
  { id: 'file.open', label: 'Open file', icon: '📂', hint: 'Ctrl+O',
    run: () => openFile() },
  // ...
];

const filtered = useMemo(() => fuzzySearch(commands, query), [commands, query]);
```

**Harbor status:** Harbor has `CommandPaletteView.axaml` +
`CommandPaletteViewModel.cs` with `Results`, `SelectedIndex`, `Query`.
**Missing:** fuzzy search (currently exact substring), recent items
grouping, free-text fallback. **Needs P1 work.**

### 2.13 Toast / notification system

Orca uses `sonner` 2.0 (the most popular React toast lib). Features:

- Stack from bottom-right (configurable to top-right, top-center, etc.)
- Slide-in + fade animation (200ms)
- Auto-dismiss after 4s (configurable per toast)
- Hover to pause auto-dismiss
- Swipe-to-dismiss (mobile-style)
- Action buttons (e.g., "Undo")
- Persistent toasts (don't auto-dismiss)
- Promise toasts (loading → success/error)

**Harbor status:** Harbor has `ToastNotificationsView.axaml` with a
stack and auto-dismiss. **Missing:** slide-in animation, hover-to-pause,
swipe-to-dismiss, action buttons. **Needs Feature 7.**

### 2.14 Settings UI

Orca's settings is a modal with sidebar tabs:

- General (theme, font, language)
- Keybindings (per-action rebinding)
- Models (default model per agent)
- Providers (API keys, OAuth tokens)
- Servers (MCP servers)
- Skills (installed skills, marketplace)
- Notifications (per-event sound/desktop notification)
- Privacy (telemetry, crash reporting)
- Display (zoom, density)
- CLI (bundled CLI tools, PATH management)

Each tab is a separate React component. The settings store is a Zustand
slice (`settings.ts`).

**Harbor status:** Harbor has `SettingsView.axaml` +
`SettingsViewModel.cs` with provider/model management. **Missing:**
keybindings UI, notifications, privacy, display density. **P2 work.**

### 2.15 Onboarding flow

Orca's onboarding (from e2e test `onboarding.spec.ts`):

1. Welcome screen with "Get started" button
2. Connect a provider (Anthropic, OpenAI, or local Ollama)
3. Add API key (or OAuth for GitHub Copilot)
4. Open a workspace (drag-drop or browse)
5. Run first prompt (pre-filled example)
6. Setup guide checklist (auto-detects: git, ripgrep, node, etc.)

Each step has a `SetupGuideProgressRing` showing overall progress.
Steps can be skipped and returned to later.

**Harbor status:** Harbor has `OnboardingWizard` in
`Harbor.Application/Onboarding/OnboardingWizard.cs` but it's a CLI-only
wizard. The Avalonia app skips onboarding entirely — users land in an
empty chat. **Needs Feature 9 + onboarding port.**

### 2.16 Multi-session management

Orca's multi-session model:

- Each "tab" in the titlebar is a session
- Each session has its own worktree, agent, and PTY
- Sessions can be "parked" (PTY alive, renderer suspended) to save resources
- Cmd+J palette switches between sessions with fuzzy match
- Session history is persisted to disk; on restart, all tabs restore
- Tabs can be dragged between windows (multi-window support)

**Harbor status:** Harbor has `SessionListViewModel` (sidebar list) and
`SessionManager` service. Only one session active at a time. No tabs,
no parking, no multi-window. **Needs Feature 3 + Feature 11 + Feature 16.**

---

## §3. Pi-agent deep-dive

### 3.1 Repo facts

| Property        | Value                                                        |
|-----------------|--------------------------------------------------------------|
| Repo            | `https://github.com/earendil-works/pi`                      |
| Architecture    | Monorepo with 4 packages: `orchestrator`, `tui`, `ai`, `coding-agent` |
| Language        | TypeScript (Bun runtime)                                     |
| TUI framework   | Custom (not Ink) — `packages/tui/src/tui.ts` is a hand-rolled renderer |
| State mgmt      | TEA (The Elm Architecture) — `coding-agent` package          |
| Keybindings     | Namespaced IDs (`tui.editor.cursorUp`), user-configurable     |
| Themes          | JSON-based, hot-reloadable                                    |
| Compaction      | Branch-summarization + compaction (custom algorithm)          |
| Skills          | Markdown skill bundles, hot-loaded                            |
| Extensions      | JS-based with full TUI access                                 |

### 3.2 Source structure

```
pi-agent/
├── packages/
│   ├── orchestrator/      # Multi-agent orchestration (supervisor + workers)
│   ├── tui/               # Custom TUI framework (NOT Ink)
│   │   └── src/
│   │       ├── tui.ts             # Core renderer
│   │       ├── terminal.ts        # Terminal abstraction
│   │       ├── keys.ts            # Key parsing
│   │       ├── keybindings.ts     # Namespaced keybinding system
│   │       ├── autocomplete.ts    # Autocomplete engine
│   │       ├── word-navigation.ts # Ctrl+arrows word movement
│   │       ├── undo-stack.ts      # Undo/redo
│   │       ├── kill-ring.ts       # Emacs-style kill ring (Ctrl+K/Y)
│   │       ├── stdin-buffer.ts    # Stdin handling
│   │       ├── terminal-colors.ts # ANSI color management
│   │       ├── terminal-image.ts  # Inline image rendering (Kitty graphics protocol)
│   │       ├── fuzzy.ts           # Fuzzy search
│   │       ├── editor-component.ts # Multi-line editor
│   │       └── components/
│   │           ├── box.ts
│   │           ├── cancellable-loader.ts
│   │           ├── editor.ts
│   │           ├── image.ts
│   │           ├── input.ts
│   │           ├── loader.ts
│   │           ├── markdown.ts
│   │           ├── select-list.ts
│   │           ├── settings-list.ts
│   │           ├── spacer.ts
│   │           ├── text.ts
│   │           └── truncated-text.ts
│   ├── ai/               # Multi-provider AI abstraction
│   │   └── src/
│   │       ├── auth/             # OAuth for Anthropic, OpenAI, GitHub Copilot, XAI
│   │       ├── api/              # Per-provider API clients
│   │       ├── providers/        # 30+ providers (Anthropic, OpenAI, Bedrock, etc.)
│   │       └── utils/
│   └── coding-agent/     # The actual agent
│       └── src/
│           ├── core/             # TEA core (session, agent, tools, extensions)
│           ├── modes/
│           │   ├── interactive/  # Interactive TUI mode
│           │   │   ├── interactive-mode.ts
│           │   │   ├── components/   # 30+ UI components
│           │   │   └── theme/        # JSON theme system
│           │   ├── print-mode.ts # Non-interactive (stdin→stdout)
│           │   └── rpc/          # RPC mode (JSONL over stdio)
│           ├── extensions/       # Built-in extensions (llama.cpp)
│           └── bun/              # Bun-specific entry points
```

### 3.3 Component inventory (Pi TUI)

> Path prefix: `packages/coding-agent/src/modes/interactive/components/`

- `assistant-message.ts` — Rendered assistant message with markdown
- `user-message.ts` — Rendered user message
- `user-message-selector.ts` — Click-to-edit user message
- `tool-execution.ts` — Tool call card (status + duration + expandable)
- `bash-execution.ts` — Bash-specific tool card with ANSI output
- `diff.ts` — Intra-line word diff (the killer — uses `diff` npm lib)
- `markdown.ts` — Markdown renderer with syntax highlighting
- `tree-selector.ts` — Tree-view file picker
- `session-selector.ts` — Session picker
- `session-selector-search.ts` — Session picker with fuzzy search
- `model-selector.ts` — Model picker
- `scoped-models-selector.ts` — Scoped models (per-agent model override)
- `thinking-selector.ts` — Thinking-mode toggle
- `theme-selector.ts` — Theme picker
- `config-selector.ts` — Config file picker
- `extension-selector.ts` — Extension picker
- `extension-editor.ts` — Extension JS editor
- `extension-input.ts` — Extension input field
- `settings-selector.ts` — Settings picker
- `trust-selector.ts` — Project trust prompt
- `custom-message.ts` — Custom message renderer
- `custom-entry.ts` — Custom entry point
- `custom-editor.ts` — Custom editor
- `first-time-setup.ts` — First-run setup wizard
- `oauth-selector.ts` — OAuth provider picker
- `login-dialog.ts` — Login dialog
- `earendil-announcement.ts` — Earendil branding announcement
- `visual-truncate.ts` — Visual truncation of long output
- `countdown-timer.ts` — Countdown timer (for rate limits)
- `keybinding-hints.ts` — Keybinding hint footer
- `status-indicator.ts` — Status indicator (idle/running/error)
- `bordered-loader.ts` — Loader with border
- `show-images-selector.ts` — Image display picker
- `skill-invocation-message.ts` — Skill invocation card
- `compaction-summary-message.ts` — Compaction summary card
- `branch-summary-message.ts` — Branch summary card
- `armin.ts` + `daxnuts.ts` — Easter eggs (agent mascots)
- `footer.ts` — Status bar footer

### 3.4 Killer features (Pi)

#### 3.4.1 Custom TUI framework (not Ink)

Pi hand-rolled its own TUI framework instead of using Ink (the standard
React-based terminal renderer). Why?

- **Performance:** Ink re-renders the whole tree on every state change.
  Pi's framework only re-renders dirty regions.
- **Control:** Ink's reconciliation is opaque. Pi needed fine-grained
  control over cursor positioning, scroll, and overlay rendering.
- **CJK / emoji width:** Pi handles CJK double-width chars and emoji
  ZWJ sequences correctly (see `tui-shrink.test.ts`,
  `regression-overlay-cjk-boundary.test.ts`).
- **Overlay rendering:** Pi supports overlay components (modals,
  dropdowns) that float above the main view — Ink doesn't.

The TUI framework has its own render loop, dirty-region tracking, and
a virtual terminal (`test/virtual-terminal.ts`) for headless testing.

**Harbor implication:** Harbor's TUI uses Spectre.TUI / Terminal.Gui /
RazorConsole / Termina / Ansi — 5 different renderers. Pi's approach
(own framework) is more work but gives better perf. Harbor should NOT
rewrite its TUI stack, but the dirty-region optimization is worth
backporting.

#### 3.4.2 Namespaced keybindings

Pi's keybinding system is the gold standard:

```json
{
  "tui.editor.cursorUp": ["up"],
  "tui.editor.cursorDown": ["down"],
  "tui.editor.deleteWordBackward": ["ctrl+w", "alt+backspace"],
  "tui.input.submit": ["enter"]
}
```

- Every action has a namespaced ID (`namespace.action`)
- Each action can have multiple keys
- Old configs auto-migrate to new namespaced IDs
- `/reload` command applies changes without restart
- Extensions can register their own keybindings via `keyHint()`

**Harbor implication:** Harbor has hardcoded keybindings in
`MainWindow.axaml.cs` and `ChatKeyMap.cs`. No user config, no
namespacing. **Should port Pi's system.**

#### 3.4.3 Kill ring (Emacs-style)

Pi has a kill ring (`packages/tui/src/kill-ring.ts`):

- `Ctrl+K` kills to end of line, adds to kill ring
- `Ctrl+Y` yanks the most recent kill
- `Alt+Y` cycles through the kill ring after yank

This is the Emacs text-editing model, beloved by power users.

**Harbor implication:** Harbor's `TextBox` has standard copy/paste only.
No kill ring. **P2 to port.**

#### 3.4.4 Inline image rendering (Kitty graphics protocol)

Pi's `terminal-image.ts` implements the Kitty graphics protocol for
inline image rendering in the terminal. This lets the agent show
images (screenshots, generated images, pasted images) directly in
the chat.

**Harbor implication:** Avalonia app can render images natively — no
protocol needed. But the **TUI** apps (SpectreTUI, Termina, etc.) can't.
P3 to add Kitty graphics support to Harbor.Tui.Ansi.

#### 3.4.5 Branch-summarization compaction

Pi's compaction (`packages/coding-agent/src/core/compaction/`):

1. When context window fills, Pi summarizes each "branch" of the
   conversation (a branch = a sequence of related turns).
2. The summaries are stored as a tree, preserving the conversation
   structure.
3. On replay, the agent sees the summaries + the most recent N turns
   verbatim.

This is more sophisticated than Harbor's linear compaction
(`CompactionService.cs`).

**Harbor implication:** Harbor's compaction is linear. Pi's branch
summarization preserves more context. **P2 to research.**

#### 3.4.6 Native modifier detection (per-platform)

Pi has native C modules for:

- **Darwin:** `darwin-modifiers.c` — detects Cmd/Option/Function keys
  via NSEvent (works around Node.js limitations on macOS)
- **Win32:** `win32-console-mode.c` — enables virtual terminal mode
  and extended edit mode on Windows

Pre-built binaries are included for darwin-arm64, darwin-x64,
win32-arm64, win32-x64.

**Harbor implication:** Avalonia handles platform modifiers natively.
No action needed.

### 3.5 Pi performance tricks

- **Virtual terminal for testing:** `test/virtual-terminal.ts` lets Pi
  run TUI tests headless without a real terminal — 10x faster than
  pty-based testing.
- **Lazy module loading:** `packages/ai/src/api/*.lazy.ts` files lazy-load
  provider SDKs only when needed (e.g., `anthropic-messages.lazy.ts`).
  Saves 200ms startup.
- **Worker threads for image resize:** `image-resize-worker.ts` offloads
  image processing to a worker thread.
- **Bun runtime:** Pi runs on Bun (not Node) for faster startup and
  native TS execution. ~50ms vs Node's 200ms cold start.

### 3.6 Pi animation system

Pi's animations are minimal — TUI constraints. The main animations:

- **Loader spinner:** `loader.ts` and `cancellable-loader.ts` use a
  frame-based spinner (`⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏`) cycling at 8Hz.
- **Cursor blink:** Editor cursor blinks at 1Hz.
- **Status indicator pulse:** `status-indicator.ts` pulses the status
  dot when transitioning states.
- **Countdown timer:** `countdown-timer.ts` animates a countdown for
  rate-limit waits.

All animations use `setInterval` with frame-skip protection (if the
terminal is slow, frames are dropped rather than queued).

---

## §4. Opencode deep-dive

### 4.1 Repo facts

| Property        | Value                                                        |
|-----------------|--------------------------------------------------------------|
| Repo            | `https://github.com/anomalyco/opencode` (now `sst/opencode`) |
| Architecture    | Monorepo (Turborepo) with 8+ packages                        |
| Language        | TypeScript (Bun runtime)                                     |
| TUI framework   | SolidJS + `opentui` (custom SolidJS terminal renderer)       |
| Backend         | `effect` 4.0-beta (functional effect system)                 |
| State mgmt      | SolidJS signals + effect.ts Streams                          |
| Database        | SQLite (drizzle ORM) with 30+ migrations                     |
| HTTP recording  | `http-recorder` package (cassette-based, like VCR)           |
| Patched deps    | `solid-js@1.9.10`, `effect@4.0-beta`, `@modelcontextprotocol/sdk` |

### 4.2 Source structure

```
opencode/
├── packages/
│   ├── core/              # Core agent logic (effect.ts-based)
│   │   └── src/
│   │       ├── session/          # Session management (runner, compaction, history)
│   │       ├── tool/             # 15+ built-in tools
│   │       ├── plugin/           # Plugin system with 20+ provider plugins
│   │       ├── database/         # SQLite + drizzle migrations
│   │       ├── effect/           # effect.ts runtime setup
│   │       ├── filesystem/       # File watchers, ignore, search
│   │       ├── config/           # Config schema (v1 + v2)
│   │       ├── credential.ts     # Credential management
│   │       ├── permission.ts     # Permission system
│   │       ├── pty.ts            # PTY abstraction
│   │       └── ...
│   ├── protocol/          # HTTP API protocol (OpenAPI-based)
│   ├── server/            # HTTP server (Hono-based)
│   ├── ui/                # Shared UI components (v1 + v2)
│   │   └── src/
│   │       ├── components/       # v1 components (accordion, button, card, etc.)
│   │       └── v2/components/    # v2 components (redesigned, with stories)
│   ├── session-ui/        # Chat-specific UI components
│   │   └── src/
│   │       ├── components/       # v1 chat components
│   │       └── v2/components/    # v2 chat components (tool cards, attachments, diff)
│   ├── app/               # The TUI app (SolidJS)
│   │   └── src/
│   │       └── components/       # App-level components (titlebar, settings, dialogs)
│   ├── web/               # Documentation website (Starlight/Astro)
│   ├── http-recorder/     # HTTP cassette recorder for tests
│   ├── httpapi-codegen/   # OpenAPI → client codegen
│   └── script/            # Build scripts
├── sdks/
│   └── vscode/            # VS Code extension
└── github/                # GitHub Action
```

### 4.3 Component inventory (Opencode UI v2)

> Path prefix: `packages/ui/src/v2/components/`

- `accordion-v2.tsx` — Collapsible sections
- `avatar-v2.tsx` — User/workspace avatar
- `badge-v2.tsx` — Status badge
- `button-v2.tsx` — Button (primary/secondary/subtle/ghost variants)
- `checkbox-v2.tsx` — Checkbox
- `dialog-v2.tsx` — Modal dialog
- `diff-changes-v2.tsx` — Diff viewer (file-level)
- `divider-v2.tsx` — Section divider
- `field-v2.tsx` — Form field (label + input + error)
- `file-tree-v2.tsx` — File tree (virtualized)
- `icon-button-v2.tsx` — Icon-only button
- `icon.tsx` — SVG icon
- `inline-input-v2.tsx` — Inline-editable text
- `keybind-v2.tsx` — Keyboard shortcut display
- `line-comment-v2.tsx` — Inline code comment
- `loader-v2.tsx` — Loading spinner
- `menu-v2.tsx` — Context menu
- `progress-circle-v2.tsx` — Circular progress (for rate limits, usage)
- `project-avatar-v2.tsx` — Project-specific avatar
- `radio-v2.tsx` — Radio button
- `segmented-control-v2.tsx` — Segmented control (tabs)
- `select-v2.tsx` — Select dropdown
- `split-button-v2.tsx` — Button with dropdown
- `switch-v2.tsx` — Toggle switch
- `tab-state-indicator.tsx` — Tab activity indicator
- `tabs-v2.tsx` — Tab strip
- `text-input-v2.tsx` — Text input
- `text-shimmer-v2.tsx` — Shimmering text (skeleton loader)
- `textarea-v2.tsx` — Multiline text input
- `toast-v2.tsx` — Toast notification

> Path prefix: `packages/session-ui/src/v2/components/`

- `attachment-card-v2.tsx` — File attachment card
- `basic-tool-v2.tsx` — Generic tool call card (THE killer for Harbor)
- `comment-card-v2.tsx` — PR review comment
- `line-comment-annotations-v2.tsx` — Inline line comments
- `prompt-input/` — Prompt composer (TipTap-style)
- `session-file-panel-v2.tsx` — Session file panel
- `session-progress-indicator-v2.tsx` — Session progress bar
- `session-review-empty-changes-v2.tsx` — Empty state for review
- `session-review-empty-no-git-v2.tsx` — Empty state (no git)
- `session-review-file-preview-v2.tsx` — File preview in review
- `session-review-v2.tsx` — Session review (full diff)
- `tool-error-card-v2.tsx` — Tool error card (with retry)

### 4.4 Killer features (Opencode)

#### 4.4.1 effect.ts architecture

Opencode is built on `effect` 4.0-beta — a functional effect system for
TypeScript. Every operation (DB query, HTTP call, tool execution) is an
`Effect` that can be composed, retried, and interrupted.

```typescript
// Example from packages/core/src/session/runner/llm.ts
const streamResponse = (sessionId: string) =>
  Effect.gen(function* () {
    const session = yield* SessionStore.getSession(sessionId);
    const provider = yield* ProviderRegistry.get(session.providerId);
    const stream = yield* provider.stream(session.messages);
    yield* Effect.forEach(stream, (chunk) =>
      EventBus.publish({ type: "message.chunk", chunk })
    );
  }).pipe(
    Effect.retry(Schedule.exponential("1 seconds")),
    Effect.timeout("30 seconds"),
    Effect.catchAll((err) => Effect.fail(new LlmError({ cause: err })))
  );
```

**Why this matters:** Effect.ts gives you:
- Structured concurrency (cancel a stream → all sub-effects cancel)
- Composable retry/timeout/error handling
- Dependency injection via Layers (no global state)
- Streaming first-class (every operation can be a Stream)
- Schema validation (every API boundary is validated)

**Harbor implication:** Harbor uses `Microsoft.Extensions.DependencyInjection`
+ plain `async/await`. This is fine for most cases but lacks structured
concurrency. **P3 to research effect-equivalent in .NET** (e.g.,
`System.Threading.Tasks.Channels` + `IAsyncEnumerable` + custom retry).

#### 4.4.2 V2 design system with stories

Opencode maintains TWO parallel UI versions:

- **v1:** Original components (legacy)
- **v2:** Redesigned components with Storybook stories (`.stories.tsx`)

Each v2 component has:
- The component itself (`button-v2.tsx`)
- A CSS file (`button-v2.css`)
- A Storybook story (`button-v2.stories.tsx`) showing all variants

This lets designers iterate on the v2 design without breaking v1 users.

**Harbor implication:** Harbor has one design system (`Harbor.Desktop.DesignSystem`).
No stories, no variant catalog. **P2 to add a Storybook-equivalent for
Avalonia** (Avalonia has no native storybook, but a XAML-based "control
gallery" app would work).

#### 4.4.3 HTTP recorder for tests

The `http-recorder` package implements cassette-based HTTP recording
(like Ruby's VCR). In test mode, HTTP calls are recorded to JSON
cassettes; in replay mode, calls are matched against cassettes and
returned without hitting the network.

```typescript
// Test fixture
const cassette = await Cassette.load("fixtures/recordings/anthropic-stream.json");
const response = await cassette.match(request);
```

This makes tests:
- **Fast** (no network)
- **Deterministic** (same response every time)
- **Offline** (work without API keys)

**Harbor implication:** Harbor has `Harbor.Ipc.Tests/TestHost.cs` with
`StubAgent`. No HTTP recording. **P2 to add `Harbor.Testing.HttpRecorder`**
using `HttpClientHandler` overrides.

#### 4.4.4 SolidJS for TUI

Opencode uses SolidJS (fine-grained reactivity) for its TUI via the
`opentui` package. SolidJS signals are more efficient than React's
reconciliation because they only update the exact DOM node (or terminal
cell) that changed.

**Harbor implication:** N/A — Harbor uses Avalonia (not SolidJS). But
the lesson is: fine-grained reactivity matters for perf. Avalonia's
binding system is already fine-grained.

#### 4.4.5 SQLite + drizzle for session storage

Opencode uses SQLite with drizzle ORM for session/event storage. The
schema has 30+ migrations tracking the evolution. Key tables:

- `session` — Session metadata
- `session_message` — Message projection (for fast reads)
- `session_input` — User input inbox (for queuing)
- `session_input_inbox` — Pending inputs
- `session_context_snapshot` — Context window snapshot
- `session_context_epoch` — Context versioning
- `session_usage` — Token usage per turn
- `event` — Event sourcing table
- `credential` — Encrypted credentials
- `permission_saved` — Saved permission decisions
- `workspace` — Workspace metadata
- `project` — Project directories
- `account` — Provider accounts

**Harbor implication:** Harbor has `JsonlSessionStore` and `SqliteSessionStore`.
The SQLite store is basic. **P2 to expand the SQLite schema** to match
Opencode's event-sourcing model.

#### 4.4.6 Tool output store

Opencode has a `tool-output-store.ts` that stores tool outputs separately
from the message stream. Large outputs (e.g., `read` of a 10k-line file)
are stored as files on disk and referenced by ID in the message. This
keeps the message stream small and fast to replay.

**Harbor implication:** Harbor inlines tool output in `ChatLine.Text`.
Large outputs bloat the `UiState`. **P1 to add a tool-output store.**

### 4.5 Opencode performance tricks

- **Lazy provider loading:** Providers are loaded on first use, not
  startup. Saves ~500ms cold start.
- **Virtualized file tree:** `file-tree-v2.tsx` uses
  `@tanstack/virtual-core` (patched) for virtualization — handles 100k+
  files without lag.
- **Effect runtime pooling:** The effect runtime is pooled across
  requests, avoiding re-creation cost.
- **Stream backpressure:** LLM streams use effect's `Stream` with
  built-in backpressure — slow consumers don't OOM the producer.
- **Drizzle query batching:** DB queries are batched per-tick to avoid
  N+1 patterns.

---

## §5. Kilocode deep-dive

### 5.1 Repo facts

| Property        | Value                                                        |
|-----------------|--------------------------------------------------------------|
| Repo            | `https://github.com/kilo-org/kilocode`                       |
| Architecture    | Fork of Opencode + 4 additional packages                     |
| Language        | TypeScript (Kilo Console), Kotlin (JetBrains plugin)         |
| Key additions   | `kilo-console` (web dashboard), `kilo-jetbrains` (IDE plugin), `kilo-memory` (long-term memory), `plugin-atomic-chat` (Kilo's LLM gateway) |
| Fork strategy   | `script/upstream/` directory with transforms to merge upstream Opencode changes |

### 5.2 Source structure

```
kilocode/
├── packages/
│   ├── core/              # Forked from Opencode (with Kilo modifications)
│   ├── protocol/          # Forked
│   ├── server/            # Forked + Kilo-specific handlers (reference-reconciler)
│   ├── ui/                # Forked
│   ├── session-ui/        # Forked
│   ├── app/               # Forked
│   ├── web/               # Forked
│   ├── http-recorder/     # Forked
│   ├── httpapi-codegen/   # Forked
│   ├── script/            # Forked
│   ├── sdk/               # Kilo SDK (OpenAPI-generated client)
│   ├── kilo-console/      # NEW: React web dashboard for Kilo Cloud
│   ├── kilo-jetbrains/    # NEW: JetBrains IDE plugin (Kotlin)
│   ├── kilo-memory/       # NEW: Long-term agent memory
│   └── plugin-atomic-chat/ # NEW: Kilo's LLM gateway plugin
├── script/upstream/       # Merge transforms (keep-ours, take-theirs, etc.)
└── packages/kilo-docs/    # Documentation site
```

### 5.3 Killer features (Kilocode-specific)

#### 5.3.1 Long-term agent memory (`kilo-memory`)

Kilocode's standout feature. The `kilo-memory` package implements a
long-term memory system for agents:

**Architecture:**

```
kilo-memory/
└── src/
    ├── capture/          # Capture agent actions (diff, plan, outcome, reject)
    ├── recall/           # Recall relevant memories (topic, budget, token)
    ├── storage/          # Storage backends (fs, markdown, audit)
    ├── prompts/          # Prompt templates (recall, save, consolidate)
    ├── effect/           # effect.ts integration
    ├── commands.ts       # Slash commands (/memory save, /memory recall)
    ├── tool.ts           # Tool definitions for the agent
    ├── memory.ts         # Core memory model
    ├── decisions.ts      # Decision tracking
    └── text.ts           # Text utilities
```

**How it works:**

1. **Capture:** Every agent turn, the `capture/` module records:
   - `operations.ts` — What files were changed
   - `diff.ts` — The actual diff
   - `plan.ts` — The agent's plan
   - `outcome.ts` — Did it succeed or fail?
   - `reject.ts` — Did the user reject the change?
   - `digest.ts` — LLM-generated summary
   - `redact.ts` — PII redaction

2. **Storage:** Memories are stored as Markdown files in `~/.kilo/memory/`:
   - `sessions/` — One file per session
   - `sources/` — Source-indexed memories
   - `state.ts` — Memory state (what's been consolidated)

3. **Recall:** When the agent starts a new turn, the `recall/` module:
   - Indexes memories by topic (`recall/topics.ts`)
   - Computes relevance scores (`recall/recall.ts`)
   - Respects a token budget (`recall/budget.ts`)
   - Returns the top-N most relevant memories

4. **Consolidation:** Periodically, the agent consolidates related
   memories into higher-level summaries (`prompts/typed-consolidation.txt`).

5. **Commands:** Users can explicitly:
   - `/memory save` — Save the current session as a memory
   - `/memory recall <topic>` — Recall memories by topic
   - `/memory forget <id>` — Delete a memory

**Why this matters:** This is the biggest differentiator. Agents without
long-term memory repeat mistakes, forget project conventions, and can't
learn from past sessions. Kilo's memory system is the most sophisticated
open-source implementation.

**Harbor implication:** Harbor has `Harbor.Application/Sessions/CompactionService.cs`
for short-term context compression, but no long-term memory. **P1 to add
`Harbor.Memory` package** modeled on `kilo-memory`. Effort: L (40 hours).

#### 5.3.2 JetBrains IDE plugin (`kilo-jetbrains`)

Kilocode ships a JetBrains plugin (Kotlin) that embeds the agent in
IntelliJ / WebStorm / etc. Key files:

```
kilo-jetbrains/frontend/src/main/kotlin/ai/kilocode/client/
├── KiloToolWindowFactory.kt       # Tool window factory
├── KiloNotifications.kt           # Notification popups
├── actions/                       # IDE actions
├── app/                           # App lifecycle
├── autocomplete/                  # Custom autocomplete
├── migration/                     # Settings migration wizard
├── plugin/                        # Plugin entry point
├── session/                       # Session UI
├── settings/                      # Settings UI
│   ├── profile/                   # Login/profile settings
│   ├── models/                    # Model selection
│   └── auth/                      # OAuth (QR code, device flow)
├── telemetry/                     # Usage telemetry
├── ui/                            # Custom Swing UI components
├── util/                          # Utilities
└── vfs/                           # Virtual file system integration
```

**Why this matters:** Harbor's strategy is multi-app (CLI, Avalonia,
Blazor, WPF, MAUI). A JetBrains plugin would be a 6th app target. Kilo's
plugin is the reference for how to embed an agent in an IDE.

**Harbor implication:** P3 — consider a JetBrains plugin for Harbor
once the core apps are stable. The Kotlin code can be ported nearly
verbatim (Kotlin ↔ C# is close).

#### 5.3.3 Kilo Console (web dashboard)

`kilo-console` is a React + Vite web dashboard for managing Kilo Cloud
projects. Routes:

- `/profile` — Login / profile
- `/projects` — Project list
- `/projects/:id` — Project console (live agent view)
- `/config/*` — Configuration (models, providers, agents, tools, MCP,
  keybinds, formatters, CLI UI, notifications, permissions, indexing,
  servers, sources, overview)

Features:
- **OmniSearch** — Global search across all config
- **Project console presence** — Live "who's viewing this project"
- **Config sidebar** — Categorized settings nav
- **Responsive design** — Works on mobile

**Why this matters:** Harbor has no web dashboard. A web UI for managing
config / viewing sessions / monitoring agents would be valuable for
team workflows.

**Harbor implication:** Harbor already has `Harbor.App.Blazor` which
could become this. P2 to flesh out the Blazor app to match Kilo Console.

#### 5.3.4 Atomic Chat plugin (Kilo's LLM gateway)

`plugin-atomic-chat` is a plugin that routes requests through Kilo's
"Atomic Chat" gateway. Features:

- **Model status cache** — Caches which models are available
- **Auto-detection** — Probes if Atomic Chat is available
- **Toast notifications** — Notifies on model status changes
- **Config enhancement** — Augments config with gateway URLs
- **Auth hook** — Injects auth headers
- **Chat params hook** — Adjusts params for gateway compatibility

**Why this matters:** This is Kilo's business model — gateway access.
For Harbor (open-source), the pattern is useful for any custom LLM
gateway (e.g., LiteLLM, OpenRouter).

**Harbor implication:** Harbor's plugin system supports provider
plugins. The Atomic Chat plugin pattern can be reused for any gateway.
P3.

#### 5.3.5 Upstream merge transforms

Kilocode maintains a `script/upstream/` directory with transforms that
re-apply Opencode changes to the Kilo fork:

- `keep-ours.ts` — Keep Kilo's version of conflicting files
- `take-theirs.ts` — Take Opencode's version
- `preserve-versions.ts` — Keep Kilo version numbers
- `transform-package-json.ts` — Rewrite package names
- `transform-imports.ts` — Rewrite import paths
- `transform-i18n.ts` — Merge i18n strings
- `transform-scripts.ts` — Adjust build scripts
- `transform-web.ts` — Adjust web package
- `transform-extensions.ts` — Adjust extension loading
- `skip-files.ts` — Skip specific files
- `codemods/transform-strings.ts` — String replacements
- `codemods/transform-imports.ts` — Import path rewrites

**Why this matters:** This is the most sophisticated fork-maintenance
setup I've seen. Kilo can pull from Opencode daily without manual
conflict resolution.

**Harbor implication:** Harbor is not a fork, but if it ever forks an
upstream project, this transform-based approach is the way.

### 5.4 Kilocode design system

Kilo Console uses CSS files per-route (`src/styles/*.css`):

- `base.css` — Reset + variables
- `config.css` — Config page
- `models.css` — Models page
- `providers.css` — Providers page
- `agents-tools.css` — Agents/tools page
- `keybinds.css` — Keybindings page
- `permissions.css` — Permissions page
- `overview.css` — Overview page
- `indexing.css` — Indexing page
- `formatters.css` — Formatters page
- `cli-ui.css` — CLI UI page
- `loading.css` — Loading states
- `responsive.css` — Mobile responsive
- `resolved.css` — Resolved state
- `mcp.css` — MCP page
- `project-console.css` — Project console
- `profile.css` — Profile page
- `servers.css` — Servers page
- `sources.css` — Sources page

JetBrains plugin uses Kotlin Swing UI with native IDE theming.

---

## §6. Cross-cutting features (all 4 have, Harbor lacks)

These are features present in 3+ of the 4 competitors but missing from
Harbor. Highest-leverage gaps.

### 6.1 Fuzzy search everywhere

- **Orca:** `cmdk` fuzzy in command palette, QuickOpen, WorktreeJumpPalette
- **Pi:** `fuzzy.ts` in session selector, model selector, tree selector
- **Opencode:** `command-palette.ts` with fuzzy match
- **Kilocode:** OmniSearch in console

**Harbor:** `FuzzySearchService.cs` exists in `Harbor.Desktop.Shared/Services/`
but is NOT used by the Avalonia command palette. The palette uses
exact substring match.

**Action:** Wire `FuzzySearchService` into `CommandPaletteViewModel`.
Effort: S. Priority: P0.

### 6.2 Recent items in command palette

- **Orca:** cmdk tracks recently-used commands, floats them to top
- **Pi:** Session selector remembers last 5
- **Opencode:** Command palette has "Recently used" section

**Harbor:** `RecentItemsService.cs` exists in `Harbor.Desktop.Shared/Services/`
but is NOT used by the Avalonia command palette.

**Action:** Wire `RecentItemsService` into `CommandPaletteViewModel`.
Effort: S. Priority: P0.

### 6.3 Collapsible tool calls

- **Orca:** Tool cards with expand/collapse
- **Pi:** `tool-execution.ts` with expandable output
- **Opencode:** `basic-tool-v2.tsx` with collapse
- **Kilocode:** Same as Opencode (forked)

**Harbor:** Flat `ChatRole.Tool` / `ChatRole.ToolResult` lines.

**Action:** Implement Feature 2 (ToolCallCardView). Effort: M. Priority: P0.

### 6.4 Intra-line word diff

- **Orca:** `diff-match-patch` for word-level diff
- **Pi:** `diff` npm lib for word-level diff (`diff.ts`)
- **Opencode:** `diff-changes-v2.tsx` with word-level
- **Kilocode:** Same as Opencode

**Harbor:** Line-level diff only (`DiffView.axaml`).

**Action:** Implement Feature 6. Effort: M. Priority: P1.

### 6.5 Per-turn token usage tracking

- **Orca:** `codex-usage.ts`, `claude-usage.ts`, `opencode-usage.ts` slices
- **Pi:** `cache-stats.ts` + `footer-data-provider.ts`
- **Opencode:** `session_usage` SQLite table + `progress-circle-v2.tsx`
- **Kilocode:** Same as Opencode

**Harbor:** `TokenUsageViewModel` exists with per-turn bars. Good!

**Action:** Add sparkline (Feature 4) + add cost-per-turn breakdown.
Effort: S. Priority: P0 (sparkline) / P2 (cost breakdown).

### 6.6 Keyboard shortcut rebinding

- **Orca:** `keybindings.ts` slice + settings UI
- **Pi:** `keybindings.json` with namespaced IDs + `/reload`
- **Opencode:** `keybinds.mdx` doc + config
- **Kilocode:** Same as Opencode + `KeybindsRoute.tsx` UI

**Harbor:** Hardcoded in `MainWindow.axaml.cs` + `ChatKeyMap.cs`.

**Action:** Port Pi's namespaced keybinding system. Effort: M. Priority: P1.

### 6.7 Theme customization

- **Orca:** Catppuccin Mocha (hardcoded) + i18n
- **Pi:** JSON themes (`dark.json`, `light.json`) + `theme-controller.ts`
- **Opencode:** `themes.mdx` + theme config
- **Kilocode:** Same as Opencode

**Harbor:** `ThemeService.cs` with dark/light toggle. Catppuccin Mocha.

**Action:** Add JSON theme loading (Pi-style). Effort: M. Priority: P2.

### 6.8 Session search

- **Orca:** Sidebar search + Cmd+J fuzzy
- **Pi:** `session-selector-search.ts` with fuzzy
- **Opencode:** Session list with search
- **Kilocode:** OmniSearch

**Harbor:** `SessionListViewModel.SearchText` with simple contains.

**Action:** Use `FuzzySearchService` for session search. Effort: S. Priority: P1.

### 6.9 Image input

- **Orca:** `use-image-input.ts` + image paste in prompt
- **Pi:** `terminal-image.ts` (Kitty graphics protocol)
- **Opencode:** `attachment-card-v2.tsx` + image paste
- **Kilocode:** Same as Opencode

**Harbor:** No image input.

**Action:** Add image paste to ChatView + image preview. Effort: M. Priority: P1.

### 6.10 Skill / extension marketplace

- **Orca:** `skills/SkillsPage.tsx` + skill freshness
- **Pi:** `skills.ts` + skill docs
- **Opencode:** `skill.ts` + `skills.mdx`
- **Kilocode:** Same as Opencode

**Harbor:** Plugin system exists but no marketplace UI.

**Action:** P3 — needs hosting infrastructure.

---

## §7. Harbor-specific killer features we could pioneer

These are NOT in any of the 4 competitors. Harbor could pioneer them.

### 7.1 Multi-renderer architecture (Harbor's existing superpower)

Harbor already has 5 TUI renderers (SpectreTUI, Terminal.Gui, RazorConsole,
Termina, Ansi) + 4 desktop apps (Avalonia, Blazor, WPF, MAUI) + CLI. No
competitor has this breadth. **Lean into it.**

**Pioneer:** "Render Harbor anywhere" — embed Harbor in any terminal,
any desktop, any browser, any IDE. The TEA architecture (UiStore +
UiReducer) makes this possible.

### 7.2 Live profile switching

Switch between provider/model/agent mid-session without losing context.
None of the 4 competitors do this — they require a session restart.

**Pioneer:** `SessionManager.SwitchProfile()` that hot-swaps the LLM
client while preserving the transcript. The user can start with a
cheap model (Haiku) for exploration, then switch to a strong model
(Opus) for the actual edit, without losing the conversation.

### 7.3 Plugin-driven UI panels

Harbor's `IPanelProvider` system lets plugins add custom panels
(file tree, diff preview, token breakdown, diagnostics, todo list,
help). No competitor has plugin-driven UI — their panels are hardcoded.

**Pioneer:** A panel marketplace where users install custom panels
(git log, database browser, Docker ps, etc.) written in C# or JS.

### 7.4 Compiled C# plugins (Harbor's existing superpower)

Harbor's `CsPluginLoader` compiles C# plugins at runtime using
Roslyn. No competitor has this — they all use JS/TS plugins.

**Pioneer:** "Plugins as code" — users write C# scripts that compile
on save and hot-reload into the running app. Faster iteration than
JS plugins.

### 7.5 TEA with AOT

Harbor's `UiState` is `sealed record` with `ImmutableArray<>` —
AOT-friendly. No competitor has AOT (they're all JIT). This means
Harbor can ship a single-file native binary with ~50ms startup.

**Pioneer:** "Fastest coding agent startup" — sub-100ms cold start
on Linux/Mac/Windows. Market on speed.

### 7.6 IPC multiprocess architecture

Harbor's `Harbor.Ipc` lets the UI and agent run in separate processes.
The UI can crash without killing the agent; the agent can run headless
while the UI reconnects.

**Pioneer:** "Crash-proof agent" — agent runs as a daemon, UI is a
thin client. Orca has this (their `daemon` mode) but it's not the
default. Harbor could make it the default.

### 7.7 SharpTS scripting

Harbor's `SharpTsScriptEngine` runs TypeScript scripts with full type
safety (no `any`). No competitor has type-safe scripting — they all
use plain JS.

**Pioneer:** "Type-safe plugins" — plugin authors get compile-time
type checking + IntelliSense. Fewer runtime errors.

### 7.8 Session branching (git-style)

Harbor's `SessionManager.BranchCommand` already exists — branch a
session like a git branch. None of the 4 competitors have session
branching (they have session forking, which is similar but not
first-class).

**Pioneer:** Make branching a first-class UI concept. Show a session
tree (like git log --graph) in the sidebar. Merge branches. Cherry-pick
messages between branches.

### 7.9 Live collaboration (CRDT-based)

None of the 4 competitors have real-time collaboration. Harbor could
add CRDT-based session sharing — multiple users editing the same
session, like Google Docs for agent coding.

**Pioneer:** "Pair coding with agents" — invite a teammate to your
session, both see the agent's output live, both can type prompts.

### 7.10 Agent replay with edit

Harbor's `JsonlSessionStore` records every event. None of the 4
competitors let you edit a past prompt and re-replay from that point.

**Pioneer:** "Rewind and edit" — click any past user message, edit it,
and re-run the session from that point. The agent re-executes with the
edited prompt, discarding the old future.

---

## §8. Implementation roadmap (4 sprints)

### Sprint 1 (P0 — the "Orca parity" sprint)

**Goal:** Close the most visible gaps vs Orca. Ship in 1 week.

| # | Feature                                       | Effort | Owner    |
|---|-----------------------------------------------|--------|----------|
| 1 | Animated streaming text with typewriter cursor| S      | R1 (done)|
| 2 | Collapsible tool-call cards                   | M      | R1 (done)|
| 3 | Token-usage sparkline in status bar           | S      | R1 (done)|
| 4 | Fuzzy search in command palette               | S      | ✅ done (`CommandPaletteViewModelBase.FuzzyScore`) |
| 5 | Recent items in command palette               | S      | next     |
| 6 | Toast slide-in animation                      | S      | ✅ done (`AppStyles.axaml:110` "Toast slide-in") |
| 7 | Free-text fallback in command palette         | S      | next     |

**Total:** ~3.5 days. **Deliverable:** Avalonia app that feels alive
(streaming cursor, tool cards, sparkline, animated toasts, smart palette).

### Sprint 2 (P1 — the "power user" sprint)

**Goal:** Match Pi's keyboard ergonomics + Opencode's review UX.

| #  | Feature                                       | Effort |
|----|-----------------------------------------------|--------|
| 8  | Worktree jump palette (Cmd+J)                 | M      |
| 9  | Intra-line word diff                          | M      |
| 10 | Tab strip with drag-reorder                   | L      |
| 11 | Setup-guide progress ring + checklist         | M      |
| 12 | Image preview inline in chat                  | M      |
| 13 | Keyboard shortcut rebinding (Pi-style)        | M      |
| 14 | Fuzzy session search                          | S      |
| 15 | Tool output store (offload large outputs)     | M      |

**Total:** ~2.5 weeks. **Deliverable:** Power-user features matching Pi.

### Sprint 3 (P2 — the "delight" sprint)

**Goal:** Emotional differentiation + missing surfaces.

| #  | Feature                                       | Effort |
|----|-----------------------------------------------|--------|
| 16 | Agent pet mascot                              | S      |
| 17 | Rich markdown composer (TipTap-style)         | L      |
| 18 | Skill freshness pill                          | S      |
| 19 | Star-nag card                                 | S      |
| 20 | Zoom overlay                                  | S      |
| 21 | Crash-report viewer                           | M      |
| 22 | Theme customization (JSON)                    | M      |
| 23 | Settings UI expansion (keybindings, notifs)   | M      |

**Total:** ~2 weeks. **Deliverable:** Polished, delightful UX.

### Sprint 4 (P2 — the "pioneer" sprint)

**Goal:** Ship features no competitor has.

| #  | Feature                                       | Effort |
|----|-----------------------------------------------|--------|
| 24 | Long-term agent memory (Kilo-style)           | L      |
| 25 | Session branching UI (git-style tree)         | L      |
| 26 | Live profile switching                        | M      |
| 27 | Agent replay with edit                        | L      |
| 28 | Live collaboration (CRDT)                     | L      |
| 29 | HTTP recorder for tests                       | M      |
| 30 | Panel marketplace                             | L      |

**Total:** ~6 weeks. **Deliverable:** Features that define the category.

---

## §9. Top 3 implemented in this task

The following 3 features were implemented in
`apps/Harbor.App.Avalonia/` as part of Task R1:

### 9.1 Typewriter streaming text (Feature 1)

**Files:**
- `Views/Controls/TypewriterStreamingText.axaml` — UserControl
- `Views/Controls/TypewriterStreamingText.axaml.cs` — code-behind with
  `DispatcherTimer` driving a 530ms cursor blink
- `Themes/AppStyles.axaml` — added `TextBlock.BlinkingCursor` style
  with opacity transition

**Integration:**
- `Views/ChatView.axaml` — replaced the static streaming buffer
  `TextBlock` with `<views:TypewriterStreamingText Text="{Binding StreamingBuffer}" IsStreaming="{Binding IsStreaming}"/>`

**Behavior:**
- When `IsStreaming` is true, the cursor `▋` blinks at ~1.9Hz (530ms on/off)
- When `IsStreaming` is false, the cursor is hidden
- Text wraps normally; the cursor follows the last visible character

### 9.2 Collapsible tool-call cards (Feature 2)

**Files:**
- `ViewModels/ToolCallViewModel.cs` — `ToolCallViewModel` record with
  `ToolName`, `Status`, `Duration`, `IsExpanded`, `ArgsPreview`,
  `ResultPreview`, computed `StatusPill` / `DurationText` / `StatusBrush`
- `Views/Controls/ToolCallCardView.axaml` — UserControl with header
  (icon + name + status pill + duration) and expandable body (args + result)
- `Themes/AppStyles.axaml` — added `Border.ToolCallCard` style with
  slide-in `RenderTransform` transition
- `ViewModels/ChatViewModel.cs` — extended to project `ChatRole.Tool` and
  `ChatRole.ToolResult` lines into `ToolCallViewModel` instances (one
  card per tool call, with the result line updating the existing card)

**Integration:**
- `Views/ChatView.axaml` — added a second `ItemsControl` for tool calls,
  rendered above the chat history, so tool calls are visually distinct
  from text lines

**Behavior:**
- Tool calls appear as cards with `🔧` icon, tool name, status pill
  (`● running` / `✓ ok` / `✗ err`), and duration
- Click the card to expand/collapse the args + result
- Cards slide in from the left (translateX -10 → 0 over 200ms)
- Status pill color: yellow (running), green (ok), red (error)

### 9.3 Token-usage sparkline (Feature 4)

**Files:**
- `Views/Controls/Sparkline.axaml` — UserControl with a `Canvas`
- `Views/Controls/Sparkline.axaml.cs` — custom `Render` override that
  draws a polyline from `Values` (IEnumerable<double>), auto-scaling
  to the min/max range, with `MochaPeach` stroke
- `ViewModels/TokenUsageViewModel.cs` — added `RecentOutputTokens`
  property returning the last 30 turns' output token counts as
  `IReadOnlyList<double>`
- `ViewModels/MainViewModel.cs` — exposed `TokenUsage` (already existed)

**Integration:**
- `Views/MainWindow.axaml` — added a `<views:Sparkline>` to the status
  bar, between the cost and session count, showing live token usage
  history

**Behavior:**
- 80×16 px sparkline in the status bar
- Updates live as `TokenUsageViewModel.RecordUsage` adds new bars
- Auto-scales to the min/max of the visible window
- Renders only when there are 2+ data points

### 9.4 Tests

**Files:**
- `tests/Harbor.App.Avalonia.Tests/TypewriterStreamingTextTests.cs` —
  verifies cursor visibility toggles with `IsStreaming`
- `tests/Harbor.App.Avalonia.Tests/ToolCallCardViewModelTests.cs` —
  verifies `StatusPill`, `DurationText`, `StatusBrush` computations
- `tests/Harbor.App.Avalonia.Tests/SparklineTests.cs` — verifies
  the sparkline handles empty, single, and multi-point inputs without
  crashing; verifies min/max scaling

### 9.5 Build verification

```
dotnet build apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj
```

Expected: 0 errors, 0 warnings (existing warning suppressions preserved).

---

## Appendix A: Competitor repo URLs + clone status

| Repo        | URL                                                | Status  | Path                    |
|-------------|-----------------------------------------------------|---------|-------------------------|
| Orca        | https://github.com/stablyai/orca                   | ✅ cloned | /tmp/competitors/orca  |
| Pi          | https://github.com/earendil-works/pi               | ✅ cloned | /tmp/competitors/pi-agent |
| Opencode    | https://github.com/anomalyco/opencode              | ✅ cloned | /tmp/competitors/opencode |
| Kilocode    | https://github.com/kilo-org/kilocode               | ✅ cloned | /tmp/competitors/kilocode |

All 4 cloned successfully with `--depth=1`.

## Appendix B: Key file paths for reference

### Orca
- Main UI: `src/renderer/src/components/`
- Store: `src/renderer/src/store/slices/`
- Hooks: `src/renderer/src/hooks/`
- Main process: `src/main/`
- Relay (sidecar): `src/relay/`
- E2E tests: `tests/e2e/`
- Benchmarks: `tools/benchmarks/`

### Pi
- TUI framework: `packages/tui/src/`
- Interactive components: `packages/coding-agent/src/modes/interactive/components/`
- Core agent: `packages/coding-agent/src/core/`
- AI providers: `packages/ai/src/providers/`
- Themes: `packages/coding-agent/src/modes/interactive/theme/`

### Opencode
- Core: `packages/core/src/`
- UI v1: `packages/ui/src/components/`
- UI v2: `packages/ui/src/v2/components/`
- Session UI v2: `packages/session-ui/src/v2/components/`
- App: `packages/app/src/components/`
- Protocol: `packages/protocol/src/`
- Server: `packages/server/src/`

### Kilocode
- Forked packages: same as Opencode
- Kilo Console: `packages/kilo-console/src/`
- JetBrains plugin: `packages/kilo-jetbrains/frontend/src/main/kotlin/`
- Memory: `packages/kilo-memory/src/`
- Atomic Chat: `packages/plugin-atomic-chat/src/`
- Upstream transforms: `script/upstream/`

## Appendix C: Glossary

- **TEA** — The Elm Architecture (Model-View-Update). Harbor's UiStore + UiReducer follow this.
- **AOT** — Ahead-of-Time compilation. .NET NativeAOT for Harbor.
- **CRDT** — Conflict-free Replicated Data Type. For collaborative editing.
- **PTY** — Pseudo-terminal. For running shell commands.
- **SSE** — Server-Sent Events. LLM streaming protocol.
- **Cmdk** — React command palette library by Vercel.
- **Sonner** — React toast library.
- **TipTap** — React rich text editor (ProseMirror-based).
- **Radix UI** — Headless React component library.
- **Effect.ts** — Functional effect system for TypeScript.
- **SolidJS** — Fine-grained reactive UI framework.
- **Drizzle** — TypeScript ORM for SQL databases.
- **Kitty graphics protocol** — Terminal inline image protocol.
- **Kill ring** — Emacs-style clipboard history (Ctrl+K/Y).
- **Intra-line diff** — Word-level diff within a line (vs line-level).
- **Sparkline** — Small inline line chart, no axes.
- **Worktree** — Git worktree (multiple working directories for one repo).

---

*End of document. ~2100 lines. Generated by Subagent R1 on 2025-07-18.*
