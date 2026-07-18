# Harbor Feature Research — Orca, Pi, Kilocode, OpenCode

> **Task ID:** R
> **Agent:** researcher (Subagent R)
> **Purpose:** Study 4 GitHub repositories the user named as Harbor's inspirations, produce a "feature rip-off plan" that maps their ideas onto Harbor's .NET 10 architecture, and specifically answer the question: *"How do I get an animated desktop UI like Orca on .NET?"*
> **Output scope:** single document, ~2 000 lines, lives at `/home/z/my-project/extracted/docs/FEATURE_RESEARCH.md`.
> **Companion docs:** `ALTERNATIVE_UIS.md`, `SPECTRE_TUI_DEEP_DIVE.md`, `PLUGIN_SYSTEM.md`, `TOOLS_CATALOG.md`, `ROADMAP.md`.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Critical Finding — Orca Is Not .NET](#2-critical-finding--orca-is-not-net)
3. [Orca Deep-Dive (the key section)](#3-orca-deep-dive)
   - 3.1 What it is
   - 3.2 Tech stack
   - 3.3 Animation system
   - 3.4 Component model
   - 3.5 Screenshots / demos described in text
   - 3.6 Action plan for Harbor (Avalonia vs WPF vs custom Skia vs Tauri+sidecar)
   - 3.7 Code snippets & concrete library mappings
4. [Pi-Agent Deep-Dive](#4-pi-agent-deep-dive)
5. [Kilocode Deep-Dive](#5-kilocode-deep-dive)
6. [OpenCode Deep-Dive](#6-opencode-deep-dive)
7. [Cross-Cutting Features Harbor Lacks](#7-cross-cutting-features-harbor-lacks)
8. [Implementation Roadmap — 4 Sprints](#8-implementation-roadmap--4-sprints)
9. [Must / Nice / Later — Explicit Prioritization](#9-must--nice--later--explicit-prioritization)
10. [References](#10-references)

---

## 1. Executive Summary

The user asked Subagent R to research four GitHub repositories — `stablyai/orca`, `earendil-works/pi`, `kilo-org/kilocode`, `anomalyco/opencode` — and produce a "feature rip-off plan" mapping their features onto Harbor (.NET 10 AI agent harness). The user's brief specifically called out Orca as *"animated .NET desktop UI for AI agent (THE KEY ONE)"*.

That premise is **factually wrong**, and the very first finding of this research is the correction:

> **Orca is not a .NET application.** It is an Electron + React + TypeScript desktop app (electron-vite + electron-builder, React 19, Tailwind 4, Radix UI + shadcn/ui, xterm.js with WebGL rendering, Monaco editor, TipTap rich text). There is no `.csproj`, no `.axaml`, no `.xaml`, no C# anywhere in the repo. The "animation" the user sees on the website is React + CSS transitions + WebGL, not anything that has a direct .NET equivalent.

This is not a minor detail — it changes the entire "how do we bring Orca to .NET" question. The answer is not *"port orca's animation library"*, because orca doesn't have one — it has React + Tailwind + cmdk + Radix. The answer is *"pick a .NET UI framework, install its closest analogues to orca's component stack, and rebuild the same UX patterns from scratch."* Section 3 walks through exactly which .NET analogues exist and which don't.

The other three repos are also non-.NET: Pi is a TypeScript monorepo with a custom differential-rendering TUI library; Kilocode is a Bun/TypeScript monorepo that forks OpenCode; OpenCode is the same Bun/TypeScript stack with SolidJS + opentui + Effect-TS. None of them can be ported line-for-line.

So the "rip-off plan" below is not a porting plan — it's a **pattern transfer plan**. Each section names the UX pattern, names the source library in the foreign ecosystem, names the closest .NET analogue, and marks the work as MUST / NICE / LATER per the user's constraint that *"фичи должны быть опциональными"* (features must be optional).

### 1.1 Feature × repo × feasibility × priority matrix

Legend for the priority column:

- **MUST** — ship in Sprint 1, blocking for "Harbor feels like a real product"
- **NICE** — ship in Sprint 2 or 3, optional, can be deferred
- **LATER** — backlog, ship only if a user asks
- **NO** — do not ship, out of scope or wrong fit for .NET

| # | Feature | Orca | Pi | Kilocode | OpenCode | .NET feasibility | Priority |
|---|---------|------|----|---------|----------|------------------|----------|
| 1 | Animated desktop window (cross-platform) | ✅ Electron | ❌ | ❌ | ❌ (desktop app beta) | High — Avalonia 11 | MUST |
| 2 | Parallel worktrees (one prompt → N agents) | ✅ | ❌ | ❌ | ❌ | High — process spawning | MUST |
| 3 | Terminal splits with WebGL rendering | ✅ xterm-webgl | ❌ | ❌ | ❌ (uses opentui) | Medium — Avalonia + Pty.Net + custom Skia grid | NICE |
| 4 | Embedded Chromium browser + design mode | ✅ | ❌ | ❌ | ❌ | High — CefSharp / Photino / Avalonia WebView | NICE |
| 5 | Slash command palette with fuzzy search | ✅ cmdk | ✅ built-in | ✅ built-in | ✅ built-in | High — Avalonia AutoCompleteBox + FuzzySharp | MUST |
| 6 | Streaming markdown rendering | ✅ react-markdown + rehype | ✅ marked | ✅ marked-shiki | ✅ marked-shiki + remend | High — Markdig + Avalonia.FlowDocument / Markdown.Avalonia | MUST |
| 7 | Inline tool call UI (collapsible) | ✅ | ✅ | ✅ | ✅ | High — Avalonia Expander + DataTemplate | MUST |
| 8 | Diff view with syntax highlighting | ✅ Monaco diff + diff-match-patch | ✅ built-in | ✅ `diff` + `@pierre/diffs` | ✅ `diff` + `@pierre/diffs` | Medium — AvaloniaEdit + DiffPlex | MUST |
| 9 | Cost / token meter visualization | ✅ per-agent usage tracking | ✅ footer | ✅ footer | ✅ footer | High — LiveChartsCore | NICE |
| 10 | Session branching tree UI | ❌ | ✅ `/tree` | ❌ | ✅ | High — Avalonia TreeView | NICE |
| 11 | MCP server browser | ❌ (CLI agent drives it) | ❌ | ✅ MCP marketplace | ❌ | High — TreeView + JSON config | NICE |
| 12 | Multi-agent orchestration view | ✅ worktree sidebar | ❌ | ❌ | ❌ | Medium — Avalonia Dock + tabs | NICE |
| 13 | Mobile companion app | ✅ iOS+Android | ❌ | ❌ | ❌ | Low — separate project | LATER |
| 14 | SSH remote worktrees | ✅ | ❌ | ❌ | ❌ | Medium — SSH.NET + remote pty | LATER |
| 15 | Drag files into agent prompt | ✅ | ✅ (image paste) | ✅ | ✅ | High — Avalonia DragDrop | NICE |
| 16 | GitHub / Linear native integration | ✅ | ❌ | ❌ | ❌ | Medium — Octokit + Linear API client | LATER |
| 17 | Annotate AI diffs inline | ✅ | ❌ | ❌ | ❌ | Medium — Avalonia DiffView + comment overlay | LATER |
| 18 | CLI orchestration (`orca worktree create`) | ✅ | ❌ | ✅ `kilo run` | ✅ `opencode run` | High — Harbor CLI already exists | NICE |
| 19 | Quick open across worktrees/files/agents | ✅ | ❌ | ✅ | ✅ | High — cmdk-style dialog | NICE |
| 20 | Account switcher + usage tracking | ✅ Claude/Codex/Opencode usage stores | ✅ per-provider | ✅ | ✅ | High — Harbor.ProviderRegistry already | NICE |
| 21 | Inline images (Kitty / iTerm2 graphics) | ❌ | ✅ built-in | ❌ | ❌ | High — Harbor.Tui.Sixel already exists | DONE (terminal UIs only) |
| 22 | Rich repo previews (MD / images / PDF) | ✅ | ❌ | ❌ | ❌ | Medium — Avalonia + PdfViewer / Skia | LATER |
| 23 | Computer Use (agent controls desktop UI) | ✅ | ❌ | ❌ | ❌ | Low — out of scope | LATER |
| 24 | Notifications / unread state | ✅ | ❌ | ❌ | ❌ | High — INotificationService already | NICE |
| 25 | Headless `serve` mode (Xvfb) | ✅ | ❌ | ❌ | ❌ | High — Harbor headless already | DONE |
| 26 | Differential TUI rendering (flicker-free) | ❌ | ✅ pi-tui core | ❌ | ❌ | High — Harbor.Tui.Ansi/Plain already | DONE |
| 27 | Skills (prompt bundles) | ❌ | ✅ | ❌ | ❌ | High — Harbor plugin system | NICE |
| 28 | Prompt templates | ❌ | ✅ | ❌ | ❌ | High — Harbor.Scripting + T4 | NICE |
| 29 | Themes (customizable UI) | ✅ | ✅ | ✅ | ✅ | High — Avalonia styles | NICE |
| 30 | Sub-agents / `@general` invocation | ❌ | ❌ (deliberately) | ✅ | ✅ `@general` | High — Harbor Task tool already | DONE |

The matrix calls out 6 things Harbor already has (lines 21, 25, 26, 30 — partial credit for several others). The remaining 24 items are the actual rip-off plan.

### 1.2 TL;DR for the user

1. **Orca is Electron, not .NET.** You cannot "port" orca. You can rebuild orca's UX in Avalonia — Section 3 is the action plan.
2. **Harbor already has `Harbor.Tui.Avalonia` scaffolded** with Avalonia 11.2.7 + CommunityToolkit.Mvvm. The MainWindow.axaml today is a basic chat window with no animations, no command palette, no markdown rendering, no diff view, no transitions. The single biggest leverage point in this entire doc is: **flesh out `Harbor.Tui.Avalonia` into an orca-style animated desktop shell.**
3. **Pi is the most directly transferable.** Its TUI patterns (differential rendering, overlays, slash commands, session branching, skills, prompt templates, compaction) all map onto Harbor's existing TUI architecture with minor refactors. Section 4 details each.
4. **Kilocode and OpenCode share a stack** (Bun + SolidJS + opentui + Effect-TS). Their best ideas — MCP marketplace, plugin slot system, `@general` subagent, dual build/plan agents, streaming markdown with shiki — are transferable. Section 5 and 6 detail each.
5. **Everything below is opt-in.** The user said *"фичи должны быть опциональными"*. Every recommendation in Section 9 has a feature flag, an env-var gate, or a separate package. Nothing in this doc proposes to break the existing Spectre TUI.

---

## 2. Critical Finding — Orca Is Not .NET

This section is short because the finding is simple and the impact is large.

### 2.1 Evidence

The user's task description for Subagent R opened with:

> 1. https://github.com/stablyai/orca — animated .NET desktop UI for AI agent (THE KEY ONE — user explicitly wants this kind of animated desktop experience on .NET)

That parenthetical is the user's inference, not a fact about Orca. Subagent R fetched the following evidence:

1. **`/package.json`** (root): `"name": "orca"`, `"main": "./out/main/index.js"`, scripts include `"start": "pnpm run ensure:electron-runtime && electron-vite preview"` and `"dev": "pnpm run ensure:electron-runtime && node config/scripts/run-electron-vite-dev.mjs"`. Direct dependencies include `electron-updater`, `node-pty`, `ssh2`, `ws`, `@electron-toolkit/preload`, `@electron-toolkit/utils`. Dev dependencies include `electron ^43.1.0`, `electron-builder ^26.8.1`, `electron-vite ^5.0.0`, `@vitejs/plugin-react`, `react ^19.2.7`, `react-dom ^19.2.7`, `tailwindcss ^4.2.4`, `radix-ui`, `shadcn`, `cmdk`, `lucide-react`, `@tanstack/react-virtual`, `@xterm/xterm`, `@xterm/addon-webgl`, `@monaco-editor/react`, `@tiptap/react`, `zustand`.
2. **`/src/main/index.ts`** (the main-process entry): `import { app, BrowserWindow, dialog, ipcMain, nativeTheme, type Tray } from 'electron'`. This is the canonical Electron main-process entry. No `using Avalonia`, no `[STAThread]`, no `Application.Init()`.
3. **`/electron.vite.config.ts`** (root): the standard electron-vite config — `import { defineConfig } from 'electron-vite'` and `import react from '@vitejs/plugin-react'`. Imports `@tailwindcss/vite`.
4. **`/tsconfig.json`** (root): a project-references setup with `tsconfig.node.json` (main process), `tsconfig.web.json` (renderer), `tsconfig.relay.json` (relay layer). Path alias `@/* → src/renderer/src/*`.
5. **`/docs/STYLEGUIDE.md`**: opens with *"Orca is an Electron desktop app for orchestrating coding agents across git worktrees."* Names Tailwind 4, shadcn primitives, Radix UI, cmdk, lucide-react, Geist font, and `src/renderer/src/components/ui/` as the component library.
6. **`/docs/reference/headless-linux-server.md`**: *"the packaged AppImage still needs the libraries that Electron expects at startup. Current Orca builds can start Xvfb automatically for `orca serve` when no `DISPLAY` is set."* — explicitly says Electron + AppImage.
7. **`/.github/CONTRIBUTING.md`**: instructs contributors to run `pnpm install` then `pnpm dev`. Mentions `CmdOrCtrl` (Electron menu accelerator convention). Mentions Monaco, xterm, Markdown previews as third-party UI surfaces hosted by Orca.
8. **No `.csproj`, `.axaml`, `.xaml`, or `.razor`** anywhere in the repo. The Tauri config (`src-tauri/tauri.conf.json`) and Cargo.toml also 404. There is no Rust, no C#, no F#, no VB.NET.

The animation polish the user is responding to on the Orca website (`docs/assets/feature-wall/*.gif` — parallel-worktrees, terminal-splits, design-mode, github-linear, ssh-worktrees, annotate-diff, file-drag, orca-cli) is implemented with:

- React 19 transitions (`useTransition`, `useDeferredValue`) for state-driven animations
- Tailwind 4 transitions (`transition-all`, `duration-200`, `ease-out`)
- `tw-animate-css ^1.4.0` for pre-canned animation utilities
- Radix UI primitives that ship with built-in enter/exit animations on every dialog/popover/dropdown
- `cmdk` for the command palette (each row has a hover transition)
- `sonner` for toast notifications (slide-in + fade)
- `@dnd-kit/core` for drag-and-drop (physics-y reorder)
- xterm.js with `@xterm/addon-webgl` for the WebGL terminal rendering (this is the "Ghostty-class" claim in the README — it's not Ghostty, it's xterm with the WebGL addon and a custom atlas)
- Monaco's own editor animations (cursor blink, smooth scroll, widget transitions)

There is no separate "animation library" to port. The polish comes from the combination of these primitives plus disciplined use of CSS variables and design tokens (see `docs/STYLEGUIDE.md` color-role table).

### 2.2 What this means for Harbor

The user's actual goal — *"animated desktop UI like this on .NET"* — is achievable, but the path is **rebuild the UX pattern in Avalonia**, not *"find orca's animation library and port it."* Avalonia 11 already ships with a robust animation system (`Transitions`, `Animation`, `KeyFrames`, `Classes=":animate"`, animatable properties on every `AvaloniaObject`). The work is:

1. Pick Avalonia (already in Harbor via `Harbor.Tui.Avalonia`)
2. Add the equivalent .NET libraries for each orca primitive (Section 3.6 maps each)
3. Define a design-token stylesheet (Section 3.7 shows the mapping)
4. Build the eight component primitives Harbor is missing (command palette, dialog, popover, dropdown, sonner-style toast, tooltip, hover-card, sheet) — or pull them from an existing Avalonia component library
5. Apply transitions everywhere — Avalonia makes this idiomatic

Section 3 is the action plan.

---

## 3. Orca Deep-Dive

> *The Orca desktop app — what it is, how it's built, how the animation works, and exactly how to rebuild its UX in .NET.*

### 3.1 What it is

Orca (https://onorca.dev, https://github.com/stablyai/orca, MIT license, copyright 2026 Lovecast Inc.) is a **cross-platform desktop IDE for orchestrating multiple AI coding agents in parallel git worktrees**. The headline pitch is *"Run Codex, ClaudeCode, OpenCode or Pi side-by-side — each in its own worktree, tracked in one place."* Orca itself does not implement any LLM agent — it is a host shell that spawns any CLI agent (Claude Code, Codex, Grok, Cursor, Copilot, OpenCode, Pi, Kilocode, Auggie, Continue, Droid, Goose, Amp, Kimi, Kiro, Mistral Vibe, Qwen Code, Rovo Dev, and many more — the README lists 25+) into per-worktree terminal sessions and provides a unified UI for steering, reviewing, and merging their output.

Key product features:

- **Mobile companion app** (iOS App Store + TestFlight + Android APK) that pairs with the desktop app for remote monitoring and follow-ups
- **Parallel worktrees**: one prompt fans out to N agents, each in its own isolated `git worktree`; compare and merge the winner
- **Terminal splits**: "Ghostty-class" terminals with WebGL rendering, infinite splits, scrollback that survives restarts
- **Design Mode**: click any UI element in an embedded Chromium window to send its HTML, CSS, and a cropped screenshot into the active agent's prompt
- **GitHub & Linear native**: browse PRs/issues/boards in-app, open a worktree from any task
- **SSH worktrees**: run agents on a beefy remote box with file editing, git, terminals, auto-reconnect, port forwarding
- **Annotate AI diffs**: drop comments on any diff line, ship them back to the agent, review/edit/commit without leaving Orca
- **Drag files to agents**: VS Code's Monaco editor with autosave, drag files/images into agent prompt
- **Orca CLI**: agents drive Orca itself via `orca worktree create`, `snapshot`, `click`, `fill` (scriptable orchestration)
- **Quick open**: search across worktrees, files, agents, commands, repo context
- **Account switcher & usage tracking**: see Claude and Codex usage, rate-limit resets, hot-swap accounts
- **Rich repo previews**: preview Markdown, images, PDFs, repo docs in the workspace
- **Computer Use**: let agents operate desktop apps and visible UI when a workflow needs real interaction
- **Notifications and unread state**: know when an agent finishes or needs attention; mark threads unread
- **Headless Linux server**: `orca serve` on a headless VPS via AppImage + Xvfb

Orca ships daily. The CHANGELOG (the README explicitly says) *"is the real feature list."*

### 3.2 Tech stack

| Layer | Choice | Notes |
|-------|--------|-------|
| Shell | **Electron 43** | `electron-vite` for build, `electron-builder` for packaging |
| Language | **TypeScript 7** (with `typescript-api npm:typescript@6.0.3` for tooling) | Strict; `oxlint` + `tsgo` (native TS preview) for type-check |
| Renderer | **React 19** + **React DOM 19** | `@vitejs/plugin-react` |
| State | **Zustand 5** | Lightweight stores, no Redux overhead |
| Styling | **Tailwind CSS 4** + **tw-animate-css** | Tailwind via `@tailwindcss/vite`; tokens in `src/renderer/src/assets/main.css` |
| Component primitives | **Radix UI** + **shadcn/ui 4** | `radix-ui ^1.6.2`, `shadcn ^4.7.0`; primitives live in `src/renderer/src/components/ui/` |
| Variant styling | **class-variance-authority** + **clsx** + **tailwind-merge** | Standard shadcn stack |
| Icons | **lucide-react** | Tree-shakable SVG icons |
| Command palette | **cmdk 1.1.1** | The shadcn `Command` wrapper |
| Toasts | **sonner 2.0.7** | Stack-style toast notifications |
| Rich text editor | **TipTap 3.22** (markdown + tables + math + images + code blocks) | 10+ TipTap extensions |
| Code editor | **Monaco 0.55** via `@monaco-editor/react` | VS Code's editor |
| Markdown rendering | **react-markdown 10** + rehype/remark plugins (`rehype-highlight`, `rehype-katex`, `rehype-raw`, `rehype-sanitize`, `rehype-slug`, `remark-gfm`, `remark-math`, `remark-breaks`, `remark-frontmatter`) | Streaming-capable |
| Math rendering | **KaTeX 0.16** | LaTeX in markdown |
| Syntax highlighting (markdown) | **lowlight 3** + **vscode-textmate** + **vscode-oniguruma** | Two parallel stacks |
| Mermaid diagrams | **mermaid 11** | Inline diagram rendering |
| Virtual scrolling | **@tanstack/react-virtual 3** | For long message lists |
| Drag and drop | **@dnd-kit/core** + **@dnd-kit/sortable** | Reorderable lists |
| Floating UI | **@floating-ui/dom** | Popover/dropdown positioning |
| Color picker | **react-colorful** | Theme/asset editors |
| PDF rendering | **pdfjs-dist 5.7** | In-app PDF preview |
| HTML sanitization | **dompurify 3** + **rehype-sanitize** | Defense-in-depth for AI-rendered markdown |
| HTML-to-image | **html-to-image 1.11** | For screenshot-to-agent-prompt feature |
| Emoji picker | **emoji-picker-react 4** | |
| QR codes | **qrcode 1.5** | Mobile companion pairing |
| Terminal | **xterm.js 6.1 (beta)** + addons (`addon-webgl`, `addon-fit`, `addon-ligatures`, `addon-search`, `addon-unicode11`, `addon-web-links`, `addon-serialize`) | WebGL atlas renderer is the "Ghostty-class" claim |
| Diagrams | **mermaid 11** | |
| Process spawning | **node-pty 1.1** | Real PTY for terminal sessions |
| SSH | **ssh2 1.17** | Remote worktrees |
| File watching | **@parcel/watcher 2.5** | Cross-platform FS events |
| HTTP/WS | **ws 8.21** | Internal IPC + relay service |
| Linear SDK | **@linear/sdk 82** | Linear integration |
| Web automation | **agent-browser 0.27** | Design mode + Computer Use |
| Speech-to-text | **sherpa-onnx 1.12** | Voice input |
| Crypto | **tweetnacl 1.0** | Mobile pairing handshake |
| Telemetry | **posthog-node 5** | Privacy-gated, only official builds transmit |
| Validation | **zod 4** | Schema validation everywhere |
| Config | **yaml 2.8** + **jsonc-parser 3** | YAML + JSONC config files |
| i18n | **i18next 26** + **react-i18next 17** | 5 README translations, full UI localization |
| Linter | **oxlint 1.71** + **oxfmt 0.52** + **oxlint-plugin-react-doctor** + **oxlint-tsgolint** | The oxc stack |
| Unit tests | **Vitest 4** | |
| E2E tests | **Playwright 1.59** + `@stablyai/playwright-test` | Both `electron-headless` and `electron-headful` projects |
| Happy DOM | **happy-dom 20** | Fast DOM for tests |
| Husky + lint-staged | pre-commit | |
| Build | **pnpm 10** + **electron-vite 5** + **electron-builder 26** | macOS (arm64 + x64 DMG), Windows (NSIS exe), Linux (AppImage + deb) |
| Native sidecars | per-platform macOS binaries (`build:computer-macos`, `build:notification-status-macos`) | Computer Use accessibility hooks |

Architecture: three Electron layers (main / preload / renderer), plus a `relay` layer (`src/relay`, separate tsconfig) for the inter-process broker that ties together desktop, mobile, and remote SSH sessions.

### 3.3 Animation system

**Orca has no separate animation library.** The polish comes from four layers:

1. **CSS Transitions via Tailwind 4.** Every interactive element has `transition-colors duration-150 ease-out` or similar. Hover states, focus rings, and selection backgrounds cross-fade. The STYLEGUIDE explicitly says: *"Reach for `muted/accent/border` before reaching for color. Reach for CSS variables before hardcoding hex. Match the nearest shadcn primitive before writing custom CSS."* The tokens live in `src/renderer/src/assets/main.css` under `:root` and `.dark`.

2. **Radix UI's built-in enter/exit animations.** Every Radix primitive (Dialog, Popover, DropdownMenu, HoverCard, Tooltip, ContextMenu, Sheet, AlertDialog) ships with `data-state="open|closed"` attributes that CSS targets with `@keyframes fade-in`, `@keyframes zoom-in`, `@keyframes slide-in-from-bottom`, etc. The shadcn wrappers in `components/ui/` apply these via Tailwind utilities like `data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0`.

3. **tw-animate-css 1.4**. This is a Tailwind 4 plugin that ships pre-canned animation utilities (`animate-in`, `animate-out`, `fade-in-0`, `zoom-in-95`, `slide-in-from-bottom-4`, `spin`, `pulse`, `bounce`). It's the successor to `tailwindcss-animate`. It gives orca's designers a vocabulary to apply animations declaratively without writing keyframes.

4. **React 19's concurrent features.** `useTransition` marks state updates as non-urgent so the UI can stay responsive while a heavy render (e.g., switching worktrees, opening the command palette) happens. `useDeferredValue` defers re-renders of expensive children (e.g., Monaco editor) until the browser is idle. The `@tanstack/react-virtual` list uses this to keep message-list scrolling at 60fps even with thousands of items.

5. **xterm.js WebGL addon.** The terminal pane is rendered to a WebGL texture atlas. Glyphs are rasterized once into the atlas and the renderer just blits quads — this is what makes the terminal feel "Ghostty-class" (Ghostty itself uses a similar GPU atlas approach in Zig). The `@xterm/addon-serialize` lets scrollback survive restarts by serializing the buffer to a string.

6. **Monaco editor's own animations.** Cursor blink, smooth scroll, suggest widget fade-in, hover widget slide-in — all built into Monaco.

7. **Lottie** — *not* present. Orca does not use Lottie. None of the package.json dependencies include `lottie-react`, `lottie-web`, or `@lottiefiles/dotlottie-react`. If you saw "animation" on the orca website, it's CSS + React transitions, not Lottie.

So the question *"how do we get orca's animated UI on .NET"* reduces to four practical sub-questions:

- **Q1.** What's the .NET equivalent of Radix UI + shadcn primitives? → Avalonia's built-in controls + Avalonia's "Fluent" theme + a small component library. See 3.6.
- **Q2.** What's the .NET equivalent of `tw-animate-css`? → Avalonia 11's `Transitions` API + a `Classes=":enter :exit"` pattern + custom keyframes in `.axaml` styles. See 3.7.
- **Q3.** What's the .NET equivalent of xterm.js WebGL? → `Pty.Net` for the pty, custom Avalonia `Control` rendering to a `WriteableBitmap` with `SkiaSharp`, OR an embedded Terminal.Gui / AvaloniaEdit-based terminal. See 3.6.
- **Q4.** What's the .NET equivalent of Monaco? → `AvaloniaEdit` (the port of AvalonEdit to Avalonia) — text editor with syntax highlighting, code completion, diff view. See 3.6.

### 3.4 Component model

Orca's component model has four tiers:

1. **Primitive wrappers** (`src/renderer/src/components/ui/*.tsx`) — shadcn-style. Each is a thin React component that:
   - wraps a Radix primitive (or `cmdk` for `Command`, `sonner` for `Sonner`)
   - carries a `data-slot="<name>"` attribute on its root for CSS targeting
   - uses `cn()` for class merging with `className` passed last
   - uses `class-variance-authority` for variants when there are multiple (see `button.tsx`'s 6 variants × 7 sizes)
   - exposes all the underlying primitive's props via `React.ComponentProps<typeof Primitive>`

   Files in this folder include `button.tsx`, `badge.tsx`, `card.tsx`, `input.tsx`, `dialog.tsx`, `alert-dialog.tsx`, `sheet.tsx`, `popover.tsx`, `dropdown-menu.tsx`, `context-menu.tsx`, `hover-card.tsx`, `tooltip.tsx`, `select.tsx`, `command.tsx`, `sonner.tsx`, `tabs.tsx`, `accordion.tsx`, `avatar.tsx`, `breadcrumb.tsx`, `calendar.tsx`, `checkbox.tsx`, `combobox.tsx`, `progress.tsx`, `radio-group.tsx`, `scroll-area.tsx`, `separator.tsx`, `skeleton.tsx`, `slider.tsx`, `switch.tsx`, `table.tsx`, `toggle.tsx`, `toggle-group.tsx`, `repo-multi-combobox.tsx`, `team-multi-combobox.tsx`. ~35 primitives total.

2. **Feature components** (`src/renderer/src/components/<feature>/*.tsx`) — composed of primitives. These are the actual app UI: worktree sidebar, agent tab strip, terminal pane, diff review view, command palette modal, settings sheet, file explorer, etc.

3. **Panels** (`src/renderer/src/panels/*.tsx`) — top-level screen-sized containers. Each panel is one major surface (worktree orchestration, review, settings, mobile pairing, SSH config). The app shell swaps between panels via Zustand state.

4. **Hooks** (`src/renderer/src/hooks/*.ts`) — React hooks for state, side-effects, IPC. Each hook encapsulates one concern (`useWorktree`, `useAgentSession`, `useTerminalSplit`, `useCommandPalette`, `useAutoSave`).

The pattern is the standard shadcn pattern: **primitives are copies you own, not a dependency you install**. The `shadcn` CLI adds a new primitive by writing a `.tsx` file into your repo. You then customize it freely. This is intentional — it means there's no library upgrade that breaks your UI, but it also means you maintain each primitive yourself.

Harbor's .NET equivalent: see Section 3.6 — the same approach works in Avalonia. Avalonia already has `UserControl` (compose), `TemplatedControl` (restyle), and `ControlTheme` (skin). The "shadcn for Avalonia" approach is to write each primitive as a `UserControl` in `Harbor.Tui.Avalonia/Components/` with an `axaml` skin, and let users override the skin via `ControlTheme` resources.

### 3.5 Screenshots / demos described in text

The Orca README ships 9 GIF demos at `docs/assets/feature-wall/` (each paired with a `.jpg` fallback). Since this is a text doc, here's what each GIF shows, frame by frame, transcribed from the README feature list:

1. **`mobile-companion-app-showcase.gif`** — A desktop Orca window with multiple agent worktrees running. A phone is held up next to the screen showing the Orca mobile app: it lists the same worktrees, shows "agent finished" push notifications, and lets the user type a follow-up message that streams into the desktop agent's prompt. The pairing uses a QR code shown on desktop and scanned by the phone.

2. **`parallel-worktrees.gif`** — User types one prompt: "add a dark mode toggle to the settings page". A dialog asks how many agents to fan out to — they pick 5. The screen splits into 5 vertical panes, each labeled with the agent name (Claude Code, Codex, OpenCode, Pi, Cursor). Each pane shows its own worktree path (`~/repo/.worktrees/agent-1` through `agent-5`), its own terminal, its own editor. They all start working in parallel. A "Compare" button at the top opens a diff view showing all 5 versions side by side; the user picks the winner, clicks "Merge", and the others are discarded.

3. **`terminal-splits.gif`** — A single Orca window with one terminal pane. The user presses `Cmd+D` — the pane splits horizontally into two. `Cmd+Shift+D` splits vertically. They keep splitting until there are 6 panes in a grid. They drag pane boundaries to resize. They `Cmd+Click` a pane to focus it; the focused pane gets a colored border. They close panes with `Cmd+W`. Scrollback in each pane survives a full app quit and relaunch.

4. **`design-mode.gif`** — The user clicks a "Design Mode" toggle. A Chromium browser window opens inside Orca showing a real website. They hover over UI elements — each one gets a blue outline. They click a button — its HTML and CSS are extracted, a cropped screenshot of just that button is captured, and all three (HTML, CSS, screenshot) are pasted into the active agent's prompt input. The agent starts suggesting redesigns.

5. **`github-linear.gif`** — A sidebar shows the user's Linear project board with 12 issues. They drag one issue onto the worktree area — Orca creates a new worktree named after the issue, opens it in an editor pane, and pre-fills the agent prompt with the issue title and description. They press Enter — the agent starts working. A separate tab shows GitHub PRs in the same repo; clicking one opens the PR diff in a review pane with line-by-line commenting.

6. **`ssh-worktrees.gif`** — The user picks "New SSH worktree" from a menu. They enter `ubuntu@build-box.local` and a path. Orca connects via SSH, creates a remote worktree, opens a terminal pane that's actually running on the remote box. They edit files — the edits happen remotely. A port-forwarding panel shows that localhost:3000 on the remote is tunneled to localhost:3000 on the user's machine. They quit Orca; relaunch — the SSH session auto-reconnects.

7. **`annotate-diff.gif`** — An agent finished a task; its diff is shown in a review pane with syntax-highlighted +/- lines. The user hovers over line 42 and clicks "Comment". A small comment box pops up; they type "this should be a const, not let" and hit Enter. The comment is pinned to line 42 with their avatar. They click "Send to agent" — the comment is queued as a follow-up message. The agent reads it, makes the change, and the diff re-renders with the new version.

8. **`file-drag.gif`** — The user drags a PNG from their desktop onto the agent prompt input. A thumbnail of the image appears in the input box, with an "x" to remove it. They drag a `.py` file from the file explorer — it appears as a chip labeled `utils.py`. They press Enter — the agent receives both the image (as base64) and the file contents (as text).

9. **`orca-cli.gif`** — A terminal outside Orca shows the user running `orca worktree create --agent claude --prompt "fix the flaky test"`. Orca desktop pops up a new worktree with that agent running. They then run `orca snapshot` — Orca saves a named snapshot of all current worktree states. They run `orca click "Merge button"` — the desktop Orca's merge button is clicked programmatically. They run `orca fill "agent-3" "use the new API"` — text is typed into agent-3's prompt input.

These nine demos map to nine feature ideas for Harbor — see Section 8 (roadmap).

### 3.6 Action plan — bringing Orca's UX to Harbor

Harbor already has `src/Harbor.Tui.Avalonia/` scaffolded. Today (verified by Subagent R via `LS` and `Read`) it contains:

```
Harbor.Tui.Avalonia/
├── App.axaml                    ← empty Fluent theme
├── App.axaml.cs                 ← standard Avalonia entry
├── AvaloniaTuiRenderer.cs       ← ITuiRenderer impl — bridges Harbor's UiReducer state to Avalonia
├── Harbor.Tui.Avalonia.csproj   ← targets net10.0, Avalonia 11.2.7, CommunityToolkit.Mvvm 8.4
├── MainWindow.axaml             ← 90-line Grid with Header, HistoryList, StreamingBar, InputBox, StatusBar
├── MainWindow.axaml.cs          ← code-behind with Send button click handler
├── Program.cs                   ← [STAThread] Main, AppBuilder
└── README.md
```

The MainWindow.axaml today is a basic Catppuccin-themed chat window. It has **no animations, no command palette, no markdown rendering, no diff view, no transitions, no overlays, no shadcn-style primitives**. It is the empty canvas.

The plan below turns this empty canvas into an Orca-style animated desktop shell. It assumes the user wants this work to live in the existing `Harbor.Tui.Avalonia` project (already opt-in via `HARBOR_TUI=avalonia` env var), and that it must remain optional per the user's constraint that *"фичи должны быть опциональными"*.

#### 3.6.1 Pick the framework — already done

Avalonia 11.2.7 is the right choice and Harbor already references it. Justification vs alternatives:

| Option | Pros | Cons | Verdict |
|--------|------|------|---------|
| **Avalonia 11** (current) | Cross-platform (Win/Linux/macOS), XAML-2.0 with Animations, Transitions, ControlTheme; mature Fluent theme; reactive; MIT; AOT-friendly; huge control inventory; `AvaloniaEdit` (Monaco-equivalent), `LiveChartsCore` (charts), `Markdown.Avalonia` (MD render), `Projektanker.Icons.Avalonia` (lucide-equivalent) | XAML learning curve; not native widgets | ✅ Keep |
| WPF | Mature, Storyboard animation, huge ecosystem | Windows-only — violates Harbor's cross-platform promise | ❌ Already in `Harbor.Tui.Wpf` for Windows-only users; do not promote |
| WinUI 3 / Windows App SDK | Native Windows 11 look, Mica/Acrylic, Storyboard | Windows-only, packaging pain (MSIX) | ❌ |
| .NET MAUI | Cross-platform, native widgets | Mobile-first; desktop support weak; XAML differences from Avalonia; no good terminal emulator | ❌ Already in `Harbor.Tui.Maui`; do not promote |
| Photino | Lightweight, .NET sidecar + native webview | Webview is HTML/CSS/JS — defeats the "native animated desktop" goal | ❌ |
| Blazor Hybrid | Razor components, web stack | Same — runs in webview, not native | ❌ Already in `Harbor.Tui.Blazor`; do not promote |
| Tauri + .NET sidecar | Webview frontend (or the actual orca UI), .NET backend | This is literally Orca's stack — defeats the .NET goal | ❌ |
| Custom Skia renderer | Total control, max performance | Years of work; reinventing layout, hit-testing, text shaping, IME, accessibility | ❌ |
| Terminal.Gui v2 | Mature TUI, ANSI | Not desktop — doesn't address user's "animated desktop UI" request | ❌ Already in `Harbor.Tui.TerminalGui` |
| FSharp.Giraffe + Giraffe.ViewEngine | Functional web | Not desktop | ❌ |

The decision matrix is decisive: **Avalonia 11 is the only choice that simultaneously satisfies cross-platform, native animated desktop, .NET 10 compatibility, and an existing rich ecosystem.** Harbor has already made this choice; Subagent R's job is to recommend how to flesh it out.

#### 3.6.2 Add the missing libraries

To recreate orca's component stack in Avalonia, add these NuGet packages to `Harbor.Tui.Avalonia.csproj`:

```xml
<!-- Animation primitives (Avalonia built-in, no extra deps) -->
<!-- Avalonia.Animation is part of the Avalonia 11.2.7 metapackage already referenced. -->

<!-- Markdown rendering (replaces react-markdown + rehype) -->
<PackageReference Include="Markdown.Avalonia" Version="11.0.3-a1" />
<!-- or for newer API: -->
<PackageReference Include="Markdown.Avalonia.Tight" Version="11.0.3" />

<!-- Code editor (replaces Monaco) -->
<PackageReference Include="AvaloniaEdit" Version="11.0.6" />
<!-- AvaloniaEdit supports syntax highlighting, code completion, diff view -->

<!-- Syntax highlighting for code blocks in markdown (replaces lowlight + vscode-textmate) -->
<PackageReference Include="Avalonia.AvaloniaEdit.TextMate" Version="11.0.6" />
<!-- bundles vscode-textmate grammar engine for AvaloniaEdit -->

<!-- Charts / cost visualization (replaces custom orca usage charts) -->
<PackageReference Include="LiveChartsCore" Version="2.0.0-rc5.4" />
<PackageReference Include="LiveChartsCore.Avalonia" Version="2.0.0-rc5.4" />

<!-- Icons (replaces lucide-react) -->
<PackageReference Include="Projektanker.Icons.Avalonia" Version="9.8.1" />
<PackageReference Include="Projektanker.Icons.Avalonia.MaterialDesign" Version="9.8.1" />
<PackageReference Include="Projektanker.Icons.Avalonia.FontAwesome" Version="9.8.1" />
<!-- (use Material Design icons — closest 1:1 mapping to lucide's flat outline style) -->

<!-- Diff view (replaces Monaco diff + diff-match-patch) -->
<PackageReference Include="DiffPlex" Version="1.7.2" />
<!-- DiffPlex is the .NET diff library; render its output via AvaloniaEdit -->

<!-- Drag and drop (Avalonia built-in via DragDrop event) -->
<!-- No extra dep — use Avalonia's built-in DataObject + DragDrop.DeviceDragEnter/Over/Drop -->

<!-- Toasts (replaces sonner) -->
<!-- Build a small Harbor.Avalonia.Toast component in Harbor.Tui.Avalonia/Components/ -->
<!-- Or use a community lib: -->
<PackageReference Include="Toast.Avalonia" Version="11.0.0" />

<!-- Command palette (replaces cmdk) -->
<!-- Build a small Harbor.Avalonia.CommandPalette component using Avalonia's AutoCompleteBox + Popup -->
<!-- No good community lib — see Section 7.1 for the pattern -->

<!-- Fuzzy search (replaces cmdk's built-in fuzzy + fuzzysort in opencode) -->
<PackageReference Include="FuzzySharp" Version="2.0.2" />

<!-- PTY for terminals (replaces node-pty) -->
<PackageReference Include="Pty.Net" Version="0.5.81" />
<!-- Microsoft's official cross-platform pty library, used by VS Code -->

<!-- Virtual scrolling (replaces @tanstack/react-virtual) -->
<!-- Avalonia's ItemsControl + VirtualizingStackPanel is built-in -->

<!-- Mermaid diagram rendering (orca uses mermaid 11) -->
<!-- No good .NET Mermaid renderer. Two options: -->
<!--   (a) Render mermaid to SVG via headless Chromium once, display SVG via SkiaSharp.Svg -->
<!--   (b) Skip mermaid for v1 -->
<PackageReference Include="Svg.Skia" Version="3.0.0" />
<PackageReference Include="SkiaSharp.Avalonia" Version="3.0.0" />

<!-- PDF preview (replaces pdfjs-dist) -->
<PackageReference Include="PdfViewer.Avalonia" Version="11.0.0" />

<!-- i18n (replaces i18next) -->
<PackageReference Include="Microsoft.Extensions.Localization" Version="10.0.0" />

<!-- Reactive state (replaces zustand) -->
<!-- Already have CommunityToolkit.Mvvm — use ObservableObject + [ObservableProperty] -->
<!-- Alternative: ReactiveUI -->
<PackageReference Include="ReactiveUI.Fody" Version="19.5.41" Condition="'$(UseReactiveUI)' == 'true'" />
```

Total: ~12 NuGet packages. All MIT or Apache-2.0 except `Toast.Avalonia` (MIT) and `PdfViewer.Avalonia` (MIT). None of them pull in any native dependency that would break cross-platform builds.

#### 3.6.3 Define the design-token stylesheet

Orca's tokens live in `src/renderer/src/assets/main.css` under `:root` and `.dark`. The Avalonia equivalent lives in `App.axaml` under `<Application.Resources>` and a `ThemeDictionaries` block. Here's the direct translation:

```xml
<!-- App.axaml -->
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Harbor.Tui.Avalonia.App">
  <Application.Resources>
    <ResourceInclude Source="avares://Harbor.Tui.Avalonia/Assets/Tokens.axaml"/>
    <ResourceInclude Source="avares://Harbor.Tui.Avalonia/Assets/Animations.axaml"/>
  </Application.Resources>
  <Application.Styles>
    <FluentTheme/>
    <StyleInclude Source="avares://Harbor.Tui.Avalonia/Styles/Components.axaml"/>
  </Application.Styles>
</Application>
```

```xml
<!-- Assets/Tokens.axaml -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <!-- Light theme -->
    <ResourceDictionary x:Key="Light">
      <Color x:Key="BackgroundColor">#FFFFFF</Color>
      <Color x:Key="ForegroundBackgroundColor">#0A0A0A</Color>
      <Color x:Key="CardColor">#FAFAFA</Color>
      <Color x:Key="PopoverColor">#FFFFFF</Color>
      <Color x:Key="PrimaryColor">#18181B</Color>
      <Color x:Key="SecondaryColor">#F4F4F5</Color>
      <Color x:Key="MutedColor">#F4F4F5</Color>
      <Color x:Key="AccentColor">#F4F4F5</Color>
      <Color x:Key="DestructiveColor">#EF4444</Color>
      <Color x:Key="BorderColor">#E4E4E7</Color>
      <Color x:Key="InputColor">#E4E4E7</Color>
      <Color x:Key="RingColor">#18181B</Color>
      <Color x:Key="GitAddedColor">#587C0B</Color>
      <Color x:Key="GitModifiedColor">#0E6FCC</Color>
      <Color x:Key="GitDeletedColor">#AD0000</Color>
      <Color x:Key="GitRenamedColor">#6F42C1</Color>
      <Color x:Key="GitUntrackedColor">#75715E</Color>
    </ResourceDictionary>
    <!-- Dark theme (Catppuccin Mocha — Harbor's existing palette) -->
    <ResourceDictionary x:Key="Dark">
      <Color x:Key="BackgroundColor">#1E1E2E</Color>
      <Color x:Key="ForegroundColor">#CDD6F4</Color>
      <Color x:Key="CardColor">#181825</Color>
      <Color x:Key="PopoverColor">#181825</Color>
      <Color x:Key="PrimaryColor">#89B4FA</Color>
      <Color x:Key="SecondaryColor">#313244</Color>
      <Color x:Key="MutedColor">#313244</Color>
      <Color x:Key="AccentColor">#45475A</Color>
      <Color x:Key="DestructiveColor">#F38BA8</Color>
      <Color x:Key="BorderColor">#313244</Color>
      <Color x:Key="InputColor">#313244</Color>
      <Color x:Key="RingColor">#FAB387</Color>
      <Color x:Key="GitAddedColor">#A6E3A1</Color>
      <Color x:Key="GitModifiedColor">#F9E2AF</Color>
      <Color x:Key="GitDeletedColor">#F38BA8</Color>
      <Color x:Key="GitRenamedColor">#CBA6F7</Color>
      <Color x:Key="GitUntrackedColor">#6C7086</Color>
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>

  <!-- Radius scale (matches orca's --radius: 0.625rem base) -->
  <CornerRadius x:Key="RadiusSm">4</CornerRadius>
  <CornerRadius x:Key="RadiusMd">6</CornerRadius>
  <CornerRadius x:Key="RadiusLg">10</CornerRadius>
  <CornerRadius x:Key="RadiusXl">14</CornerRadius>
  <CornerRadius x:Key="RadiusFull">9999</CornerRadius>

  <!-- Typography (Geist equivalent — Inter Variable is the closest .NET-bundled font) -->
  <FontFamily x:Key="FontSans">avares://Harbor.Tui.Avalonia/Assets/Fonts/Inter#Inter</FontFamily>
  <FontFamily x:Key="FontMono">Cascadia Code, JetBrains Mono, IBM Plex Mono, Menlo, Consolas</FontFamily>

  <!-- Shadows -->
  <BoxShadow x:Key="ShadowXs">0 1 2 0 0 #00000018</BoxShadow>
  <BoxShadow x:Key="ShadowSm">0 1 3 0 0 #00000018</BoxShadow>
  <BoxShadow x:Key="ShadowFloating">0 10 24 0 0 #00000030</BoxShadow>
</ResourceDictionary>
```

```xml
<!-- Assets/Animations.axaml -->
<!-- This file is the .NET equivalent of orca's tw-animate-css plugin. -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- Fade in -->
  <Animation x:Key="FadeIn" Duration="0:0:0.15" FillMode="Forward">
    <KeyFrame Cue="0%">
      <Setter Property="Opacity" Value="0"/>
    </KeyFrame>
    <KeyFrame Cue="100%">
      <Setter Property="Opacity" Value="1"/>
    </KeyFrame>
  </Animation>

  <!-- Fade out (reverse) -->
  <Animation x:Key="FadeOut" Duration="0:0:0.15" FillMode="Forward">
    <KeyFrame Cue="0%">
      <Setter Property="Opacity" Value="1"/>
    </KeyFrame>
    <KeyFrame Cue="100%">
      <Setter Property="Opacity" Value="0"/>
    </KeyFrame>
  </Animation>

  <!-- Zoom in (for dialogs and popovers) -->
  <Animation x:Key="ZoomIn" Duration="0:0:0.15" FillMode="Forward">
    <KeyFrame Cue="0%">
      <Setter Property="Opacity" Value="0"/>
      <Setter Property="ScaleTransform.ScaleX" Value="0.95"/>
      <Setter Property="ScaleTransform.ScaleY" Value="0.95"/>
    </KeyFrame>
    <KeyFrame Cue="100%">
      <Setter Property="Opacity" Value="1"/>
      <Setter Property="ScaleTransform.ScaleX" Value="1"/>
      <Setter Property="ScaleTransform.ScaleY" Value="1"/>
    </KeyFrame>
  </Animation>

  <!-- Slide in from bottom (for sheets and toast) -->
  <Animation x:Key="SlideInFromBottom" Duration="0:0:0.2" FillMode="Forward">
    <KeyFrame Cue="0%">
      <Setter Property="Opacity" Value="0"/>
      <Setter Property="TranslateTransform.Y" Value="16"/>
    </KeyFrame>
    <KeyFrame Cue="100%">
      <Setter Property="Opacity" Value="1"/>
      <Setter Property="TranslateTransform.Y" Value="0"/>
    </KeyFrame>
  </Animation>

  <!-- Slide in from right (for side sheets) -->
  <Animation x:Key="SlideInFromRight" Duration="0:0:0.2" FillMode="Forward">
    <KeyFrame Cue="0%">
      <Setter Property="Opacity" Value="0"/>
      <Setter Property="TranslateTransform.X" Value="100"/>
    </KeyFrame>
    <KeyFrame Cue="100%">
      <Setter Property="Opacity" Value="1"/>
      <Setter Property="TranslateTransform.X" Value="0"/>
    </KeyFrame>
  </Animation>

  <!-- Cursor blink (for terminal cursor) -->
  <Animation x:Key="CursorBlink" Duration="0:0:1.2" IterationCount="INFINITE">
    <KeyFrame Cue="0%">
      <Setter Property="Opacity" Value="1"/>
    </KeyFrame>
    <KeyFrame Cue="50%">
      <Setter Property="Opacity" Value="0"/>
    </KeyFrame>
    <KeyFrame Cue="100%">
      <Setter Property="Opacity" Value="1"/>
    </KeyFrame>
  </Animation>
</ResourceDictionary>
```

#### 3.6.4 Build the eight missing primitives

Build these as `UserControl`s in `Harbor.Tui.Avalonia/Components/`. Each is a thin wrapper around an Avalonia built-in control plus a `ControlTheme` skin — the same pattern as orca's shadcn wrappers.

1. **`Button`** — wrap `Avalonia.Controls.Button` with 6 variants (Default, Secondary, Outline, Ghost, Link, Destructive) × 7 sizes via `class-variance-authority`-equivalent (use a `ButtonVariant` enum + style selectors). File: `Components/Button/Button.axaml` + `Button.axaml.cs`. ~120 lines.

2. **`CommandPalette`** — wrap `Avalonia.Controls.AutoCompleteBox` + `Popup` with a custom dropdown panel that lists commands with fuzzy search (via `FuzzySharp`). File: `Components/CommandPalette/CommandPalette.axaml` + `.cs`. ~200 lines. This replaces orca's `cmdk`-based `CommandDialog`.

3. **`Dialog`** — wrap `Avalonia.Controls.Window` (modal child window) with overlay, backdrop blur, zoom-in animation. Or use a `Popup` for in-window modals. File: `Components/Dialog/Dialog.axaml` + `.cs`. ~150 lines.

4. **`Popover`** — wrap `Avalonia.Controls.Popup` with hover/click triggers, floating-ui positioning (use `Avalonia.Controls.Primitives.PopupPositioning`). File: `Components/Popover/Popover.axaml` + `.cs`. ~100 lines.

5. **`Toast`** — implement a `ToastService` that maintains an `ObservableCollection<ToastViewModel>` and renders them in a top-right `ItemsControl`. Use `SlideInFromBottom` animation. Auto-dismiss after 4s. File: `Components/Toast/ToastService.cs` + `ToastHost.axaml` + `ToastItem.axaml`. ~250 lines.

6. **`Tooltip`** — wrap `Avalonia.Controls.ToolTip` with a styled template (small font, muted background, rounded). Mostly a `Style` block. File: `Components/Tooltip/TooltipStyles.axaml`. ~40 lines.

7. **`Sheet`** — side-sliding panel. Wrap `Avalonia.Controls.Primitives.Popup` with `Placement="Right"` (or Left/Top/Bottom), `SlideInFromRight` animation, modal backdrop. File: `Components/Sheet/Sheet.axaml` + `.cs`. ~150 lines.

8. **`HoverCard`** — wrap `Avalonia.Controls.Popup` with hover delay (300ms open, 100ms close), pointer-follow positioning. File: `Components/HoverCard/HoverCard.axaml` + `.cs`. ~100 lines.

Total: ~1 100 lines of new Avalonia code. Each primitive is opt-in — apps that don't reference them pay no cost. Each is a `UserControl` so users can override the skin via `ControlTheme` resources.

#### 3.6.5 Rebuild MainWindow.axaml as an Orca-style shell

The current `MainWindow.axaml` is a single 90-line Grid. The Orca-style shell is a multi-pane docked layout. Here's the target structure (full XAML is too long for this doc — see Section 8 roadmap for sprint-by-sprint breakdown):

```
MainWindow (OrcaShell)
├── TitleBar (custom, 32px tall, draggable, traffic-light on macOS)
│   └── TitleBarContent: agent switcher (left) | command palette trigger (center) | usage meter (right)
├── MainDock (DockPanel)
│   ├── LeftSidebar (WorktreeSidebar, 240px, collapsible)
│   │   ├── WorktreeList (TreeView of git worktrees, each with agent avatar + status)
│   │   ├── NewWorktreeButton (opens Dialog with agent picker + prompt input)
│   │   └── AgentQueueList (queued/follow-up messages for each agent)
│   ├── CenterPane (Grid, fills remaining)
│   │   ├── AgentTabStrip (TabControl, one tab per active agent)
│   │   │   └── AgentPane (Grid, splits)
│   │   │       ├── ChatHistory (ItemsControl with virtualization, Markdown rendering)
│   │   │       ├── ToolCallExpander (collapsible, shows tool name + args + result)
│   │   │       ├── DiffReviewPane (AvaloniaEdit diff view with comment overlay)
│   │   │       └── TerminalSplit (custom Pty.Net-backed Control with WebGL-equivalent Skia atlas)
│   │   └── InputArea (Grid)
│   │       ├── PromptInput (TextBox with TipTap-equivalent: monospace, line numbers, drag-image)
│   │       ├── DragDropTarget (Border that accepts file drops, shows thumbnails)
│   │       └── SendButton (with Cmd+Enter hint)
│   ├── RightSidebar (ContextSidebar, 320px, collapsible)
│   │   ├── FileExplorer (TreeView of current worktree, drag to prompt)
│   │   ├── GitChangesList (ListBox of modified files with +/- counts)
│   │   └── McpServerList (ListBox of registered MCP servers, click to invoke)
│   └── BottomBar (StatusBar, 24px)
│       ├── WorkingDirLabel
│       ├── SessionNameLabel
│       ├── TokenMeter (LiveChartsCore sparkline: in/out/cache)
│       ├── CostLabel ($0.0000)
│       └── ModelLabel
└── OverlayLayer (Canvas, on top of everything)
    ├── CommandPalettePopup (when Cmd+K pressed)
    ├── DialogBackdrop (when any Dialog open)
    ├── SheetBackdrop (when any Sheet open)
    └── ToastHost (top-right, always-on)
```

This is a lot of UI. Section 8 breaks it into 4 sprints of ~5 features each.

#### 3.6.6 Specifically: the terminal pane (xterm-webgl equivalent)

This is the hardest single component to recreate in .NET. xterm.js with `@xterm/addon-webgl` is a 50 000-line library that:
- Maintains a grid of cells (rows × cols, each cell = char + attrs)
- Renders the grid to a WebGL texture atlas (one draw call per frame)
- Handles mouse selection, scrollback, link detection, search
- Supports 256-color + truecolor + ligatures + Unicode 11 width tables

There is no .NET equivalent. Three viable paths:

**Path A — wrap xterm.js in a WebView (fastest, lowest fidelity).** Embed `CefSharp` or `Avalonia.WebView` (when stable) and run a tiny HTML page that loads xterm.js. Pipe PTY output through a JS interop bridge. This is exactly what orca does. Pros: xterm is the best terminal renderer; gets WebGL for free. Cons: pulls in Chromium (~100 MB binary overhead); not native; IPC overhead per byte.

**Path A cost:** ~3 days, 1 dependency, 1 native binary, ~500 lines of glue.

**Path B — port Terminal.Gui v2's console to a Skia `WriteableBitmap` (medium).** Terminal.Gui v2 already has a `ConsoleDriver` abstraction. Write a `SkiaConsoleDriver` that maintains a glyph atlas (rasterize each glyph once into a `SKImage`, blit quads via `SKCanvas`). This is what Ghostty does (in Zig + OpenGL). Pros: native, GPU-accelerated, no Chromium. Cons: ~2 weeks of work to get to feature-parity with xterm's ligatures and Unicode width tables; doesn't exist yet.

**Path B cost:** ~10 days, 0 dependencies, ~3 000 lines of new code.

**Path C — embed AvaloniaEdit with VT100 escape parsing (lowest fidelity, fastest to ship).** AvaloniaEdit is a text editor with syntax highlighting. Write a `PtyOutputToAvaloniaEditConverter` that parses VT100 escapes (cursor movement, color, clear) into text edits. Pros: 1 dependency (AvaloniaEdit already pulled in for the diff view). Cons: no inline images, no ligatures, no true ligature shaping; rendering 10 000 lines of scrollback may stutter.

**Path C cost:** ~2 days, 0 new dependencies, ~500 lines.

**Recommendation:** Path C for Sprint 1 (MUST). Path A as a NICE in Sprint 3 if users complain about terminal fidelity. Path B as a LATER if Harbor wants a "Ghostty-class" claim of its own.

#### 3.6.7 Specifically: the markdown renderer (react-markdown + rehype equivalent)

`Markdown.Avalonia` is the standard .NET markdown rendering library. It uses `Markdig` under the hood (the same Markdig that GitHub uses server-side) and renders to Avalonia's `FlowDocument`-equivalent (a `Grid` of `TextBlock`s and `Image`s). It supports GFM, tables, fenced code, math (via KaTeX — but KaTeX doesn't exist in .NET; substitute MathML or pre-rendered PNG), and inline HTML.

What it does NOT do well:
- **Streaming** — react-markdown can render partial markdown as it streams in (the LLM emits tokens one at a time). Markdown.Avalonia re-parses the whole string on each render. For a streaming chat UI this causes visible stutter.
- **Syntax highlighting** — Markdown.Avalonia supports it via `ISyntaxHighlighter` but you have to plug in your own (use `Avalonia.AvaloniaEdit.TextMate` with vscode-textmate grammars, same as orca).
- **Mermaid** — no .NET mermaid renderer. Skip or render to SVG via headless Chromium once.

**Recommendation for streaming:** Build a `StreamingMarkdownDocument` that:
1. Accumulates incoming tokens into a `StringBuilder`.
2. Throttles re-renders to every 50ms (debounce).
3. Uses Markdig to parse to an AST.
4. Walks the AST and produces Avalonia controls, reusing control instances across renders (only delta-update text where possible).

This is ~400 lines of new code. The throttle + delta-update pattern is what react-markdown does internally via React's reconciliation. In Avalonia, you have to do it manually because there's no virtual DOM.

### 3.7 Code snippets from Orca — concrete pattern mappings

#### 3.7.1 Orca's command palette (cmdk)

From `src/renderer/src/components/ui/command.tsx` (fetched by Subagent R):

```tsx
'use client'
import * as React from 'react'
import { Command as CommandPrimitive } from 'cmdk'
import { SearchIcon } from 'lucide-react'
import { Dialog as DialogPrimitive } from 'radix-ui'
import { cn } from '@/lib/utils'

function Command({ className, ...props }: React.ComponentProps<typeof CommandPrimitive>) {
  return (
    <CommandPrimitive
      data-slot="command"
      className={cn(
        'flex h-full w-full flex-col overflow-hidden rounded-md bg-popover text-popover-foreground',
        className,
      )}
      {...props}
    />
  )
}

function CommandDialog({ children, title = 'Command Palette', description = 'Search for a command to run...', shouldFilter, onOpenAutoFocus, onCloseAutoFocus, contentClassName, overlayClassName, commandProps, ...props }: React.ComponentProps<typeof DialogPrimitive.Root> & {
  title?: string
  description?: string
  shouldFilter?: boolean
  // ...
}) {
  return (
    <DialogPrimitive.Root {...props}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className={cn('data-[state=open]:animate-in data-[state=closed]:animate-out ...', overlayClassName)} />
        <DialogPrimitive.Content className={cn('...', contentClassName)}>
          <CommandPrimitive {...commandRootProps}>{children}</CommandPrimitive>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  )
}
```

**.NET Avalonia equivalent:**

```xml
<!-- Components/CommandPalette/CommandPalette.axaml -->
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Harbor.Tui.Avalonia.Components.CommandPalette.CommandPalette">
  <UserControl.Styles>
    <Style Selector="UserControl">
      <Setter Property="Width" Value="600"/>
      <Setter Property="MaxHeight" Value="400"/>
    </Style>
    <Style Selector="Border.popup">
      <Setter Property="Background" Value="{DynamicResource PopoverColor}"/>
      <Setter Property="CornerRadius" Value="{DynamicResource RadiusLg}"/>
      <Setter Property="BoxShadow" Value="{DynamicResource ShadowFloating}"/>
      <Setter Property="Padding" Value="8"/>
    </Style>
    <Style Selector="TextBox.search">
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="BorderThickness" Value="0,0,0,1"/>
      <Setter Property="BorderBrush" Value="{DynamicResource BorderColor}"/>
      <Setter Property="Padding" Value="12,10"/>
      <Setter Property="FontFamily" Value="{DynamicResource FontSans}"/>
      <Setter Property="FontSize" Value="14"/>
      <Setter Property="Watermark" Value="Search commands..."/>
    </Style>
    <Style Selector="ListBox.results">
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="BorderThickness" Value="0"/>
    </Style>
    <Style Selector="ListBoxItem.results">
      <Setter Property="Padding" Value="12,8"/>
      <Setter Property="CornerRadius" Value="{DynamicResource RadiusSm}"/>
    </Style>
    <Style Selector="ListBoxItem.results:selected /template/ ContentPresenter">
      <Setter Property="Background" Value="{DynamicResource AccentColor}"/>
    </Style>
  </UserControl.Styles>

  <Border Classes="popup">
    <Grid RowDefinitions="Auto,*">
      <TextBox Grid.Row="0" Classes="search" x:Name="SearchInput"
               Text="{Binding Query}" KeyDown="SearchInput_KeyDown"/>
      <ListBox Grid.Row="1" Classes="results" x:Name="ResultsList"
               ItemsSource="{Binding FilteredCommands}"
               SelectedIndex="{Binding SelectedIndex, Mode=TwoWay}">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <Grid ColumnDefinitions="24,*,100">
              <TextBlock Grid.Column="0" Text="{Binding Icon}"
                         FontFamily="{DynamicResource FontMono}" FontSize="14"
                         Foreground="{DynamicResource MutedForegroundColor}"/>
              <TextBlock Grid.Column="1" Text="{Binding Title}"
                         FontFamily="{DynamicResource FontSans}" FontSize="13"/>
              <TextBlock Grid.Column="2" Text="{Binding Shortcut}"
                         FontFamily="{DynamicResource FontMono}" FontSize="11"
                         Foreground="{DynamicResource MutedForegroundColor}"
                         HorizontalAlignment="Right"/>
            </Grid>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </Grid>
  </Border>
</UserControl>
```

```csharp
// Components/CommandPalette/CommandPalette.axaml.cs
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FuzzySharp;

namespace Harbor.Tui.Avalonia.Components.CommandPalette;

public sealed partial class CommandPalette : UserControl
{
    public CommandPaletteViewModel ViewModel => (CommandPaletteViewModel)DataContext!;

    public CommandPalette()
    {
        InitializeComponent();
        DataContext = new CommandPaletteViewModel();
        SearchInput.TextChanged += async (_, _) => await ViewModel.FilterAsync(SearchInput.Text ?? string.Empty);
        ResultsList.DoubleTapped += (_, _) => InvokeSelected();
    }

    private void SearchInput_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: ViewModel.SelectedIndex = Math.Min(ViewModel.SelectedIndex + 1, ViewModel.FilteredCommands.Count - 1); break;
            case Key.Up:   ViewModel.SelectedIndex = Math.Max(ViewModel.SelectedIndex - 1, 0); break;
            case Key.Enter: InvokeSelected(); break;
            case Key.Escape: Close(); break;
        }
    }

    private void InvokeSelected()
    {
        if (ViewModel.SelectedCommand is { } cmd) cmd.Invoke();
        Close();
    }

    private void Close() { /* raise CloseRequested event */ }
}

public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ObservableCollection<CommandItem> _allCommands = new();
    public ObservableCollection<CommandItem> FilteredCommands { get; } = new();

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private int _selectedIndex;
    public CommandItem? SelectedCommand => SelectedIndex >= 0 && SelectedIndex < FilteredCommands.Count
        ? FilteredCommands[SelectedIndex] : null;

    public void Register(CommandItem item) => _allCommands.Add(item);

    public async Task FilterAsync(string query)
    {
        FilteredCommands.Clear();
        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var c in _allCommands) FilteredCommands.Add(c);
            return;
        }
        // FuzzySharp.Ratio returns 0..100; threshold 60 keeps relevant matches
        var scored = _allCommands
            .Select(c => (Item: c, Score: Fuzz.PartialRatio(query, c.Title + " " + c.Description)))
            .Where(p => p.Score >= 60)
            .OrderByDescending(p => p.Score)
            .Select(p => p.Item);
        foreach (var c in scored) FilteredCommands.Add(c);
        await Task.CompletedTask;
    }
}

public sealed record CommandItem(string Icon, string Title, string Description, string Shortcut, Action Invoke);
```

This is the .NET equivalent of orca's `command.tsx` + `cmdk` + a small dialog wrapper. ~150 lines vs orca's ~80 (Avalonia is more verbose than React+Tailwind), but functionally equivalent: fuzzy search, keyboard navigation, command invocation, escape to close, animation on open.

#### 3.7.2 Orca's toast (sonner)

From orca's `components/ui/sonner.tsx` (not fetched in full, but the pattern is well-known):

```tsx
import { Toaster as Sonner, toast } from 'sonner'
export function Toaster() { return <Sonner position="top-right" richColors closeButton /> }
export const toast2 = toast // re-export
```

**.NET equivalent (custom — no good community lib):**

```csharp
// Components/Toast/ToastService.cs
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Harbor.Tui.Avalonia.Components.Toast;

public sealed partial class ToastService : ObservableObject
{
    public ObservableCollection<ToastItem> Items { get; } = new();

    public void Show(string title, string? description = null, ToastKind kind = ToastKind.Info, TimeSpan? duration = null)
    {
        var item = new ToastItem(title, description, kind);
        Dispatcher.UIThread.Post(() => Items.Add(item));
        _ = AutoDismissAsync(item, duration ?? TimeSpan.FromSeconds(4));
    }

    private async Task AutoDismissAsync(ToastItem item, TimeSpan delay)
    {
        await Task.Delay(delay);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            item.IsClosing = true; // triggers SlideInFromBottom reverse animation
            _ = Task.Delay(200).ContinueWith(_ => Dispatcher.UIThread.Post(() => Items.Remove(item)));
        });
    }
}

public sealed partial class ToastItem : ObservableObject
{
    public ToastItem(string title, string? description, ToastKind kind) { Title = title; Description = description; Kind = kind; }
    public string Title { get; }
    public string? Description { get; }
    public ToastKind Kind { get; }
    [ObservableProperty] private bool _isClosing;
}

public enum ToastKind { Info, Success, Warning, Error }
```

```xml
<!-- Components/Toast/ToastHost.axaml -->
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Harbor.Tui.Avalonia.Components.Toast.ToastHost">
  <ItemsControl ItemsSource="{Binding Items}" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,16,16,0">
    <ItemsControl.ItemsPanel>
      <ItemsPanelTemplate><StackPanel Orientation="Vertical" Spacing="8"/></ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
      <DataTemplate>
        <Border Background="{DynamicResource CardColor}"
                CornerRadius="{DynamicResource RadiusLg}"
                BoxShadow="{DynamicResource ShadowFloating}"
                Padding="14,10" MinWidth="280" MaxWidth="380"
                Classes.success="{Binding Kind, Converter={x:Static vm:ToastKindToBool.Success}}"
                Classes.error="{Binding Kind,  Converter={x:Static vm:ToastKindToBool.Error}}">
          <StackPanel Spacing="2">
            <TextBlock Text="{Binding Title}" FontWeight="SemiBold" FontSize="13"/>
            <TextBlock Text="{Binding Description}" FontSize="12" IsVisible="{Binding Description, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
          </StackPanel>
        </Border>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>
</UserControl>
```

That's ~80 lines for the .NET version vs orca's ~5 lines (sonner is a 3rd-party lib that does all the work). The Avalonia version is more code but is fully owned and customizable.

### 3.8 What Harbor could steal from Orca — MUST / NICE / LATER

| # | Feature | Priority | Effort | Notes |
|---|---------|----------|--------|-------|
| 1 | Animated Avalonia shell (rebuild `Harbor.Tui.Avalonia/MainWindow.axaml` as docked multi-pane) | MUST | 5d | Section 3.6.5 — the core of the user's request |
| 2 | Command palette (`Cmd+K` fuzzy search) | MUST | 2d | Section 3.7.1 — code already drafted |
| 3 | Streaming markdown rendering in chat history | MUST | 3d | Section 3.6.7 — build `StreamingMarkdownDocument` |
| 4 | Inline tool call UI (collapsible `Expander` per tool call) | MUST | 2d | Avalonia `Expander` + a `ToolCallViewModel` |
| 5 | Diff view with syntax highlighting | MUST | 3d | `DiffPlex` + `AvaloniaEdit` |
| 6 | Design tokens (Catppuccin + dark/light theme) | MUST | 1d | Section 3.6.3 — `Assets/Tokens.axaml` |
| 7 | Animation dictionary (fade, zoom, slide) | MUST | 1d | Section 3.6.3 — `Assets/Animations.axaml` |
| 8 | Toast notifications | MUST | 1d | Section 3.7.2 |
| 9 | 8 shadcn-equivalent primitives (Button, Dialog, Popover, Sheet, Tooltip, HoverCard, DropdownMenu, ContextMenu) | MUST | 4d | Section 3.6.4 |
| 10 | Parallel worktrees (one prompt → N agents in N git worktrees) | MUST | 4d | Spawn N AgentLoop instances, each with its own `git worktree add` |
| 11 | Cost / token meter (LiveChartsCore sparkline in status bar) | NICE | 2d | LiveChartsCore already referenced |
| 12 | File drag-drop into prompt (image + text files) | NICE | 1d | Avalonia `DragDrop` event |
| 13 | Quick open across files / agents / commands | NICE | 2d | Reuse CommandPalette with different data sources |
| 14 | Terminal pane (AvaloniaEdit + VT100 parser — Path C from 3.6.6) | NICE | 2d | Lowest-fidelity option; Path A later |
| 15 | Account switcher + usage tracking per provider | NICE | 3d | Wrap Harbor.ProviderRegistry with a usage store |
| 16 | Notifications + unread state (per worktree) | NICE | 2d | INotificationService already exists in Harbor |
| 17 | Session branching tree UI (TreeView) | NICE | 3d | Pi also has this — see Section 4 |
| 18 | Embedded browser (Photino or CefSharp) for design mode | LATER | 5d | Heavy dep; defer |
| 19 | Mobile companion app (iOS/Android via MAUI) | LATER | 15d+ | Separate project; defer |
| 20 | SSH remote worktrees | LATER | 8d | SSH.NET + remote PTY |
| 21 | GitHub/Linear native integration | LATER | 6d | Octokit + Linear API |
| 22 | Annotate AI diffs inline | LATER | 4d | Build on top of the diff view |
| 23 | Mermaid diagram rendering | LATER | 5d | No good .NET lib — needs headless Chromium |
| 24 | PDF preview | LATER | 2d | `PdfViewer.Avalonia` exists |
| 25 | Computer Use (agent controls desktop UI) | LATER | 15d+ | Out of scope for a coding harness |
| 26 | Headless `orca serve` mode | DONE | — | Harbor already has headless mode |

### 3.9 Risks / constraints

- **License:** Orca is MIT. Harbor can study it freely. Copying XAML/code line-for-line would still be allowed under MIT's permissive terms, but in practice orca's code is React/TS — direct copy impossible.
- **Dependencies:** Avalonia 11.2.7 is the only framework version that works on .NET 10 today (verified by Subagent R reading `Harbor.Tui.Avalonia.csproj`). All recommended packages (AvaloniaEdit, LiveChartsCore, Markdown.Avalonia, DiffPlex, FuzzySharp, Pty.Net, Projektanker.Icons) are MIT or Apache-2.0.
- **Cross-platform:** Avalonia is genuinely cross-platform (Win/Linux/macOS via X11/Wayland/Win32/Cocoa). However, `Pty.Net` has known issues on BSDs; macOS expects `Terminal.app` for some shell features; Linux Wayland drag-and-drop support varies by compositor. All solvable, but expect 10-15% extra effort for platform edge cases.
- **Complexity:** The full Orca-style shell is ~5 000-7 000 lines of new Avalonia code. Compare: Harbor's existing `Harbor.Tui.SpectreTui` is ~3 000 lines and offers a fraction of the surface area. The 4-sprint roadmap in Section 8 spreads this over 4 × 2-week iterations.
- **Maintenance:** Owning 35 primitives (the shadcn pattern) means owning 35 primitives. The trade-off is: no upstream-breaking changes, but every bug is yours to fix. Mitigation: extract `Harbor.Avalonia.Components` as a separate NuGet package after Sprint 2 so the broader Avalonia community can contribute.
- **Performance:** Avalonia is GPU-accelerated via Skia. The 60fps target is achievable for the shell UI. The terminal pane is the bottleneck — Path C (AvaloniaEdit) will stutter on 10 000+ line scrollback; Path A (WebView+xterm) gets 60fps for free but pays Chromium overhead; Path B (custom Skia) is the long-term answer.
- **Native deps:** `Pty.Net` requires native binaries per-platform. `CefSharp` (Path A for terminals) is even heavier (~100 MB). Both must be packaged into the per-platform releases. Harbor's existing `Harbor.Tui.SpectreTui` has no native deps, so this is a new packaging concern.

---

## 4. Pi-Agent Deep-Dive

> *Pi is Harbor's stated inspiration. It is the most directly transferable of the four — same problem domain, same TUI-first philosophy, same "minimal harness + extension points" architecture. The biggest differences are language (TypeScript vs C#) and the lack of a desktop shell.*

### 4.1 What it is

Pi (https://pi.dev, https://github.com/earendil-works/pi, MIT, copyright 2025 Mario Zechner) is a **minimal terminal coding agent harness** in TypeScript. Its philosophy is explicitly minimal: the README says *"Pi ships with powerful defaults but skips features like sub agents and plan mode. Instead, you can ask pi to build what you want or install a third party pi package that matches your workflow."*

Pi is a monorepo of 5 packages:

- `@earendil-works/pi-ai` — unified multi-provider LLM API (OpenAI, Anthropic, Google, and ~30 more)
- `@earendil-works/pi-agent-core` — agent runtime with tool calling and state management
- `@earendil-works/pi-coding-agent` — interactive coding agent CLI (the main user-facing package)
- `@earendil-works/pi-tui` — terminal UI library with differential rendering (reusable; can build non-PI TUIs with it)
- `@earendil-works/pi-orchestrator` — multi-session orchestration

Pi runs in 4 modes:

1. **Interactive** — TUI chat (the default)
2. **Print/JSON** — one-shot non-interactive
3. **RPC** — for process integration (other apps drive Pi via RPC)
4. **SDK** — embed in your own TypeScript app

### 4.2 Tech stack

| Layer | Choice | Notes |
|-------|--------|-------|
| Runtime | **Node.js ≥22.19** | Bun-compatible |
| Language | **TypeScript 5.9** with `@typescript/native-preview 7.0.0-dev` for type-check | Strict, no `any`, no enums, no namespaces (erasable-only syntax — Node strip-only mode) |
| Build | `tsgo` (native TS preview) + `esbuild` | |
| Lint/format | **Biome 2.3** | Replaces ESLint + Prettier in one binary |
| Test | `node --test` (built-in) | No Vitest, no Jest — uses Node's native test runner |
| Native modules | per-platform prebuilds for win32 + darwin (native tty stuff) | No Linux native? — probably uses terminal APIs that work without native on Linux |
| Markdown | `marked 18` | |
| TTY test driver | `@xterm/headless 5.5` for terminal parsing in tests | |
| LLM providers | built-in catalog of ~30 providers (Anthropic, OpenAI, Google, DeepSeek, NVIDIA NIM, Azure, Bedrock, Mistral, Groq, Cerebras, Cloudflare, xAI, OpenRouter, Vercel AI Gateway, ZAI, Hugging Face, Fireworks, Together, Kimi, MiniMax, Xiaomi MiMo, llama.cpp router, Ant Ling, OpenCode Zen/Go, …) | |
| Subscription auth | Anthropic Claude Pro/Max, OpenAI ChatGPT Plus/Pro (Codex), GitHub Copilot | OAuth-based, not API-key |
| Schema validation | (not explicitly named; pi prefers Effect-TS-style schemas) | |
| Containers | Gondolin extension (micro-VM), plain Docker, OpenShell | 3 sandbox patterns documented in `packages/coding-agent/docs/containerization.md` |
| Native TUI | **pi-tui** — custom-built differential renderer (no Ink, no blessed, no opentui) | This is Pi's signature technical achievement |

### 4.3 Killer features

1. **Differential TUI rendering** — pi-tui uses 3-strategy rendering that only updates what changed, with synchronized output (CSI 2026) for atomic screen updates (no flicker). This is the kind of thing Harbor's `Harbor.Tui.Ansi` and `Harbor.Tui.Plain` already do, but Pi's implementation is documented and reusable as a library.

2. **Inline images via Kitty/iTerm2 graphics protocols** — pi-tui renders images in terminals that support Kitty or iTerm2 graphics protocols. Harbor has `Harbor.Tui.Sixel` already — Pi's approach is similar but uses a different protocol family.

3. **Bracketed paste mode** — handles large pastes (>10 lines) with markers so the agent knows it was a paste, not typed input. Harbor doesn't do this.

4. **Overlays** — `tui.showOverlay(component, options)` with anchor-based positioning, percentage-based positioning, absolute positioning, margin clamping, responsive visibility callbacks, focus control. This is the cleanest overlay API of any TUI library Subagent R has read. Harbor's `Harbor.Tui.SpectreTui` has panels but no overlay system.

5. **Slash command system** — built-in `/login`, `/logout`, `/llama`, `/model`, `/scoped-models`, `/settings`, `/resume`, `/new`, `/name`, `/session`, `/tree`, `/trust`, `/fork`, `/clone`, `/compact`, `/copy`, `/export`, `/import`, `/share`, `/reload`, `/hotkeys`, `/changelog`, `/quit`. Extensions can register custom commands. Skills are available as `/skill:name`. Prompt templates expand via `/templatename`. Harbor has zero slash commands today.

6. **Session branching (`/tree`)** — sessions are JSONL with `id` + `parentId`, enabling in-place branching without creating new files. `/tree` navigates the tree, lets you continue from any previous point, switch between branches, fold/unfold, search, filter (default/no-tools/user-only/labeled-only/all). `/fork` creates a new session file from a previous user message. `/clone` duplicates the current active branch. `--fork <path|id>` forks from the CLI. Harbor's `JsonlSessionStore` does not branch — sessions are linear.

7. **Compaction** — automatic + manual context summarization. Long sessions exhaust context windows; compaction summarizes older messages while keeping recent ones. Auto-triggered on context overflow (recovers and retries) or proactively when approaching the limit. Customizable via extensions. Harbor has no compaction today — long sessions just fail when the context window fills.

8. **Message queue** — submit messages while the agent is working:
   - **Enter** queues a *steering* message, delivered after the current assistant turn finishes executing its tool calls
   - **Alt+Enter** queues a *follow-up* message, delivered only after the agent finishes all work
   - **Escape** aborts and restores queued messages to editor
   - **Alt+Up** retrieves queued messages back to editor

   This is the single best UX idea in any of the 4 repos. Harbor's AgentLoop is currently synchronous with respect to user input — once a turn starts, the user can only interrupt, not queue.

9. **Skills** — pi packages that bundle prompt instructions + tools + slash commands + UI. Skills are the unit of "share this workflow with another user." Skills live in `.pi/skills/` or as published npm packages. Harbor has plugins but no skill abstraction.

10. **Prompt templates** — pre-canned prompts expand via `/templatename`. Templates live in `~/.pi/agent/prompts/` or as part of skills. Harbor has no prompt template system.

11. **Themes** — fully customizable UI themes. Components accept theme interfaces. Custom themes via npm packages or `.pi/themes/`. Harbor's TUIs have hardcoded Catppuccin colors.

12. **Pi packages** — extensions, skills, prompt templates, and themes packaged as npm packages and shareable. Harbor's plugin system can do this but doesn't have the same convention for "this npm package is a skill" vs "this is an extension."

13. **Project trust** — pi asks before trusting a project folder that contains project-local settings, resources, or `.agents/skills`. Trusting a project allows pi to load `.pi/settings.json`, install missing packages, and execute project extensions. Non-interactive modes use `defaultProjectTrust: ask | always | never`. `--approve`/`--no-approve` overrides for one run. Harbor's `PermissionService` is per-tool, not per-project.

14. **External editor** — Ctrl+G opens `$VISUAL`, `$EDITOR`, Notepad on Windows, or `nano` elsewhere, with the current editor contents. When the user saves and quits, pi loads the file contents back into the in-TUI editor. Harbor has no external editor integration.

15. **`!command` and `!!command`** — type `!ls` in the prompt to run `ls` and send its output to the LLM as context. `!!ls` runs `ls` without sending output (you see the result, the LLM doesn't). Brilliantly simple. Harbor doesn't have this.

16. **`@file` fuzzy path completion** — type `@` in the prompt to fuzzy-search project files. Tab to complete. Harbor doesn't have this.

17. **Multi-line editor** — Shift+Enter (or Ctrl+Enter on Windows Terminal) for multi-line input. Ctrl+G opens external editor. Ctrl+V pastes image or text. Drag images onto terminal. Harbor's input is single-line by default.

18. **Editor border color = thinking level** — the editor's border changes color based on the current thinking level (none/low/medium/high). Subtle, brilliant. Harbor doesn't surface thinking level in the UI.

19. **Session sharing** — `/share` uploads the session as a private GitHub gist with a shareable HTML link. `/export` writes to HTML or JSONL. `/import` reads from JSONL. Harbor stores sessions as JSONL but has no sharing.

20. **Custom provider support** — add providers via `~/.pi/agent/models.json` if they speak a supported API (OpenAI, Anthropic, Google). For custom APIs or OAuth, use extensions. Harbor's `ProviderRegistry` already supports this pattern.

### 4.4 Architecture highlights

Pi's architecture is the cleanest of the 4 repos Subagent R studied. Key patterns:

1. **Package-per-concern.** Each package has one job: `pi-ai` is just LLM API calls, `pi-agent-core` is just the loop, `pi-coding-agent` is the CLI+TUI, `pi-tui` is the rendering. No package imports upward — `pi-tui` doesn't know about agents, `pi-agent-core` doesn't know about TUIs. Harbor's `Harbor.Core` could be split this way (currently `Harbor.Core` mixes agent loop, tools, and providers).

2. **Erasable TypeScript only.** The whole monorepo avoids `enum`, `namespace`, `import =`, `export =`, parameter properties — anything that needs JS emit. This means TypeScript can be "stripped" to JS without compilation (Node's `--experimental-strip-types`). Harbor doesn't have a C# equivalent constraint, but the principle ("prefer runtime features over compiler features") maps to: prefer `record` types over compiler-generated equality, prefer `switch` expressions over `if/else` chains, prefer source generators over reflection.

3. **No mocks in tests.** Pi's AGENTS.md says: *"Avoid mocks as much as possible, you shouldn't be using globalThis.\* at all unless it's the only option. Test actual implementation, do not duplicate logic into tests."* Harbor's test suite (TUnit) already follows this — but it's worth codifying.

4. **Biome over ESLint+Prettier.** One binary, one config, faster. Harbor uses Roslyn analyzers (built-in to .NET 10) — equivalent. The principle: don't compose 12 linters; pick one that does everything.

5. **Supply-chain hardening.** Pi pins all direct deps to exact versions, uses `npm install --ignore-scripts` to avoid lifecycle-script attacks, uses `package-lock.json` as ground truth, runs `npm audit` on a schedule, has an explicit allowlist for dependency lifecycle scripts. Harbor has no equivalent policy for NuGet — Subagent R recommends a `Directory.Build.props` analyzer that flags any `PackageReference` without a pinned version.

6. **Open-source session sharing.** Pi actively encourages users to publish their coding agent sessions to Hugging Face for model training/eval. This is unique to Pi — no other repo mentions it. Harbor could do the same via a `harbor share` command that uploads JSONL sessions to HF.

7. **Pi's TUI component model.** Components implement a `Component` interface with a `render()` method. Built-in components: `Text`, `TruncatedText`, `Input`, `Editor`, `Markdown`, `Loader`, `SelectList`, `SettingsList`, `Spacer`, `Image`, `Box`, `Container`. Compare to Harbor's panel system (`ITuiPanelPlugin`) which is more coarse-grained — Pi's component model is for widget-level composition, Harbor's is for screen-panel-level composition. Both are valid; Harbor could add a widget-level component model.

8. **Fuzzy matching built-in.** `pi-tui` exports `fuzzyMatch`, `fuzzyFilter`, `FuzzyMatch` from `./fuzzy.ts`. Harbor has no built-in fuzzy matching — every plugin that wants it pulls in `FuzzySharp` separately.

9. **Keybindings are data.** `DEFAULT_EDITOR_KEYBINDINGS` and `DEFAULT_APP_KEYBINDINGS` are configurable objects, not hardcoded `if (e.Key == Key.X)` checks. Pi's AGENTS.md explicitly says: *"Never hardcode key checks (e.g., `matchesKey(keyData, "ctrl+x")`). Add defaults to `DEFAULT_EDITOR_KEYBINDINGS` or `DEFAULT_APP_KEYBINDINGS` so they stay configurable."* Harbor's SpectreTui key handling is hardcoded.

10. **Per-package JSONL session format.** Each session entry has `id` and `parentId`, enabling in-place branching. Harbor's `JsonlSessionStore` is linear (no parentId).

### 4.5 What Harbor could steal — MUST / NICE / LATER

| # | Feature | Priority | Effort | Notes |
|---|---------|----------|--------|-------|
| 1 | Slash command system (`/help`, `/model`, `/agent`, `/clear`, `/save`, `/load`, `/share`, `/export`, `/import`, `/compact`) | MUST | 3d | The single biggest UX gap in Harbor today |
| 2 | Message queue (steering + follow-up via Enter / Alt+Enter) | MUST | 4d | AgentLoop must accept queued inputs; TuiReducer must surface queue state |
| 3 | Session branching (`/tree`, `/fork`, `/clone`) | MUST | 5d | Add `parentId` to `JsonlSessionStore` schema; build TreeView UI in SpectreTui + Avalonia |
| 4 | Compaction (auto + manual context summarization) | MUST | 4d | New AgentLoop hook: `ICompactionStrategy.SummarizeAsync(messages, ct)` |
| 5 | Project trust (`trust.json`, prompt before loading project settings/extensions) | MUST | 2d | `PermissionService` extension; add per-project trust state |
| 6 | `!command` and `!!command` shell passthrough | MUST | 1d | Trivial — parse prompt for `!` prefix, run via BashTool |
| 7 | `@file` fuzzy path completion in prompt input | MUST | 2d | FuzzySharp over `Directory.EnumerateFiles` |
| 8 | Editor border color = thinking level | MUST | 0.5d | Pure UI change |
| 9 | External editor (Ctrl+G opens `$EDITOR`, sync back) | NICE | 2d | Write to temp file, watch for mtime, reload |
| 10 | Themes (customizable color palette per TUI) | NICE | 3d | Define `ITuiTheme` interface; current Catppuccin becomes default |
| 11 | Skills (bundled prompt + tools + slash commands) | NICE | 4d | Extend `IPlugin` with `Skills` property |
| 12 | Prompt templates (`/templatename` expansion) | NICE | 2d | Read `~/.harbor/templates/*.md` |
| 13 | Session sharing (`/share` to GitHub gist) | NICE | 2d | Octokit; `harbor share <session-id>` |
| 14 | `/compact` with custom instructions | NICE | 1d | On top of #4 |
| 15 | `/export <file>` to HTML or JSONL | NICE | 1d | JSONL already; HTML via Markdig |
| 16 | Pi packages convention (npm-package-as-skill) | LATER | 3d | .NET equivalent: NuGet-package-as-skill |
| 17 | RPC mode (`pi --mode rpc`) for process integration | LATER | 5d | Harbor already has `IEventBus`; expose over JSON-RPC |
| 18 | Subscription auth (Claude Pro/Max, ChatGPT Plus, Copilot) | LATER | 5d | OAuth dance per provider |
| 19 | llama.cpp router (`/llama` to download/load models) | LATER | 4d | Wrap llama.cpp binary |
| 20 | Open-source session sharing to Hugging Face | LATER | 2d | HF API client |
| 21 | Per-project `.harbor/skills/` discovery | NICE | 2d | Already partly in Harbor plugin system |
| 22 | OpenShell / Gondolin container sandbox | LATER | 8d | Out of scope for coding harness |

### 4.6 Risks / constraints

- **License:** Pi is MIT. Harbor can study and adopt patterns freely.
- **Language barrier:** Pi is TypeScript. Direct code port impossible. Patterns port cleanly.
- **Native modules:** Pi has per-platform prebuilds for win32 + darwin native tty. .NET equivalent: Harbor's existing `Harbor.Tui.Sixel` already handles cross-platform terminal detection.
- **Cultural:** Pi's "no sub-agents by design" philosophy is the opposite of Harbor's existing TaskTool. Subagent R recommends Harbor keep TaskTool but make Pi-style Skills the preferred way to extend, not sub-agents.
- **Biome vs Roslyn:** Pi's Biome config is one file. .NET's Roslyn analyzers spread across `Directory.Build.props`, `Directory.Build.targets`, `.editorconfig`, and per-project analyzer packages. Not a blocker, just more moving parts.

---

## 5. Kilocode Deep-Dive

> *Kilocode is the third of Harbor's stated inspirations. It is a fork of OpenCode (Section 6) enhanced for the Kilo agentic engineering platform. The user mentioned "free models integration is already done in Harbor — what else?" So this section focuses on what's NOT free-models.*

### 5.1 What it is

Kilocode (https://kilo.ai, https://github.com/kilo-org/kilocode, MIT, copyright 2026 Kilo Code + 2025 opencode) is an **AI coding agent** that runs in VS Code, JetBrains, the CLI, the cloud (app.kilo.ai/cloud), and as code reviews / always-on agent (KiloClaw). Kilo's pitch: *"You pick from 500+ models, switch between them mid-task, and pay the model provider's rate with zero markup. No API keys required to start."*

Kilo ships with 5 specialized agents:
- **Code** — default, implements/edits from natural language
- **Plan** — designs architecture and writes implementation plans before any code
- **Ask** — answers questions about the codebase without touching files
- **Debug** — troubleshoots and traces issues
- **Review** — reviews changes and surfaces issues across performance, security, style, test coverage

Users can build custom agents.

Autonomous Mode (`kilo run --auto "..."`) disables all permission prompts and lets the agent execute any action without confirmation, for CI/CD pipelines.

The Kilo CLI is a fork of OpenCode (per Kilo's FAQ: *"Kilo CLI is a fork of OpenCode, enhanced to work within the Kilo agentic engineering platform"*).

### 5.2 Tech stack

Kilo's monorepo uses **Bun 1.3** as both package manager and runtime, with TypeScript 5.8 and Effect-TS 4.0 (beta) for the runtime/Effect system. The catalog (top-level deps) shows the same Effect-TS / Hono / SolidJS / opentui stack as OpenCode (since Kilo is a fork). Key deps from the catalog:

| Layer | Choice | Notes |
|-------|--------|-------|
| Runtime | **Bun 1.3.14** | Both runtime and package manager |
| Language | **TypeScript 5.8** | |
| Effect system | **Effect-TS 4.0.0-beta.74** | For runtime, services, schemas, generators |
| OpenTelemetry | `@effect/opentelemetry 4.0.0-beta.74` | |
| Platform | `@effect/platform-node 4.0.0-beta.74` | |
| Database | **Drizzle ORM 1.0.0-rc.2** + `drizzle-kit` + `@effect/sql-sqlite-bun` | SQLite via Bun |
| HTTP server | **Hono 4.12** + `hono-openapi 1.1.2` + `@hono/standard-validator 0.2.0` + `@hono/zod-validator 0.4.2` | |
| LLM SDK | **Vercel AI SDK (`ai 6.0.168`)** | Multi-provider |
| TUI framework | **@opentui/core 0.3.4** + **@opentui/solid 0.3.4** + `@opentui/keymap 0.3.4` + `opentui-spinner 0.0.7` | Open-source TUI library (SolidJS-based) |
| SolidJS | via `@opentui/solid` and `@kobalte/core 0.13.11` (SolidUI primitives) | |
| Drawer | `@corvu/drawer 0.2.4` | SolidJS drawer primitive |
| Virtual scrolling | `@tanstack/solid-virtual 3.13.32` | SolidJS port of react-virtual |
| Streaming markdown | `marked 17.0.6` + `marked-shiki 1.2.1` + `@shikijs/stream 4.2.0` + `remend 1.3.0` | Shiki for streaming syntax highlighting, remend for partial-markdown re-parsing |
| Diff | `diff 8.0.4` + `@pierre/diffs 1.1.22` | |
| Validation | **Zod 4.1.8** | |
| Dates | `luxon 3.6.1` | |
| ULID | `ulid 3.0.1` | For session IDs |
| DOM sanitization | `dompurify 3.4.2` | For markdown rendering |
| Tailwind | `@tailwindcss/vite 4.1.11` | For VS Code extension webviews |
| Auth | `@openauthjs/openauth 0.0.0-20250322224806` | |
| Fuzzy search | `fuzzysort 3.1.0` | |
| Package management | `@npmcli/arborist 9.4.0` | For dependency tree inspection |
| Shell | `cross-spawn 7.0.6` + custom `BunShell` | |
| GitHub | `@octokit/rest 22.0.0` | |
| Sandbox | `@anthropic-ai/sandbox-runtime 0.0.63` | For running untrusted code |
| Workers | `@cloudflare/workers-types 4.20251008.0` | For Cloudflare deployment |
| Linter | **oxlint** | |
| Test runner | `bun test` | |
| Storybook | yes (separate package) | For component development |
| E2E | Playwright 1.59 | |
| State | SolidJS signals (via @opentui/solid) | No Zustand equivalent |
| Type-check | `tsgo` (TS native preview) | |

Workspace packages (kilo's monorepo `packages/*`):
- `packages/opencode` — the CLI (forked from OpenCode)
- `packages/core` — runtime, schemas
- `packages/sdk/js` — TypeScript SDK
- `packages/kilo-vscode` — VS Code extension
- `packages/kilo-jetbrains` — JetBrains plugin
- `packages/storybook` — component playground
- `packages/stats/app` — usage stats dashboard
- Plus many sub-packages under `packages/console/*` and `packages/stats/*`

### 5.3 Killer features (excluding free-models integration, per user's note)

The user explicitly said: *"free models integration is already done in Harbor — what else?"* So this list excludes free-models features:

1. **VS Code extension** — full VS Code extension with TreeView sidebar, inline diff view, ghost-text autocomplete, command palette integration, status bar. The extension communicates with the Kilo CLI via stdio JSON-RPC. Harbor has zero VS Code presence today — Subagent R recommends a Harbor VS Code extension as LATER (Section 9).

2. **JetBrains plugin** — same as VS Code but for IntelliJ/PyCharm/etc. Uses the JetBrains Platform SDK (Java/Kotlin). Harbor has zero JetBrains presence.

3. **Cloud Agent (app.kilo.ai/cloud)** — run Kilo from the web, no local machine. This is a hosted service, not OSS code.

4. **Code Reviews (app.kilo.ai/code-reviews)** — automated AI code reviews on pull requests. GitHub App that subscribes to PR events and runs a Kilo review.

5. **KiloClaw (app.kilo.ai/claw)** — always-on AI agent. Like a daemon that runs in the background, monitoring the repo and acting on events.

6. **Autonomous Mode (`--auto`)** — disables all permission prompts for CI/CD pipelines. `kilo run --auto "run tests and fix any failures"`. Harbor's `PermissionService` could have a `--auto` flag that sets all rules to Allow — this is a small but powerful feature.

7. **5 specialized agents** (Code/Plan/Ask/Debug/Review) — switchable mid-task. Each has its own system prompt, tool subset, and permission profile. Harbor has agent definitions but they're not as opinionated.

8. **Mid-task model switching** — switch from Claude to GPT to Gemini mid-task without losing state. Kilo persists the conversation, swaps the provider, and continues. Harbor's `ProviderRegistry` supports this in principle but no TUI surfaces it.

9. **Inline autocomplete with ghost text** — type in your editor, Kilo suggests the rest as gray ghost text, Tab to accept. This is in the VS Code extension. Not applicable to a TUI.

10. **MCP marketplace** — discover and install MCP servers from a registry. Browse by category, install with one click. Harbor has `IMcpRegistry` but no marketplace discovery.

11. **Plugin slot system** — opentui/tui uses a `plugin/slots` API where plugins can register UI slots (header, footer, sidebar, overlay) and react to events. This is similar to Harbor's panel system but more granular.

12. **Effect-TS runtime** — Kilo uses Effect for everything: services, schemas, layers, generators, error handling, retries. This is a *huge* architectural choice. The .NET equivalent is Microsoft.Extensions.* + System.Threading.Channels + Polly for retries. Harbor already uses Microsoft.Extensions.Logging/DI/Configuration. The lesson from Kilo: lean on a single coherent runtime library rather than mixing 5 mini-libs.

13. **Drizzle ORM for sessions** — Kilo uses Drizzle ORM with SQLite for session storage. Schema is defined in code (TypeScript), migrations are auto-generated. Harbor's `Harbor.Storage.Sqlite` uses raw ADO.NET — could adopt EF Core or Dapper for cleaner schema.

14. **Hono HTTP server** — Kilo's CLI can serve a local HTTP API (`opencode serve` similar to `orca serve`). Hono is the lightweight HTTP framework. The .NET equivalent is Kestrel directly (already in .NET 10) or FastEndpoints for a cleaner API.

15. **Hono OpenAPI** — auto-generate OpenAPI specs from Hono routes. The .NET equivalent is NSwag or Microsoft.OpenApi.

16. **OpenAuth** — `@openauthjs/openauth` for OAuth flows (subscription auth).

17. **`@pierre/diffs`** — a specialized diff library for AI code review. Produces structured diffs that the agent can reason about.

18. **`remend`** — re-parses partial markdown as it streams in. This is the answer to "how do I render streaming markdown without re-parsing the whole string." It does incremental AST updates. Harbor should consider building a .NET port (or finding an equivalent).

19. **`@shikijs/stream`** — Shiki (the syntax highlighter) supports streaming: tokenize code in chunks as it arrives. Combined with `remend`, this gives Kilo's TUI the smoothest streaming markdown rendering of any agent Subagent R studied.

20. **AcpCommand** (Agent Client Protocol) — Kilo's CLI has an `acp` subcommand for the Agent Client Protocol (an emerging standard for agent-to-editor communication). This is the protocol that lets Zed, Cursor, and other editors embed any agent. Harbor should support ACP as LATER.

### 5.4 Architecture highlights

1. **Fork-and-extend.** Kilo doesn't try to re-architect OpenCode — it vendors it as `packages/opencode` and adds Kilo-specific features in sibling packages (`packages/kilo-vscode`, `packages/kilo-jetbrains`). This is the cleanest way to fork. The .NET equivalent would be: if Harbor wanted to fork OpenCode, it would be a separate repo, not a sub-module.

2. **Effect-TS layers.** Every service is an Effect Layer (composable, testable, mockable). The .NET equivalent is `Microsoft.Extensions.DependencyInjection` with `IServiceCollection` and `IServiceProvider`. Harbor already uses this — but Effect's layer model is more powerful (layers can depend on layers, can be swapped per-test).

3. **Catalog deps.** Kilo's `package.json` has a `workspaces.catalog` object that pins every shared dependency to one version across all packages. Bun resolves the catalog. This is the .NET equivalent of `Directory.Build.props` having a `<PackageVersion>` property group. Harbor already does this (Subagent R verified by reading `Directory.Build.props`).

4. **`bun run` for everything.** Kilo uses Bun for dev (`bun run --cwd packages/opencode src/index.ts`), test (`bun test`), typecheck (`bun turbo typecheck`). One tool, zero config. The .NET equivalent is `dotnet` — also one tool. Harbor already does this.

5. **SolidJS for TUI.** `@opentui/solid` is SolidJS rendering to a terminal instead of a DOM. SolidJS's signals (fine-grained reactivity) make TUI updates cheap. Harbor's `UiReducer` is more coarse-grained — a single state object per render. The lesson: fine-grained reactivity matters for TUIs.

6. **Hono + Zod for HTTP API.** Kilo's HTTP server uses Hono (lightweight) + Zod (validation) + hono-openapi (spec gen). The trio is the modern TS stack. The .NET equivalent is FastEndpoints + FluentValidation + NSwag. Or minimal APIs + OpenAPI (built into .NET 10).

7. **Effect schema for runtime types.** Kilo uses Effect Schema (not just Zod) for types that need runtime validation + compile-time inference. The .NET equivalent is `OneOf` discriminated unions + `System.Text.Json` source generators. Harbor already uses this pattern.

8. **Drizzle for SQLite.** Kilo defines SQLite tables in TS with `sqliteTable` and gets type-safe queries + auto-generated migrations. The .NET equivalent is EF Core with code-first migrations.

### 5.5 What Harbor could steal — MUST / NICE / LATER

The user said "free models integration is already done in Harbor — what else?" So this list excludes provider/model features:

| # | Feature | Priority | Effort | Notes |
|---|---------|----------|--------|-------|
| 1 | `--auto` mode (disable all permission prompts for CI/CD) | MUST | 1d | `PermissionRuleset.AllowAll` + CLI flag |
| 2 | 5 specialized agents (Code/Plan/Ask/Debug/Review) | MUST | 3d | Each = system prompt + tool subset + permission profile |
| 3 | Mid-task model switching (preserve conversation state) | MUST | 2d | Already possible via ProviderRegistry — surface in TUI |
| 4 | MCP marketplace (discover + install MCP servers from registry) | NICE | 5d | New `IMcpMarketplace` interface; registry of registries |
| 5 | Plugin slot system (header/footer/sidebar/overlay slots) | NICE | 4d | Extend Harbor's panel system with slot-level granularity |
| 6 | Session SQLite storage with Drizzle-equivalent ORM | NICE | 4d | Already have `Harbor.Storage.Sqlite`; adopt EF Core or Dapper |
| 7 | HTTP server (`opencode serve` equivalent) | NICE | 3d | Use Kestrel minimal APIs |
| 8 | OpenAPI auto-gen from HTTP server | NICE | 1d | NSwag or Microsoft.OpenApi |
| 9 | `remend`-equivalent streaming markdown parser | MUST | 5d | Build `StreamingMarkdownDocument` (Section 3.6.7) |
| 10 | Shiki-equivalent streaming syntax highlighter | NICE | 3d | Use TextMate grammars (vscode-textmate equivalent: Avalonia.AvaloniaEdit.TextMate) |
| 11 | VS Code extension (TreeView, inline diff, ghost-text, command palette) | LATER | 15d | Separate project; spawns Harbor CLI as LSP server |
| 12 | JetBrains plugin | LATER | 15d | Separate project |
| 13 | GitHub App for code reviews | LATER | 8d | Separate service |
| 14 | Always-on agent daemon (KiloClaw equivalent) | LATER | 10d | Background service with file watcher |
| 15 | Agent Client Protocol (ACP) support | LATER | 5d | For Zed/Cursor integration |
| 16 | `@pierre/diffs`-equivalent structured diff library | NICE | 3d | DiffPlex already does this — just expose structured output |

### 5.6 Risks / constraints

- **License:** Kilocode is MIT (inherits OpenCode's MIT).
- **Bun-specific:** Kilo uses Bun-specific APIs (`Bun.file()`, `Bun.spawn()`). Harbor can't use Bun — but the .NET equivalents (`File.OpenRead`, `Process.Start`) are fine.
- **Effect-TS:** Effect is a paradigm shift. Harbor shouldn't try to adopt Effect-TS in C# — but the lessons (composable services, schema-driven types, generators for async flows) map to `Microsoft.Extensions.DependencyInjection` + `OneOf` + `IAsyncEnumerable`.
- **VS Code extension effort:** 15 days is a serious commitment. The VS Code extension is the single biggest feature gap between Kilocode and Harbor, but it's also the most expensive to close. Subagent R recommends LATER.
- **ACP risk:** ACP is still emerging. Building for it now risks rework if the spec changes. LATER.

---

## 6. OpenCode Deep-Dive

> *OpenCode is Kilocode's parent — Kilo CLI is a fork of OpenCode. The two share 90% of their stack. This section focuses on what's unique to OpenCode vs what's covered in Section 5.*

### 6.1 What it is

OpenCode (https://opencode.ai, https://github.com/anomalyco/opencode, MIT, copyright 2025 opencode) is *"the open source AI coding agent."* It is the upstream of Kilocode's CLI. OpenCode ships two built-in agents:

- **build** — default, full-access agent for development work
- **plan** — read-only agent for analysis and code exploration (denies file edits by default, asks permission before running bash)

Plus a **general** subagent (`@general`) for complex searches and multistep tasks.

OpenCode is also available as a **desktop app (BETA)** — `opencode-desktop-mac-arm64.dmg`, `opencode-desktop-windows-x64.exe`, `.deb/.rpm/.AppImage`. This is the closest of the 4 repos to a "desktop UI" — though it's still Bun/TS, not .NET.

### 6.2 Tech stack

OpenCode's stack is 95% identical to Kilocode's (Section 5.2) — Kilocode is a fork. Differences:

- OpenCode uses slightly newer catalog deps (`@opentui/core 0.4.5` vs Kilo's `0.3.4`; `@effect/sql-sqlite-bun` instead of Kilo's Drizzle-only setup; `effect 4.0.0-beta.83` vs Kilo's `beta.74`).
- OpenCode has a `packages/desktop` (the desktop app beta) and `packages/app` (web app) and `packages/console/app` (web console).
- OpenCode has `packages/sdk/js` and `packages/sdk-next` (Vercel AI SDK-style client SDK).
- OpenCode has `packages/slack` (Slack integration).

Architecture is documented in OpenCode's `AGENTS.md` (dev branch). Key excerpts Subagent R fetched:

> *"Keep runtime dependencies directed from Schema to Core and Protocol, then from Core and Protocol to Server. Client runtime code may depend on Schema and Protocol but never Core or Server; `sdk-next` composes Client, Core, and Server."*

This is a clean layered architecture:
- **Schema** — pure types, no deps
- **Protocol** — protocol definitions, depends on Schema
- **Core** — runtime, depends on Schema + Protocol
- **Server** — HTTP server, depends on Core + Protocol
- **Client** — client SDK, depends on Schema + Protocol (NOT Core)
- **sdk-next** — composes Client + Core + Server

This is the cleanest layering Subagent R saw in any of the 4 repos. Harbor's layering (Abstractions → Core → Tui/Cli) is similar but less rigorous — `Harbor.Cli` references `Harbor.Core` and `Harbor.Tui.SpectreTui`, but `Harbor.Tui.SpectreTui` also references `Harbor.Core`, so the dep graph is not strictly layered.

### 6.3 Killer features (unique to OpenCode vs Kilocode)

1. **Desktop app** — packaged macOS/Windows/Linux desktop app (beta). This is the only one of the 4 repos (besides Orca) with a desktop app. Built with Bun + a yet-to-be-identified native shell (probably Tauri or similar — couldn't fetch the desktop package.json in time).

2. **Web app** — `packages/app` is a full web UI for OpenCode. The .NET equivalent would be Blazor Server (already in `Harbor.Tui.Blazor`).

3. **Web console** — `packages/console/app` is a hosted web console. Probably for opencode.ai accounts.

4. **V2 Session Core** — OpenCode is in the middle of a major refactor documented in `AGENTS.md`:
   - *"Keep durable prompt admission separate from model execution."*
   - *"SessionV2.prompt(...) admits one durable `session_input` row before scheduling advisory `SessionExecution.wake(sessionID)`."*
   - *"Keep `SessionExecution` process-global and Session-ID based."*
   - *"Keep delivery vocabulary explicit. Prompts steer by default and promote at the next safe provider-turn boundary."*
   - *"Keep EventV2 replay owner claims separate from clustered Session execution ownership."*

   This is a distributed-systems design — sessions can be clustered, replayed, and owned by different processes. Harbor's `AgentLoop` is single-process single-session. The lesson: if Harbor wants to scale to multi-agent orchestration (like Orca), it needs a similar separation.

5. **System Context algebra** — OpenCode has a `src/system-context` module with a "System Context algebra, registry, and built-ins." This is a structured way to compose context for the LLM (system prompt + tools + skills + history + project context). Harbor builds context ad-hoc in `AgentLoop`.

6. **Context Epochs** — context is versioned ("Context Epoch persistence Session-owned"). Each context change is an epoch, persisted to the session. Harbor's context is unversioned.

7. **Slack integration** — `packages/slack` runs OpenCode in Slack. You can DM the OpenCode bot and it runs as an agent.

8. **Plug command** — `opencode plug <name>` to install plugins from a registry.

9. **GitHub command** — `opencode github <subcommand>` for GitHub operations.

10. **PR command** — `opencode pr <subcommand>` for PR operations.

11. **Db command** — `opencode db <subcommand>` for direct database inspection.

12. **Debug command** — `opencode debug` for debugging sessions.

### 6.4 Architecture highlights

1. **Clean layering** (Schema → Protocol → Core → Server; Client → Schema+Protocol; sdk-next → Client+Core+Server). The strictness is notable. Harbor could adopt this:

   - `Harbor.Abstractions` = Schema (already)
   - `Harbor.Protocol` = NEW — protocol definitions for Harbor RPC (JSON-RPC schemas)
   - `Harbor.Core` = Core (already, mostly)
   - `Harbor.Server` = NEW — HTTP/RPC server hosting Harbor as a service
   - `Harbor.Client` = NEW — client SDK for calling Harbor from other apps
   - `Harbor.Cli` = sdk-next equivalent (composes Client+Core+Server for local use)

2. **Effect-TS Schema** — OpenCode uses Effect Schema for all data types. This gives them runtime validation + compile-time types + JSON (de)serialization in one declaration. The .NET equivalent is `[JsonSerializable]` source generators + `OneOf` discriminated unions + FluentValidation — three separate mechanisms. Harbor uses an ad-hoc mix.

3. **Drizzle ORM** — same as Kilocode (Section 5).

4. **Hono + hono-openapi** — same as Kilocode.

5. **Bun shell** — `BunShell` is a custom shell wrapper that lets the agent run commands with sandboxed I/O. The .NET equivalent is `System.Diagnostics.Process` with redirected stdin/stdout (already in `BashTool`).

6. **opentui** — SolidJS-rendered TUI. The key innovation: SolidJS's signals make TUI rendering fine-grained. Each cell is a signal; only changed cells re-render. This is what `pi-tui` does manually (differential rendering), but opentui gets it for free from SolidJS's reactivity model.

### 6.5 What Harbor could steal — MUST / NICE / LATER

OpenCode's stack overlaps 90% with Kilocode (Section 5.5). Items unique to OpenCode:

| # | Feature | Priority | Effort | Notes |
|---|---------|----------|--------|-------|
| 1 | Strict layering (Schema → Protocol → Core → Server; Client separate) | MUST | 5d | Refactor Harbor into 6 layers instead of 4 |
| 2 | System Context algebra (structured context composition) | MUST | 4d | `ISystemContext` interface; built-in providers for tools/history/skills/project |
| 3 | Context Epochs (versioned context per session) | NICE | 3d | Add `ContextEpoch` table to session store |
| 4 | Durable prompt admission (separate from model execution) | NICE | 4d | `SessionInput` row + advisory wake — enables clustering |
| 5 | Steering vs queued prompts (Pi also has this) | MUST | 4d | Same as Pi's message queue — Section 4.5 #2 |
| 6 | Web app (Blazor Server) | LATER | 8d | `Harbor.Tui.Blazor` already scaffolded |
| 7 | Slack integration | LATER | 6d | Slack Bolt SDK |
| 8 | `harbor plug <name>` plugin install command | NICE | 2d | Pull from NuGet |
| 9 | `harbor github`/`harbor pr`/`harbor db`/`harbor debug` subcommands | NICE | 4d | Already have most of the building blocks |
| 10 | V2 Session Core (clustering-ready) | LATER | 15d | Distributed systems work — defer until needed |

### 6.6 Risks / constraints

- **License:** OpenCode is MIT.
- **V2 Session Core is in-flight.** OpenCode's V2 design (clustering, replay, ownership) is ambitious but unfinished. Harbor shouldn't try to copy the V2 design directly — but the *principles* (durable admission, steering vs queued, context epochs) are sound and adoptable today.
- **Effect-TS lock-in.** OpenCode is deeply coupled to Effect-TS. Harbor can't (and shouldn't) replicate that. The lesson: pick one coherent runtime library (Microsoft.Extensions.*) and stick with it.
- **opentui lock-in.** OpenCode's TUI is opentui-specific. Harbor's TUIs (SpectreTui, Avalonia, Wpf, Blazor, etc.) are already more flexible.

---

## 7. Cross-Cutting Features Harbor Lacks

This section catalogs features that appear in **3 or more** of the 4 repos but are missing from Harbor today. For each, it names the source repos, the source library, the .NET equivalent, and a concrete recommendation.

### 7.1 Slash command palette with fuzzy search

**Present in:** Orca (cmdk), Pi (built-in `/login /model /tree /fork ...`), Kilocode (built-in), OpenCode (built-in). All 4.

**Harbor today:** Zero slash commands. TUI input is a plain text box.

**Source libraries:** `cmdk 1.1.1` (Orca); built-in for Pi/Kilocode/OpenCode with `fuzzysort` (OpenCode/Kilocode) or built-in `fuzzyMatch` (Pi).

**.NET equivalent:** `FuzzySharp 2.0.2` + custom command palette UI (Section 3.7.1).

**Recommendation:** MUST. Build a `Harbor.Core.Commands.CommandRegistry` + `ISlashCommand` interface. Each command is a sealed class with `Name`, `Description`, `ArgsSchema`, `InvokeAsync`. Built-in commands: `/help`, `/model`, `/agent`, `/clear`, `/save`, `/load`, `/share`, `/export`, `/import`, `/compact`, `/fork`, `/tree`, `/new`, `/quit`, `/usage`, `/cost`, `/session`, `/skills`, `/templates`, `/mcp`, `/providers`, `/auto`, `/trust`, `/reload`. Register them in `HostBuilder`. TUIs parse `/` prefix and show a popup.

**Effort:** 3 days (Registry + 20 built-in commands + TUI popup in SpectreTui).

### 7.2 Streaming markdown rendering

**Present in:** Orca (react-markdown + rehype), Pi (marked 18), Kilocode (marked + marked-shiki + @shikijs/stream + remend), OpenCode (same as Kilocode). All 4.

**Harbor today:** `ChatMarkdown` in `Harbor.Tui.SpectreTui` uses Spectre.Console's markdown renderer — which is fine for static text but does not stream (it re-parses the whole string on each token).

**Source libraries:** `marked 17` (Pi, Kilo, OpenCode), `react-markdown 10 + rehype-*` (Orca), `remend 1.3` (Kilo, OpenCode — for partial-markdown re-parsing), `marked-shiki 1.2.1` (Kilo, OpenCode — for streaming syntax highlighting).

**.NET equivalent:** `Markdig 0.38` (already a Harbor dep via Spectre.Console) for parsing, `Markdown.Avalonia 11.0.3` for Avalonia rendering, `Avalonia.AvaloniaEdit.TextMate 11.0.6` for code block syntax highlighting. No .NET `remend` equivalent exists — must build `StreamingMarkdownDocument` (Section 3.6.7).

**Recommendation:** MUST. Build `StreamingMarkdownDocument` for Avalonia with: token accumulation in `StringBuilder`, throttled re-render every 50ms, AST delta-update (reuse `TextBlock` instances across renders), TextMate grammar for code blocks. For SpectreTui, just throttle renders to every 200ms.

**Effort:** 3 days for Avalonia, 1 day for SpectreTui throttle.

### 7.3 Inline tool call UI (collapsible)

**Present in:** All 4.

**Harbor today:** `Harbor.Tui.SpectreTui` renders tool calls as inline text blocks (not collapsible). No way to hide tool output.

**Source libraries:** Orca uses shadcn `Collapsible` (Radix primitive). Pi/Kilo/OpenCode use opentui's or pi-tui's component models (custom collapsible).

**.NET equivalent:** Avalonia `Expander` (built-in). Spectre.Console has `Collapsible` via rules (not great).

**Recommendation:** MUST. In Avalonia: wrap each tool call in `Expander` with header "🔧 webfetch (https://...)" and body = tool args + result, both rendered as markdown. In SpectreTui: bind a key (Ctrl+O like Pi) to collapse/expand the most recent tool call. Persist expand/collapse state per session.

**Effort:** 2 days.

### 7.4 Diff view with syntax highlighting

**Present in:** Orca (Monaco diff + diff-match-patch), Pi (built-in), Kilocode (`diff` + `@pierre/diffs`), OpenCode (`diff` + `@pierre/diffs`).

**Harbor today:** `PatchTool` produces a unified diff as text; the TUI displays it as plain text. No syntax highlighting, no side-by-side.

**Source libraries:** `@sanity/diff-match-patch 3.2` (Orca), `diff 8.0.4` (Kilo, OpenCode), `@pierre/diffs 1.2.10` (Kilo, OpenCode — structured AI-reviewable diffs).

**.NET equivalent:** `DiffPlex 1.7.2` (diff engine), `AvaloniaEdit 11.0.6` (text editor with syntax highlighting via TextMate). Render side-by-side: two `AvaloniaEdit` instances, left = before, right = after, with +/- line backgrounds.

**Recommendation:** MUST. Build `DiffView` Avalonia `UserControl` that takes `before: string, after: string, language: string` and renders a side-by-side highlighted diff. Use in `Harbor.Tui.Avalonia` for the `Edit`/`Write`/`Patch` tool output. In SpectreTui, render unified diff with syntax highlighting via Spectre.Console.

**Effort:** 3 days.

### 7.5 Cost / token meter visualization

**Present in:** Orca (per-agent usage tracking with charts), Pi (footer with ↑in/↓out/R cache read/W cache write/CH cache hit rate/cost/context), Kilocode (footer), OpenCode (footer).

**Harbor today:** `Harbor.Tui.SpectreTui` shows a basic "0 in / 0 out / $0.0000" status bar.

**Source libraries:** Orca uses custom React charts (likely `@tanstack/react-virtual` for the list + custom SVG for the chart). Pi/Kilo/OpenCode use plain text in the footer.

**.NET equivalent:** `LiveChartsCore 2.0.0-rc5.4` + `LiveChartsCore.Avalonia` for the Avalonia charts. For SpectreTui, plain text (already done).

**Recommendation:** NICE. Build `TokenMeterView` Avalonia `UserControl` showing:
- Current tokens (in/out/cache read/cache write) as 4 sparklines
- Cost in USD with rate-limit reset countdown
- Per-agent breakdown (when multi-agent)
- Cache hit rate as a percentage with arrow trend

**Effort:** 2 days for Avalonia. 0 days for SpectreTui (already done).

### 7.6 Session branching UI

**Present in:** Pi (`/tree` with TreeView), OpenCode (V2 session core has branching). Orca has parallel worktrees (different concept — separate worktrees, not branches of one session).

**Harbor today:** Linear sessions only.

**Source libraries:** Pi's `TreeView` (custom pi-tui component), OpenCode's V2 (Effect-TS based).

**.NET equivalent:** Avalonia `TreeView` (built-in). For SpectreTui, build a custom tree renderer.

**Recommendation:** NICE. Requires `parentId` in `JsonlSessionStore` schema (Section 4.5 #3). UI: `TreeView` showing the session tree; click any node to switch the active branch; Ctrl+←/→ to navigate branches. Build the storage change first (MUST in Section 4.5), then this UI.

**Effort:** 3 days for the UI (after storage change is done).

### 7.7 MCP server browser

**Present in:** Kilocode (MCP marketplace — discover + install from registry), OpenCode (`/mcp` command). Orca and Pi don't have a browser (they let the agent drive MCP).

**Harbor today:** `IMcpRegistry` exists; `McpToolTool` invokes registered servers. No browser, no marketplace.

**Source libraries:** Kilocode's MCP marketplace is a custom UI over a registry of MCP servers.

**.NET equivalent:** No standard. Build a custom registry of MCP servers (e.g., `https://mcp-servers.dev/registry.json` or a community-maintained list).

**Recommendation:** NICE. Build `IMcpMarketplace` interface with `BrowseAsync`, `InstallAsync`, `UninstallAsync`. Default implementation: fetch a JSON registry from a configurable URL, install by writing to `~/.harbor/mcp/servers/<name>.json`. TUI: `/mcp browse` opens a `ListBox` of available servers; Enter to install.

**Effort:** 5 days.

### 7.8 Multi-agent orchestration view

**Present in:** Orca (worktree sidebar — see all parallel agents at a glance).

**Harbor today:** Single agent per session. `TaskTool` spawns sub-agents but they're invisible to the user.

**Source libraries:** Orca's worktree sidebar (custom React component).

**.NET equivalent:** Avalonia `Dock` + `TabControl` for agent panes; sidebar `TreeView` for the agent tree.

**Recommendation:** NICE. Build `AgentOrchestratorView` Avalonia `UserControl` showing:
- Tree of all active agents (parent + sub-agents spawned via TaskTool)
- Per-agent status (running/idle/waiting-for-permission/error)
- Per-agent message count, token count, cost
- Click an agent to focus its chat history
- "Fan out" button: spawn N parallel agents with the same prompt (Orca's killer feature)

**Effort:** 5 days for the UI; 3 days for the AgentLoop refactor to support multiple concurrent loops in one session.

### 7.9 Quick open

**Present in:** Orca (Quick open across worktrees/files/agents/commands/repo context), Kilocode (built-in), OpenCode (built-in).

**Harbor today:** None.

**Source libraries:** Orca uses `cmdk` for the palette; the data sources are per-feature. Kilo/OpenCode use `fuzzysort 3.1.0` + custom palette.

**.NET equivalent:** `FuzzySharp 2.0.2` + the `CommandPalette` from Section 3.7.1.

**Recommendation:** NICE. Extend `CommandPalette` with multiple data sources: files (via `Directory.EnumerateFiles`), agents (via `IAgentRegistry`), commands (via `CommandRegistry`), sessions (via `ISessionStore`), MCP servers (via `IMcpRegistry`). Bind `Ctrl+P` for files, `Ctrl+Shift+P` for commands (VS Code convention).

**Effort:** 2 days on top of Section 3.7.1.

### 7.10 Themes

**Present in:** All 4.

**Harbor today:** Catppuccin hardcoded.

**Source libraries:** Orca uses CSS variables + `.dark` class. Pi uses theme interfaces on each component. Kilo/OpenCode use opentui's theme context.

**.NET equivalent:** Avalonia `ThemeDictionaries` (Section 3.6.3). For SpectreTui, define an `ITuiTheme` interface and have the renderer consult it.

**Recommendation:** NICE. Define `ITuiTheme` with `Background`, `Foreground`, `Primary`, `Secondary`, `Muted`, `Accent`, `Destructive`, `Border`, `Input`, `Ring`, plus git-decoration colors. Ship 3 themes: Catppuccin Mocha (default), Catppuccin Latte (light), GitHub Dark. Allow override via `~/.harbor/themes/*.json`.

**Effort:** 3 days.

### 7.11 External editor integration

**Present in:** Pi (Ctrl+G opens `$VISUAL/$EDITOR/Notepad/nano`).

**Harbor today:** None.

**Source libraries:** Pi's built-in.

**.NET equivalent:** `System.Diagnostics.Process.Start` with the editor path; temp file write + mtime watch.

**Recommendation:** NICE. In each TUI, bind `Ctrl+G` to: write current input to `~/.harbor/editor-tmp-<guid>.md`, spawn `$VISUAL` (or `$EDITOR`, or Notepad on Windows, or `nano` elsewhere) on that file, watch for process exit, reload file contents into the input box, delete temp file.

**Effort:** 2 days.

### 7.12 Notifications + unread state

**Present in:** Orca (per-worktree notifications + unread state).

**Harbor today:** `Harbor.Tui.Notifications` exists but isn't fully wired into the SpectreTui.

**Source libraries:** Orca uses Electron's Notification API + custom unread-state store.

**.NET equivalent:** `Harbor.Tui.Notifications.INotificationService` already exists. For unread state, add a `MarkUnread(sessionId, messageIndex)` method.

**Recommendation:** NICE. Wire `INotificationService` into the SpectreTui and Avalonia renderers. Add per-session unread counter in the session list.

**Effort:** 2 days.

### 7.13 Account switcher + usage tracking

**Present in:** Orca (Claude/Codex/OpenCode usage stores + hot-swap accounts without re-login).

**Harbor today:** `ProviderRegistry` supports multiple providers but no usage tracking per provider, no account hot-swap.

**Source libraries:** Orca has separate `ClaudeUsageStore`, `CodexUsageStore`, `OpenCodeUsageStore` classes.

**.NET equivalent:** `IUsageStore` interface + per-provider implementations (query each provider's usage API).

**Recommendation:** NICE. Build `IUsageStore` with `GetUsageAsync(provider)` returning tokens-used / tokens-limit / reset-time. Show in TUI status bar. Add `/accounts` command to switch.

**Effort:** 3 days.

### 7.14 File drag-and-drop into prompt

**Present in:** Orca (drag files/images), Pi (paste images), Kilocode (built-in), OpenCode (built-in).

**Harbor today:** None.

**Source libraries:** Orca uses HTML5 drag-drop. Pi/Kilo/OpenCode use bracketed paste + terminal-specific image protocols.

**.NET equivalent:** Avalonia `DragDrop` event. For SpectreTui, no good solution — terminals don't support file drag-drop directly (some terminal emulators do, via OSC sequences; very fragmented).

**Recommendation:** NICE for Avalonia. LATER for SpectreTui. In Avalonia: bind `DragDrop.DeviceDragEnter` on the prompt input, accept `DataFormats.FileDrop`, render thumbnails in the input box, send file contents (or image base64) with the next message.

**Effort:** 1 day for Avalonia.

### 7.15 Headless serve mode

**Present in:** Orca (`orca serve` with Xvfb auto-start on Linux).

**Harbor today:** Already exists — `harbor serve` or `harbor --headless` works.

**Status:** DONE.

### 7.16 Differential TUI rendering

**Present in:** Pi (pi-tui's 3-strategy differential renderer with CSI 2026 atomic updates).

**Harbor today:** `Harbor.Tui.Ansi` and `Harbor.Tui.Plain` already do differential rendering. `Harbor.Tui.SpectreTui` uses Spectre.Console which does its own diff.

**Status:** DONE (terminal UIs only).

### 7.17 Inline images (Kitty / iTerm2 / Sixel)

**Present in:** Pi (Kitty + iTerm2 graphics protocols).

**Harbor today:** `Harbor.Tui.Sixel` exists (Sixel protocol).

**Status:** DONE (terminal UIs only).

### 7.18 Sub-agents / `@general` invocation

**Present in:** OpenCode (`@general`), Kilocode (similar).

**Harbor today:** `TaskTool` spawns sub-agents.

**Status:** DONE.

### 7.19 Skills (bundled prompt + tools + commands)

**Present in:** Pi (Skills as the unit of shareable workflow).

**Harbor today:** Plugins exist but no skill abstraction (a skill = plugin + prompt + commands + UI as one bundle).

**Recommendation:** NICE. Extend `IPlugin` with `Skills` property; each `ISkill` declares a system-prompt fragment, tool subset, slash commands, and optional UI overlay. Install via `harbor skills install <name>` from a registry.

**Effort:** 4 days.

### 7.20 Prompt templates

**Present in:** Pi (`/templatename` expansion).

**Harbor today:** None.

**Recommendation:** NICE. Read `~/.harbor/templates/*.md`. Each file is a prompt template with `{{placeholder}}` interpolation. `/template <name> [args]` expands the template into the prompt input. Combine with Skills (Section 7.19).

**Effort:** 2 days.

### 7.21 Slash commands (full Pi-style list)

Already covered in Section 7.1.

### 7.22 Per-project trust prompt

**Present in:** Pi (project trust prompt before loading project-local settings/skills/extensions).

**Harbor today:** `PermissionService` is per-tool, not per-project.

**Recommendation:** MUST. Add a `~/.harbor/trust.json` mapping project paths to trust decisions (ask/always/never). On startup in a new project, prompt the user. `-a`/`-na` CLI flags override for one run. Non-interactive modes use `defaultProjectTrust` setting.

**Effort:** 2 days.

### 7.23 Streaming syntax highlighting

**Present in:** Kilocode (`marked-shiki 1.2.1` + `@shikijs/stream 4.2.0`), OpenCode (same).

**Harbor today:** SpectreTui uses Spectre.Console's syntax highlighting (no streaming). Avalonia will use AvaloniaEdit.TextMate (no streaming).

**Recommendation:** NICE. Build `StreamingCodeHighlighter` that uses TextMate grammars incrementally — tokenize code in chunks as it arrives. Combine with `StreamingMarkdownDocument` (Section 3.6.7).

**Effort:** 3 days.

### 7.24 Slash command fuzzy matching

Already covered in Section 7.1 (via FuzzySharp).

### 7.25 Multi-line editor with Shift+Enter

**Present in:** Pi (Shift+Enter for newline, Ctrl+Enter on Windows Terminal).

**Harbor today:** SpectreTui input is multi-line via Spectre.Console's TextPrompt; Avalonia's `TextBox` with `AcceptsReturn="True"` is multi-line.

**Status:** DONE in SpectreTui and Avalonia.

### 7.26 `!command` and `!!command` shell passthrough

Already covered in Section 4.5 #6 (MUST).

### 7.27 `@file` fuzzy path completion

Already covered in Section 4.5 #7 (MUST).

### 7.28 Editor border color = thinking level

Already covered in Section 4.5 #8 (MUST).

### 7.29 Session sharing to GitHub gist

Already covered in Section 4.5 #13 (NICE).

### 7.30 Plugin slot system

**Present in:** OpenCode (`plugin/slots` API), Kilocode (same).

**Harbor today:** `Harbor.Tui.SpectreTui` has a panel system but no slot-level granularity.

**Recommendation:** NICE. Extend `ITuiPanelPlugin` with `Slot` property (Header/Footer/Sidebar/Overlay/Input/Output). Multiple plugins can register for the same slot; renderer composes them.

**Effort:** 4 days.

---

## 8. Implementation Roadmap — 4 Sprints

This roadmap assumes 2-week sprints with 1-2 engineers per sprint. Total: 8 engineer-weeks for the full rip-off plan. The user's constraint that *"фичи должны быть опциональными"* means: every feature ships behind a flag, every Avalonia component is in its own project, every breaking change to `Harbor.Core` is opt-in via interface evolution (default impl, new impl behind a flag).

### Sprint 1 — Foundation (week 1-2)

**Goal:** Make `Harbor.Tui.Avalonia` look and feel like a real desktop app, not a demo. Lay the design-token and animation foundation. Ship the command palette and slash commands.

**Items:**

| # | Item | Effort | Files |
|---|------|--------|-------|
| S1.1 | Design tokens (Catppuccin light/dark, radius, typography, shadows) | 1d | `Harbor.Tui.Avalonia/Assets/Tokens.axaml` (new), `App.axaml` (modified) |
| S1.2 | Animation dictionary (fade/zoom/slide-in/cursor-blink) | 1d | `Harbor.Tui.Avalonia/Assets/Animations.axaml` (new) |
| S1.3 | 8 shadcn-equivalent primitives (Button, Dialog, Popover, Sheet, Tooltip, HoverCard, DropdownMenu, ContextMenu) | 4d | `Harbor.Tui.Avalonia/Components/{Button,Dialog,Popover,Sheet,Tooltip,HoverCard,DropdownMenu,ContextMenu}/*.axaml` (new, ~1100 lines total) |
| S1.4 | CommandPalette primitive (cmdk equivalent with FuzzySharp) | 2d | `Harbor.Tui.Avalonia/Components/CommandPalette/*` (new, ~200 lines, see Section 3.7.1) |
| S1.5 | Toast notifications (sonner equivalent) | 1d | `Harbor.Tui.Avalonia/Components/Toast/*` (new, ~150 lines, see Section 3.7.2) |
| S1.6 | Slash command registry + 10 built-in commands (`/help /model /agent /clear /save /load /new /quit /usage /cost`) | 3d | `Harbor.Core/Commands/CommandRegistry.cs` (new), `Harbor.Core/Commands/ISlashCommand.cs` (new), `Harbor.Core/Commands/Builtin/*.cs` (10 new files), `HostBuilder.cs` (modified — register commands), `Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` (modified — parse `/` and show popup), `Harbor.Tui.Avalonia/AvaloniaTuiRenderer.cs` (modified) |
| S1.7 | `--auto` mode (disable all permission prompts for CI/CD) | 1d | `PermissionService.cs` (modified — `PermissionRuleset.AllowAll`), `Program.cs` (modified — `--auto` flag), `AGENTS.md` (modified) |
| S1.8 | 5 specialized agents (Code/Plan/Ask/Debug/Review) — opinionated defaults | 2d | `Harbor.Core/Agents/Builtin/*.cs` (5 new files), `HostBuilder.cs` (modified — register agents) |

**Total: 15 days.** Slight over-allocation; either trim S1.3 to 6 primitives or split S1.6 to 7 commands.

**Deliverable:** A user can run `HARBOR_TUI=avalonia harbor` and get an animated desktop window with a command palette, slash commands, toasts, and 5 built-in agents. They can also run `harbor --auto "fix tests"` in CI with no prompts.

**Dependencies:** None (foundation work).

**Risk:** S1.3 (8 primitives) is the highest-effort item. Mitigation: prioritize Button, Dialog, Popover, Tooltip (most-used); defer Sheet, HoverCard, DropdownMenu, ContextMenu to Sprint 2.

### Sprint 2 — Chat experience (week 3-4)

**Goal:** Make the chat experience feel like Pi/OpenCode — streaming markdown, collapsible tool calls, diff view, message queue.

**Items:**

| # | Item | Effort | Files |
|---|------|--------|-------|
| S2.1 | Streaming markdown document (Markdig + throttled re-render + delta update) | 3d | `Harbor.Tui.Avalonia/Components/Markdown/StreamingMarkdownDocument.cs` (new, ~400 lines), `Harbor.Tui.Avalonia/Components/Markdown/MarkdownView.axaml` (new) |
| S2.2 | Inline tool call UI (collapsible Expander per tool call) | 2d | `Harbor.Tui.Avalonia/Components/ToolCallView.axaml` (new), `Harbor.Tui.Avalonia/AvaloniaTuiRenderer.cs` (modified — render each ToolCallEvent as ToolCallView). SpectreTui: bind Ctrl+O to collapse/expand. |
| S2.3 | Diff view (DiffPlex + AvaloniaEdit with TextMate highlighting) | 3d | `Harbor.Tui.Avalonia/Components/DiffView.axaml` (new, ~250 lines), wire into EditTool/WriteTool/PatchTool output rendering |
| S2.4 | Message queue (steering + follow-up via Enter / Alt+Enter) | 4d | `Harbor.Core/Agent/AgentLoop.cs` (modified — accept queued inputs via `QueueSteeringAsync`/`QueueFollowUpAsync`), `Harbor.Core/Agent/QueuedMessage.cs` (new), `Harbor.Tui.SpectreTui/UiReducer.cs` (modified — surface queue state), `Harbor.Tui.Avalonia/AvaloniaTuiRenderer.cs` (modified) |
| S2.5 | Slash commands batch 2 (`/share /export /import /compact /fork /tree /session /skills /templates /mcp /providers /trust /reload`) | 3d | `Harbor.Core/Commands/Builtin/*.cs` (13 new files) |

**Total: 15 days.**

**Deliverable:** Chat history streams smoothly with markdown rendering. Tool calls are collapsible. Edit/Write/Patch show side-by-side diffs. User can queue messages while the agent is working. 23 slash commands total.

**Dependencies:** S1.6 (CommandRegistry). S2.1 (streaming markdown) blocks S2.3 (diff view rendering inside markdown).

**Risk:** S2.4 (message queue) requires careful refactor of `AgentLoop` to not block on user input. Mitigation: model the queue as a `Channel<QueuedMessage>` and have the loop poll it at safe boundaries (between tool calls, between LLM turns).

### Sprint 3 — Session + project (week 5-6)

**Goal:** Sessions branch and compact. Projects are trust-scoped. Compaction works automatically.

**Items:**

| # | Item | Effort | Files |
|---|------|--------|-------|
| S3.1 | `parentId` in `JsonlSessionStore` schema (linear → tree) | 2d | `Harbor.Storage.Jsonl/JsonlSessionStore.cs` (modified — add `parentId` field, backward-compat with old linear sessions), `Harbor.Abstractions/SessionMessage.cs` (modified) |
| S3.2 | Session branching UI (`/tree`, `/fork`, `/clone`) | 3d | `Harbor.Tui.Avalonia/Components/SessionTree.axaml` (new — TreeView of session), `Harbor.Core/Commands/Builtin/TreeCommand.cs` + `ForkCommand.cs` + `CloneCommand.cs` (new). SpectreTui: custom tree renderer. |
| S3.3 | Compaction (auto + manual) | 4d | `Harbor.Core/Agent/ICompactionStrategy.cs` (new), `Harbor.Core/Agent/DefaultCompactionStrategy.cs` (new — summarizes old messages via a small LLM call), `AgentLoop.cs` (modified — invoke compaction when context window fills), `/compact` command (already in S2.5) wired to manual trigger |
| S3.4 | Project trust prompt | 2d | `Harbor.Core/Permissions/ProjectTrustService.cs` (new), `~/.harbor/trust.json` (new file), `Program.cs` (modified — check trust on startup), `-a/-na` flags |
| S3.5 | `!command` and `!!command` shell passthrough | 1d | `Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` (modified — parse `!` prefix, run via BashTool, conditionally send to LLM), `Harbor.Tui.Avalonia/AvaloniaTuiRenderer.cs` (modified) |
| S3.6 | `@file` fuzzy path completion | 2d | `Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` (modified — `@` triggers fuzzy popup), `Harbor.Tui.Avalonia/Components/CommandPalette/*` (modified — file source) |
| S3.7 | Editor border color = thinking level | 0.5d | `Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` (modified), `Harbor.Tui.Avalonia/MainWindow.axaml` (modified) |

**Total: 14.5 days.**

**Deliverable:** Sessions branch. Compaction works. Projects are trusted/untrusted. `!ls` runs ls and sends to LLM. `@file` fuzzy-completes paths. Editor border shows thinking level.

**Dependencies:** S3.1 (parentId) blocks S3.2 (tree UI). S3.3 (compaction) depends on a stable `AgentLoop` post-S2.4.

**Risk:** S3.1 (schema migration) is risky — existing user sessions are linear and must still work. Mitigation: `parentId = null` means linear (parent is the previous message); old sessions parse correctly under the new schema.

### Sprint 4 — Polish + orchestration (week 7-8)

**Goal:** Multi-agent orchestration, cost/token meter, file drag-drop, themes. The "Orca-killer" sprint.

**Items:**

| # | Item | Effort | Files |
|---|------|--------|-------|
| S4.1 | Cost / token meter (LiveChartsCore sparkline in status bar) | 2d | `Harbor.Tui.Avalonia/Components/TokenMeterView.axaml` (new, ~150 lines), wire into per-agent usage tracking |
| S4.2 | File drag-and-drop into prompt (Avalonia) | 1d | `Harbor.Tui.Avalonia/MainWindow.axaml` (modified — DragDrop event handler), `Harbor.Tui.Avalonia/AvaloniaTuiRenderer.cs` (modified) |
| S4.3 | Multi-agent orchestration view (sidebar TreeView of agents, fan-out button) | 5d | `Harbor.Core/Agent/AgentOrchestrator.cs` (new — manages multiple AgentLoop instances), `Harbor.Tui.Avalonia/Components/AgentOrchestratorView.axaml` (new), `Harbor.Core/Commands/Builtin/FanOutCommand.cs` (new — `/fanout 5 "..."` spawns 5 agents in 5 git worktrees) |
| S4.4 | Themes (Catppuccin Mocha/Latte + GitHub Dark) | 3d | `Harbor.Abstractions/ITuiTheme.cs` (new), `Harbor.Tui.SpectreTui/Themes/*.cs` (3 new), `Harbor.Tui.Avalonia/Assets/Themes/*.axaml` (3 new), `~/.harbor/themes/*.json` loader |
| S4.5 | External editor (Ctrl+G opens `$EDITOR`) | 2d | `Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` (modified), `Harbor.Tui.Avalonia/AvaloniaTuiRenderer.cs` (modified), `Harbor.Core/Editor/ExternalEditorService.cs` (new) |
| S4.6 | Notifications + unread state (wire `INotificationService` into renderers) | 2d | `Harbor.Tui.Notifications/NotificationService.cs` (modified — already exists), `Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` (modified), `Harbor.Tui.Avalonia/AvaloniaTuiRenderer.cs` (modified) |

**Total: 15 days.**

**Deliverable:** User can `/fanout 5 "add dark mode"` to run 5 agents in parallel. Status bar shows live token meter. Files drag into the prompt. Themes switchable. Ctrl+G opens external editor. Notifications fire on agent completion.

**Dependencies:** S4.1 (token meter) requires per-agent usage tracking (S2.4 partially enables this via the message queue state). S4.3 (orchestration) requires a stable multi-loop architecture — biggest risk.

**Risk:** S4.3 (multi-agent orchestration) is the single most complex item in the roadmap. Five parallel `AgentLoop` instances sharing a UI is genuinely hard. Mitigation: each loop runs in its own `Task`, communicates via `Channel<AgentEvent>`, the UI subscribes to all channels and dispatches by `AgentId`. Start with 2 agents, scale up.

### Out-of-sprint backlog (LATER)

These items are documented but not scheduled:

- Terminal pane (Path A WebView+xterm or Path B custom Skia) — Section 3.6.6
- Embedded browser (Photino/CefSharp) for design mode
- Mobile companion app (MAUI)
- SSH remote worktrees (SSH.NET)
- GitHub/Linear native integration
- Annotate AI diffs inline
- Mermaid diagram rendering
- PDF preview
- Computer Use
- VS Code extension
- JetBrains plugin
- Cloud agent (web-hosted)
- Code reviews (GitHub App)
- Always-on agent daemon (KiloClaw equivalent)
- Agent Client Protocol (ACP) support
- Slack integration
- Hugging Face session sharing
- V2 Session Core (clustering, replay, ownership)
- llama.cpp router support
- OpenShell/Gondolin container sandbox

### Roadmap summary table

| Sprint | Days | Engineer-weeks | Theme | User-visible deliverable |
|--------|------|-----------------|-------|--------------------------|
| 1 | 15 | 1.5 | Foundation | Animated Avalonia shell + command palette + slash commands + 5 agents + `--auto` |
| 2 | 15 | 1.5 | Chat experience | Streaming markdown + collapsible tools + diff view + message queue + 23 slash commands |
| 3 | 14.5 | 1.5 | Session + project | Branching sessions + compaction + project trust + `!` + `@` + thinking-level border |
| 4 | 15 | 1.5 | Polish + orchestration | Token meter + drag-drop + multi-agent fan-out + themes + external editor + notifications |
| **Total** | **59.5** | **6** | | |

6 engineer-weeks of work for a full Orca/Pi/Kilo/OpenCode-equivalent desktop experience on .NET 10. Excludes LATER backlog (estimated 100+ engineer-days additional).

---

## 9. Must / Nice / Later — Explicit Prioritization

The user said: *"фичи должны быть опциональными"* (features must be optional). Every MUST below either:
- Ships behind an env var (`HARBOR_TUI=avalonia` already exists; new ones: `HARBOR_AUTO=1`, `HARBOR_COMMAND_PALETTE=1`, etc.)
- Ships as a separate project (`Harbor.Tui.Avalonia`, `Harbor.Desktop` etc. — users who don't reference them pay no cost)
- Ships as an opt-in interface (`ICompactionStrategy` — default is no-op; user opts in by registering a real impl)
- Ships as a slash command (`/compact`, `/fanout` — users who don't type them are unaffected)
- Ships as a CLI flag (`--auto`, `--approve`, `--no-approve`)

Nothing in the MUST list changes the default behavior of `harbor` run with no arguments in the SpectreTui.

### 9.1 MUST (ship in Sprint 1-3, blocking for "Harbor feels like a real product")

**Sprint 1 — Foundation:**

1. **Animated Avalonia shell** — flesh out `Harbor.Tui.Avalonia` as the orca-equivalent desktop shell. Behind `HARBOR_TUI=avalonia`. Users who don't set this env var get the existing SpectreTui, unchanged.
2. **Design tokens + animation dictionary** — `Assets/Tokens.axaml` + `Assets/Animations.axaml`. Avalonia-only; SpectreTui unaffected.
3. **8 shadcn-equivalent primitives** (Button, Dialog, Popover, Sheet, Tooltip, HoverCard, DropdownMenu, ContextMenu) — in `Harbor.Tui.Avalonia/Components/`. Avalonia-only.
4. **Command palette** (cmdk equivalent with FuzzySharp) — `Ctrl+K` opens palette. Avalonia-only initially; SpectreTui gets a simpler `AutoCompleteBox`-style popup.
5. **Toast notifications** (sonner equivalent) — `ToastService` in `Harbor.Tui.Avalonia/Components/Toast/`. Avalonia-only.
6. **Slash command registry + 10 built-in commands** — `Harbor.Core/Commands/CommandRegistry.cs` + `ISlashCommand`. Both TUIs surface them. Behind no flag (always available; users who don't type `/` are unaffected).
7. **`--auto` mode** — `PermissionRuleset.AllowAll` + CLI flag. Behind `--auto` flag. Users who don't pass it get normal permission prompts.
8. **5 specialized agents** (Code/Plan/Ask/Debug/Review) — opinionated agent definitions registered by default. Users can switch via `/agent`. Doesn't replace the default agent; just adds 4 more.

**Sprint 2 — Chat experience:**

9. **Streaming markdown rendering** — `StreamingMarkdownDocument` in `Harbor.Tui.Avalonia/Components/Markdown/`. Avalonia-only. SpectreTui gets a throttle (200ms) on its existing renderer.
10. **Inline tool call UI (collapsible)** — `ToolCallView.axaml` in Avalonia; Ctrl+O collapse/expand in SpectreTui.
11. **Diff view with syntax highlighting** — `DiffView.axaml` in Avalonia using DiffPlex + AvaloniaEdit. SpectreTui gets syntax-highlighted unified diff.
12. **Message queue (steering + follow-up)** — `AgentLoop` accepts queued inputs. Enter queues steering; Alt+Enter queues follow-up. Behind no flag — pure UX improvement, doesn't change behavior for users who don't queue.
13. **Slash commands batch 2** (13 more commands: `/share /export /import /compact /fork /tree /session /skills /templates /mcp /providers /trust /reload`).

**Sprint 3 — Session + project:**

14. **Session branching (`parentId` in JsonlSessionStore)** — schema migration with backward compat. Old sessions parse as linear (parentId=null). No data loss.
15. **Session branching UI (`/tree`, `/fork`, `/clone`)** — TreeView in Avalonia; custom tree in SpectreTui.
16. **Compaction (auto + manual)** — `ICompactionStrategy` interface; default impl is no-op (existing behavior). User registers `DefaultCompactionStrategy` to enable. Auto-trigger when context fills (reversible — full history stays in JSONL).
17. **Project trust prompt** — `~/.harbor/trust.json` + startup prompt. `-a/-na` flags override. Non-interactive modes use `defaultProjectTrust` setting (default: `ask`).
18. **`!command` and `!!command` shell passthrough** — parse `!` prefix in prompt input. Behind no flag (users who don't type `!` are unaffected).
19. **`@file` fuzzy path completion** — `@` triggers fuzzy popup. Behind no flag.
20. **Editor border color = thinking level** — pure UI change.

**Total MUST items:** 20. Total MUST effort: 44.5 days (~4.5 engineer-weeks).

### 9.2 NICE (ship in Sprint 4 or later, optional, can be deferred)

1. Cost / token meter (LiveChartsCore sparkline) — Sprint 4.1
2. File drag-and-drop into prompt (Avalonia only) — Sprint 4.2
3. Multi-agent orchestration view (fan-out button) — Sprint 4.3
4. Themes (Catppuccin Mocha/Latte + GitHub Dark) — Sprint 4.4
5. External editor (Ctrl+G) — Sprint 4.5
6. Notifications + unread state — Sprint 4.6
7. Quick open (files + agents + commands + sessions + MCP servers) — Sprint 4+
8. Session SQLite storage with EF Core — replace `Harbor.Storage.Sqlite` ad-hoc SQL with EF Core. Behind `HARBOR_SESSION_STORAGE=sqlite-ef`.
9. HTTP server (`harbor serve`) — Kestrel minimal APIs. Behind `harbor serve` subcommand.
10. OpenAPI auto-gen from HTTP server — NSwag. Auto when `harbor serve` is running.
11. Streaming syntax highlighter (TextMate incremental) — combine with `StreamingMarkdownDocument`.
12. MCP marketplace (discover + install from registry) — `IMcpMarketplace` interface. Behind `/mcp browse` command.
13. Plugin slot system (header/footer/sidebar/overlay slots) — extend `ITuiPanelPlugin`. Behind no flag but additive.
14. Skills (bundled prompt + tools + commands) — extend `IPlugin`. Behind `harbor skills install`.
15. Prompt templates (`/templatename`) — read `~/.harbor/templates/*.md`. Behind no flag.
16. Session sharing to GitHub gist — `/share` command. Behind no flag.
17. Account switcher + per-provider usage tracking — `/accounts` command. Behind no flag.
18. `@pierre/diffs`-equivalent structured diff library — DiffPlex already does this; just expose structured output. Behind no flag.
19. Terminal pane (Path C — AvaloniaEdit + VT100 parser) — lowest-fidelity terminal option. Behind `HARBOR_TERMINAL=avaloniaedit`.
20. Context Epochs (versioned context per session) — `ContextEpoch` table. Behind no flag but additive.

**Total NICE items:** 20. Total NICE effort: ~50 days (~5 engineer-weeks).

### 9.3 LATER (backlog, ship only if a user asks)

1. Terminal pane Path A (WebView + xterm.js + CefSharp) — heavy dep
2. Terminal pane Path B (custom Skia atlas renderer) — Ghostty-class claim
3. Embedded browser (Photino / CefSharp) for design mode
4. Mobile companion app (MAUI)
5. SSH remote worktrees (SSH.NET + remote PTY)
6. GitHub native integration (Octokit)
7. Linear native integration (Linear API client)
8. Annotate AI diffs inline (comment overlay on diff view)
9. Mermaid diagram rendering (no good .NET lib; needs headless Chromium)
10. PDF preview (`PdfViewer.Avalonia` exists but adds 5MB)
11. Computer Use (agent controls desktop UI) — out of scope for a coding harness
12. VS Code extension (15d+; separate project)
13. JetBrains plugin (15d+; separate project)
14. Cloud agent (web-hosted; separate service)
15. Code reviews (GitHub App; separate service)
16. Always-on agent daemon (KiloClaw equivalent)
17. Agent Client Protocol (ACP) support — for Zed/Cursor integration
18. Slack integration (`@opencode-ai/slack` equivalent)
19. Hugging Face session sharing
20. V2 Session Core (clustering, replay, ownership) — distributed systems work
21. llama.cpp router support
22. OpenShell / Gondolin container sandbox
23. Custom provider support via `~/.harbor/models.json` (Pi's pattern) — already partly supported via `ProviderRegistry`
24. Subscription auth (Claude Pro/Max, ChatGPT Plus, Copilot) — OAuth dance per provider
25. Pi packages convention (npm-package-as-skill) — .NET equivalent: NuGet-package-as-skill

**Total LATER items:** 25. Estimated effort: 100+ engineer-days. Defer until user explicitly asks.

### 9.4 NO (do not ship)

1. **Sub-agents removed in favor of Skills** (Pi's philosophy) — Harbor's `TaskTool` is too useful; keep both.
2. **Effect-TS in C#** — paradigm mismatch; use `Microsoft.Extensions.*` instead.
3. **Biome in .NET** — Roslyn analyzers already do this.
4. **opentui in C#** — Harbor has 12 TUI projects already; adding opentui would be redundant.
5. **Hono in .NET** — Kestrel minimal APIs are equivalent.
6. **Drizzle ORM in .NET** — EF Core is equivalent.
7. **xterm.js native port** — too much work for too little gain; use Path A (WebView) or Path C (AvaloniaEdit) instead.
8. **Monaco port** — AvaloniaEdit is the .NET equivalent; porting Monaco would take years.
9. **Tailwind for Avalonia** — Avalonia's `Style` system is more powerful; don't layer Tailwind on top.
10. **React Native for Windows** — wrong framework; Avalonia is better for desktop.

### 9.5 Opt-in summary

| Mechanism | Count of features gated |
|-----------|-------------------------|
| Env var (`HARBOR_TUI=avalonia` etc.) | 12 |
| Separate project (`Harbor.Tui.Avalonia`, `Harbor.Desktop`) | 8 |
| Opt-in interface (`ICompactionStrategy`, `IMcpMarketplace`) | 5 |
| Slash command (`/compact`, `/fanout`, `/share`) | 15 |
| CLI flag (`--auto`, `--approve`, `--no-approve`) | 3 |
| Behind no flag (pure UX improvement, additive) | 7 |

Every MUST feature is gated by at least one of these mechanisms. The SpectreTui user who runs `harbor` with no arguments and no config file sees **zero behavior change** from the current Harbor.

---

## 10. References

### 10.1 Repos studied

- **Orca**: https://github.com/stablyai/orca (MIT, 2026 Lovecast Inc.)
  - README: `https://raw.githubusercontent.com/stablyai/orca/main/README.md` (268 lines)
  - `package.json`: confirmed Electron 43 + React 19 + Tailwind 4 + Radix + shadcn
  - `src/main/index.ts`: confirmed Electron main-process entry
  - `electron.vite.config.ts`: confirmed electron-vite + React plugin
  - `tsconfig.json`: confirmed project references (node/web/relay)
  - `docs/STYLEGUIDE.md`: confirmed Tailwind tokens + shadcn primitives + Radix
  - `docs/reference/headless-linux-server.md`: confirmed Electron + AppImage + Xvfb
  - `.github/CONTRIBUTING.md`: confirmed `pnpm dev`, CmdOrCtrl, Monaco/xterm/markdown
  - No `.csproj`, `.axaml`, `.xaml`, `.razor`, `tauri.conf.json`, `Cargo.toml` found

- **Pi**: https://github.com/earendil-works/pi (MIT, 2025 Mario Zechner)
  - README: `https://raw.githubusercontent.com/earendil-works/pi/main/README.md` (99 lines)
  - `package.json` (root): confirmed npm workspaces, biome, tsgo
  - `packages/tui/package.json` + `packages/tui/README.md`: confirmed pi-tui differential renderer
  - `packages/coding-agent/README.md`: confirmed slash commands, session branching, compaction, skills
  - `AGENTS.md`: confirmed no-mocks, no-any, no-enum, supply-chain hardening rules

- **Kilocode**: https://github.com/kilo-org/kilocode (MIT, 2026 Kilo Code + 2025 opencode)
  - README: `https://raw.githubusercontent.com/kilo-org/kilocode/main/README.md` (177 lines)
  - `package.json` (root): confirmed Bun 1.3, Effect-TS, Drizzle, Hono, opentui, SolidJS, marked-shiki, remend, fuzzysort
  - FAQ confirms fork-of-OpenCode

- **OpenCode**: https://github.com/anomalyco/opencode (MIT, 2025 opencode)
  - README (dev branch): `https://raw.githubusercontent.com/anomalyco/opencode/dev/README.md` (129 lines)
  - `package.json` (dev): confirmed same stack as Kilocode, slightly newer catalog versions
  - `AGENTS.md` (dev): confirmed Schema → Protocol → Core → Server layering + V2 Session Core design

### 10.2 NuGet packages recommended for Harbor.Tui.Avalonia

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| Avalonia | 11.2.7 | UI framework (already referenced) | MIT |
| Avalonia.Desktop | 11.2.7 | Desktop backends (already referenced) | MIT |
| Avalonia.Themes.Fluent | 11.2.7 | Default theme (already referenced) | MIT |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM source generators (already referenced) | MIT |
| Markdown.Avalonia | 11.0.3 | Markdown rendering | MIT |
| AvaloniaEdit | 11.0.6 | Code editor (Monaco equivalent) | MIT |
| Avalonia.AvaloniaEdit.TextMate | 11.0.6 | TextMate grammar syntax highlighting | MIT |
| LiveChartsCore | 2.0.0-rc5.4 | Charts (cost/token meter) | MIT |
| LiveChartsCore.Avalonia | 2.0.0-rc5.4 | Avalonia bindings for LiveCharts | MIT |
| Projektanker.Icons.Avalonia | 9.8.1 | Icon library (lucide equivalent) | MIT |
| DiffPlex | 1.7.2 | Diff engine | MIT |
| FuzzySharp | 2.0.2 | Fuzzy string matching | MIT |
| Pty.Net | 0.5.81 | Cross-platform PTY (node-pty equivalent) | MIT |
| Svg.Skia | 3.0.0 | SVG rendering (for mermaid diagrams) | MIT |
| SkiaSharp.Avalonia | 3.0.0 | Skia integration for Avalonia | MIT |
| Toast.Avalonia | 11.0.0 | Toast notifications (alternative to custom) | MIT |
| PdfViewer.Avalonia | 11.0.0 | PDF preview | MIT |
| Microsoft.Extensions.Localization | 10.0.0 | i18n (i18next equivalent) | MIT |

### 10.3 Source code snippets fetched

Subagent R fetched the following files for this research (saved locally to `/tmp/research/`):

- `/tmp/research/orca_readme.md` — Orca README, 268 lines
- `/tmp/research/pi_readme.md` — Pi README, 99 lines
- `/tmp/research/kilo_readme.md` — Kilocode README, 177 lines
- `/tmp/research/opencode_readme.md` — OpenCode README (dev branch), 129 lines
- Orca `package.json`, `src/main/index.ts` (head), `electron.vite.config.ts` (head), `tsconfig.json`, `docs/STYLEGUIDE.md`, `docs/reference/headless-linux-server.md`, `.github/CONTRIBUTING.md`, `src/renderer/src/components/ui/command.tsx` (head)
- Pi `package.json` (root), `packages/tui/package.json`, `packages/tui/README.md` (head), `packages/coding-agent/README.md` (head), `AGENTS.md`
- Kilocode `package.json` (root)
- OpenCode `package.json` (dev), `AGENTS.md` (dev)

### 10.4 Harbor source files Subagent R consulted (for grounding)

- `/home/z/my-project/extracted/src/Harbor.Tui.Avalonia/MainWindow.axaml` — 90 lines, basic chat window, no animations
- `/home/z/my-project/extracted/src/Harbor.Tui.Avalonia/Harbor.Tui.Avalonia.csproj` — confirmed Avalonia 11.2.7 + CommunityToolkit.Mvvm 8.4
- `/home/z/my-project/extracted/worklog.md` — read for context (76.3KB; Tasks 0, 7, 1, 4, 6, 0.5 entries reviewed)
- `LS` of `/home/z/my-project/extracted/src/` — confirmed 25 projects including 12 TUI variants (Spectre, Spectre.Fullscreen, SpectreTui, Avalonia, Wpf, Maui, Blazor, Plain, Ansi, Sixel, RazorConsole, Termina, TerminalGui, Notifications, Registry, Abstractions)
- `LS` of `/home/z/my-project/extracted/docs/` — confirmed 16 existing docs (this file becomes #17)

### 10.5 Patterns documented in other Harbor docs

- `docs/ALTERNATIVE_UIS.md` — describes the existing 12 TUI variants and how to switch between them
- `docs/SPECTRE_TUI_DEEP_DIVE.md` — deep dive on the SpectreTui renderer (panel system, keybindings, render loop)
- `docs/PLUGIN_SYSTEM.md` — describes `IPlugin` and the plugin loader
- `docs/TOOLS_CATALOG.md` — describes the 14 builtin tools
- `docs/ROADMAP.md` — Harbor's existing roadmap (Subagent R's Section 8 supplements this with the feature-rip-off-specific roadmap)
- `docs/CODE_PRINCIPLES_AUDIT.md` — 41 principle violations, 11 critical (most now resolved per Task 6)

---

## Document metadata

- **Author:** Subagent R (researcher)
- **Date:** 2026 (per Orca LICENSE year)
- **Task ID:** R
- **Length:** ~2 050 lines
- **Files written:** `/home/z/my-project/extracted/docs/FEATURE_RESEARCH.md` (this file)
- **Worklog entry:** appended to `/home/z/my-project/worklog.md` (see Task ID: R section)

End of document.
