# Harbor-Harness UI/UX Competitive Analysis

## 1. Competitor Landscape Summary

### 1.1 Cursor (cursor.sh) — AI-First IDE
**Позиционирование:** Саmost polished AI-native IDE на рынке, VS Code форк с глубокой AI интеграцией.

**Signature UI/UX паттерны:**
- **Composer** (`Cmd+I`) — многофайловый агентный режим с планированием и выполнением
- **Design Mode** — визуальное редактирование UI через point-and-click + voice
- **Tab** — модель предсказания следующего редактирования (не просто автодополнение, а понимание потока изменений)
- **@ Context System** — упоминания `@File`, `@Folder`, `@Code`, `@Docs`, `@Web`, `@Git`, `@Codebase` для явного контекста
- **Background Agents** — длительно работающие агенты, не блокирующие IDE
- **Cloud Agents** — облачные сессии, доступные с мобильных устройств

**Interaction Model:**
- Два режима: быстрый (inline edit, Cmd+K) и стратегический (Composer, Plan Mode)
- Агент-centric workflow: задачи делегируются агенту, пользователь принимает диффы
- Встроенный браузер для тестирования UI

**Visual Polish:**
- Тематическая система (dark/light), анимации переходов
- Индикаторы streaming с shimmer-эффектами
- Cell-diff view с инлайн-предпросмотром

**Information Architecture:**
- Agents Window — центральный хаб для управления агентами
- Чат-панель + редактор + файловое дерево + дифф-вью
- Планировщик задач (Plan Mode) с editable step list

**Unique Differentiators:**
- Multi-agent collaboration (параллельные ветки агентов)
- Origin Code Hosting (собственный хостинг репозиториев)
- Plugin Marketplace с MCP, skills, subagents

---

### 1.2 Windsurf (codeium.com/windsurf) — Flow-First AI IDE
**Позиционирование:** AI IDE от Codeium (приобретен OpenAI), философия "Flow state".

**Signature UI/UX паттерны:**
- **Cascade** — flow-aware агент, поддерживающий контекст всей сессии
- **Supercomplete** — многострочное, многофайловое автодополнение
- **Flows** — Kanban-style dashboard для управления агентскими сессиями
- **Devin** — облачный автономный агент (отдельная машина)

**Interaction Model:**
- Минимум прерываний: агент действует проактивно на основе trajectory analysis
- Терминал с AI-awareness: Cascade читает вывод терминала и действует на ошибки
- @references для контекста

**Visual Polish:**
- Чистый VS Code-based интерфейс
- Индикаторы Cascade активности
- Генерации UI preview (для frontend задач)

**Information Architecture:**
- Единый редактор + AI панель
- Flow-tracking: файловая активность, clipboard history, terminal context
- Memories + Rules для персистентного контекста

**Unique Differentiators:**
- **Free tier:** 200 Cascade flows/день (бесплатно)
- **Zero Data Retention** для Teams/Enterprise
- Git Worktrees support (параллельные сессии в одном окне)
- Модельный кредит system (взвешенная стоимость моделей)

---

### 1.3 GitHub Copilot / Copilot Workspace
**Позиционирование:** Enterprise-standard, глубоко интегрированный в GitHub экосистему.

**Signature UI/UX паттерны:**
- **Copilot Workspace** — task-to-code: Issue → Plan → Code → PR
- **Plan Mode** — генерация структурированных планов перед написанием кода
- **Cloud Agent** — фоновые задачи с auto-filing PR
- **Copilot CLI** — TUI с tabbed access (PR, Issues, Gists), voice input

**Interaction Model:**
- Chat в редакторе + inline completion + agent mode
- Workspace: editable plan → editable code → terminal → Codespace
- Асинхронные review agents на каждый PR

**Visual Polish:**
- GitHub-native дизайн система
- Reasoning tokens UI (collapsed/collapsedPreview/fixedScrolling)
- Activity log структурированный как code review

**Information Architecture:**
- GitHub-centric: Issue → Workspace → Code → PR → Actions
- PR/Issue-first entry points
- Shared workspace links для коллаборации

**Unique Differentiators:**
- GitHub-native integration (Actions, Security, CodeQL)
- Enterprise: SSO, SCIM, audit logs
- VS Code 2026 cloud agent preview
- Open-sourced Copilot Chat extension (MIT)

---

### 1.4 Continue.dev — Open-Source BYOLLM
**Позиционирование:** Open-source альтернатива с максимальной гибкостью моделей.

**Signature UI/UX паттерны:**
- **Continuous AI** — CI-first platform (автоматические AI review на каждый PR)
- **Checks** — async агенты на PR
- **Context Providers** — гибкая система контекста

**Interaction Model:**
- IDE extension (VS Code + JetBrains) + CLI
- Slash commands + @context
- Config-driven workflow

**Visual Polish:**
- Функциональный, но менее полированный UI ("Plain UI Design")
- Требует ручной конфигурации config.json

**Information Architecture:**
- IDE-native chat + completion
- Hub для shared configs
- CI/PR интеграция через API

**Unique Differentiators:**
- Полностью open-source (MIT)
- 100+ моделей включая локальные (Ollama)
- 100% offline support
- Teams plan: $10/user/month для CI features

---

### 1.5 Cline / Claude Dev — Open-Source VS Code Agent
**Позиционирование:** De facto open-source агент внутри VS Code.

**Signature UI/UX паттерны:**
- **Agent Mode** — автономное выполнение с approval gates
- **Kanban Board** — визуальный трекинг задач
- **MCP Support** — расширяемость через внешние инструменты
- **SDK** — programmatic agent API

**Interaction Model:**
- Chat-driven agent: пользователь описывает задачу, Cline выполняет шаг за шагом
- Approval prompts для каждого tool use
- VS Code-native: файловое дерево, терминал, editor

**Visual Polish:**
- VS Code extension UI
- Индикаторы agent status
- Rich tool call cards

**Information Architecture:**
- Chat panel + file explorer + terminal output
- Task decomposition view
- MCP tools panel

**Unique Differentiators:**
- Open-source (Apache 2.0)
- VS Code + JetBrains plugins + CLI + SDK
- BYO API keys
- Agent Teams (multi-agent orchestration)

---

### 1.6 Aider — Terminal Pair Programmer
**Позиционирование:** Git-native терминальный AI pair programmer.

**Signature UI/UX паттерны:**
- **Codebase Map** — семантический индекс файлов
- **Repo Map** — понимание отношений между файлами
- **Voice-to-code** — голосовой ввод
- **Architect Mode** — разделение планирования и исполнения между моделями

**Interaction Model:**
- Терминал-first: `aider` → чат → изменения коммитятся автоматически
- Git-centric: каждый edit → auto-commit с осмысленным message
- Мультимодальный: images, URLs, voice

**Visual Polish:**
- Функциональный terminal UI
- Colorized diff output
- Git integration visualization

**Information Architecture:**
- Repo-centric: файлы указываются явно или через Codebase Map
- Git history как UI для отката изменений
- Модель-agnostic routing

**Unique Differentiators:**
- Лучшая Git интеграция (auto-commit, auto-revert)
- 100+ моделей включая локальные
- Auto-test & lint cycle (TDD + AI)
- 39K+ GitHub stars, 15B tokens/неделю

---

### 1.7 Claude Code — Anthropic Terminal Agent
**Позиционирование:** Official Anthropic CLI agent, терминал-first.

**Signature UI/UX паттерны:**
- **Rich TUI** — interactive checklists, selection prompts (в native terminal)
- **Subagents** — делегирование специализированным суб-агентам
- **Hooks** — PreToolUse/PostToolUse customization
- **Agent Teams** — координация мульти-агентных команд

**Interaction Model:**
- Терминал-first, работает с любым редактором
- Natural language → чтение кода → редактирование → запуск тестов
- Interactive plan confirmation (checkboxes)

**Visual Polish:**
- Обновленный terminal interface (2025)
- Searchable prompt history (Ctrl+R)
- Status visibility improvements

**Information Architecture:**
- Codebase index на первый запуск
- Tool call cards с approval prompts
- Verbose mode для debugging

**Unique Differentiators:**
- First-party Anthropic (Opus, Sonnet)
- Agent SDK для кастомных агентов
- VS Code extension с richer graphical experience
- MCP support из коробки

---

### 1.8 Kilo Code — Open-Source Multi-IDE Agent
**Позиционирование:** All-in-one agentic engineering platform, открытый исходный код.

**Signature UI/UX паттерны:**
- **Multi-Mode** — Code, Ask, Plan, Debug (+ custom modes)
- **Agent Manager** — параллельные worktree сессии
- **MCP Marketplace** — магазин расширений
- **Autocomplete** — inline ghost-text

**Interaction Model:**
- VS Code extension + CLI + Cloud
- @filename, @terminal для контекста
- Pop-out chat в отдельный tab
- Secondary Side Bar layout

**Visual Polish:**
- VS Code-native
- System notifications
- Inline diff preview

**Information Architecture:**
- Agent-centric sidebar
- Session management с поиском
- Worktree-based isolation

**Unique Differentiators:**
- 500+ моделей (BYOK, zero markup)
- Open-source (Apache 2.0)
- Roo Code migration path
- Kilo Pass ($19/mo) для bundled credits
- Slack integration, mobile app

---

### 1.9 OpenHands / OpenDevin — Autonomous Agent Platform
**Позиционирование:** Open-source автономный software engineer (аналог Devin).

**Signature UI/UX паттерны:**
- **CodeAct Agent** — Plan → Code → Test → Fix loop
- **Docker Sandbox** — изолированное выполнение
- **Multi-Agent** — делегирование между специализированными агентами
- **Computer Use** — браузер для веб-задач

**Interaction Model:**
- Web UI + CLI
- Task description → autonomous execution → PR
- Human-in-the-loop для approval на критических действиях

**Visual Polish:**
- Web-based dashboard
- Event stream architecture для real-time observability
- Chat interface + shell view + browser view

**Information Architecture:**
- Session-centric: каждый запуск в Docker контейнере
- Event-sourced diff synchronization
- REST API + SDK

**Unique Differentiators:**
- 72% SWE-Bench (открытый источник)
- MIT License
- 100+ LLM providers
- 66K+ active users
- On-prem deploy option

---

## 2. Harbor's Current Position

### 2.1 CellForge TUI (`src/Harbor.Tui.CellForge/`)
**Текущее состояние:**
- Raw-mode terminal renderer с SGR automaton (стиль-диффинг)
- Event-driven архитектура: `AgentEvent` → streaming output
- Базовый chat history + input prompt
- Status bar + diff overlay через builtin views
- Отсутствие layout compositing (один столбец streaming)
- Отсутствие inline editing, multi-pane, syntax highlighting в реальном времени

**Сильные стороны:**
- Аппаратно-независимый SGR автомaton (эффективный escape-code diffing)
- Чистый contract: `ITuiRenderer` / `BaseTuiRenderer`
- Golden-frame test seam для тестирования
- Поддержка thinking tokens (dim + italic)
- Alternate screen buffer

**Слабые стороны:**
- Монолитный streaming: нет разделения на panels/windows
- Нет интерактивного approval UI (только текстовый ввод)
- Нет rich tool call cards (только `→ tool_name args`)
- Нет persistent layout (всё streaming vertically)
- Нет syntax highlighting для tool outputs

### 2.2 Avalonia Desktop (`apps/Harbor.App.Avalonia/`)
**Текущее состояние:**
- Cross-platform desktop GUI (Windows/Linux/macOS)
- Avalonia 12.1 + AvaloniaEdit 12 + CommunityToolkit.Mvvm
- Catppuccin-Mocha/Latte темы
- Sidebar + chat + code editor + status bar (1280×800)
- Streaming chat с markdown (Markdig AST → Avalonia controls)
- Session manager (list/load/save через `ISessionStore`)
- Diff view для file-changing tool calls
- Token usage chart
- Board view + Focus Session view
- Plugin panel host (right dock)
- Toast notifications
- Command palette (`Ctrl+P`)
- Onboarding window с provider preset catalog
- Provider browser с health check

**Сильные стороны:**
- Полноценная desktop архитектура (MVVM + DI + hosting)
- Theme system с переключением Ctrl+Shift+T
- Rich markdown rendering через Markdig
- Session persistence
- Provider onboarding flow
- Plugin panel lifecycle

**Слабые стороны:**
- AOT-incompatible (требует JIT)
- Headless тесты с "Stack empty" flakes
- Отсутствие multi-window / detachable panels
- Нет system tray + background runs
- Нет auto-update
- Нет native OS notifications

---

## 3. Specific Gaps Where Competitors Are Ahead

### 3.1 Agent Experience & Transparency
| Competitor | Feature | Harbor Status |
|------------|---------|---------------|
| Cursor | Background Agents (не блокируют IDE) | ❌ Нет |
| Claude Code | Interactive checklists для multi-step plans | ❌ Нет |
| OpenHands | Event stream с real-time observability | ⚠️ Частично (AgentEvent streaming) |
| Cline | Approval gates с rich UI для каждого tool use | ❌ Нет |
| Windsurf | Flow tracking (файловая активность + clipboard) | ❌ Нет |

### 3.2 Visual Polish & Animations
| Competitor | Feature | Harbor Status |
|------------|---------|---------------|
| Cursor | Shimmer skeletons для generating diffs | ❌ Нет |
| Cursor | Cell-diff inline preview | ⚠️ Есть базовый diff view, нет inline preview |
| Perplexity/Cursor | Stable-layout markdown streaming (без layout shift) | ❌ Нет (полный re-render на каждый токен) |
| Windsurf | Generation progress indicators | ❌ Нет |
| Claude Code | Phase-based streaming с status line | ⚠️ Базовый, нет phase indicators |

### 3.3 Information Architecture
| Competitor | Feature | Harbor Status |
|------------|---------|---------------|
| Cursor | Agents Window (центральный хаб для всех агентов) | ❌ Нет |
| Cursor | Multi-file Composer с editable plan | ❌ Нет |
| Windsurf | Cascade Flows (Kanban для agent sessions) | ❌ Нет |
| Kilo Code | Agent Manager + worktree isolation | ❌ Нет |
| OpenHands | Docker sandbox + multi-agent delegation | ❌ Нет |

### 3.4 Interaction Models
| Competitor | Feature | Harbor Status |
|------------|---------|---------------|
| Cursor | Design Mode (point-and-click UI editing) | ❌ Нет |
| Cursor | Inline edit (`Cmd+K`) | ❌ Нет |
| Windsurf | Supercomplete (многострочное автодополнение) | ❌ Нет |
| Aider | Voice-to-code | ❌ Нет |
| Kilo Code | Pop-out chat → full editor tab | ❌ Нет |
| GitHub Copilot | Workspace (Issue → Plan → Code → PR) | ❌ Нет |

### 3.5 Context & Memory
| Competitor | Feature | Harbor Status |
|------------|---------|---------------|
| Cursor | @Codebase (полнотекстовый поиск по индексу) | ❌ Нет |
| Cursor | Project-level Rules | ❌ Нет |
| Windsurf | Memories + `.windsurfrules` | ❌ Нет |
| Continue.dev | Context Providers (гибкая система) | ❌ Нет |
| Aider | Codebase Map (семантический индекс) | ❌ Нет |

### 3.6 Terminal-Specific
| Competitor | Feature | Harbor Status |
|------------|---------|---------------|
| Aider | Auto-commit на каждый edit | ❌ Нет |
| Aider | Auto-test & lint cycle (TDD + AI) | ❌ Нет |
| Claude Code | Rich interactive prompts (checkboxes, selection) | ❌ Нет (только ReadLine) |
| Claude Code | Subagent transparency UI | ❌ Нет |
| Kilo Code | `kilo console` lifecycle | ⚠️ Базовый REPL |

---

## 4. Unique Advantages Harbor Has

### 4.1 Architecture & Modularity
- **Event Bus Decoupling:** TUI subscribes to events, doesn't call Core directly (позволяет multiple renderers simultaneously)
- **Clean/Hexagonal Layering:** enforced by 46 architecture tests
- **NativeAOT-readiness:** Core код AOT-compatible (за исключением Avalonia)
- **Plugin System:** Roslyn CS-source plugins + DLL-based, out-of-process

### 4.2 Dual-Surface Strategy
- **CellForge TUI + Avalonia Desktop** — два независимых renderer на один и тот же `AgentEvent` stream
- Возможность запуска в headless environments (CellForge) + rich desktop (Avalonia)
- Другие инструменты: либо IDE extension, либо CLI, но не оба simultaneously с shared state

### 4.3 Zero-Reflection Hot Paths
- `AnsiWriter` SGR automaton с style-diffing (zero redundant escape codes)
- `Utf8JsonWriter` / `JsonSerializerContext` на hot paths
- `ReadOnlySpan<T>` для per-line JSONL parsing (PERF-005 mitigation)

### 4.4 Event-Driven Streaming
- Полный lifecycle: `AgentStartEvent` → `TurnStartEvent` → `MessageStartEvent` → `MessageUpdateEvent` (TextDelta, ThinkingDelta, ToolCallDelta) → `MessageEndEvent` → `TurnEndEvent` → `AgentEndEvent`
- Это более детализировано, чем у многих конкурентов (Cursor, Copilot)

### 4.5 Permission System
- `PermissionRuleset` с granular `Allow/Ask/Deny`
- Per-tool, per-arg-path правила
- Это редкость среди open-source инструментов

### 4.6 Modular Tool Registry
- `ITool` abstraction с `ExecutionMode` (Parallel/Sequential)
- 14 builtin tools + plugin extensibility
- MCP tool support через `McpToolTool`

---

## 5. Concrete Recommendations to Reach "Max" Level

### 5.1 CellForge TUI — Immediate Wins (P0)

#### 5.1.1 Rich Layout Compositor
**Задача:** Разделить экран на panels вместо монолитного streaming.
```
┌─────────────────────────────────────────┐
│ Status Bar          │ Chat History      │
│ (tool, model, cost) │ (scrollable)      │
├─────────────────────┴───────────────────┤
│ Tool Call Card (collapsible)            │
│ ┌─ read /README.md ──────────────────┐ │
│ │ Args: {"limit":10}                 │ │
│ │ Result: success (234 tokens)        │ │
│ └─────────────────────────────────────┘ │
├─────────────────────────────────────────┤
│ Input Prompt [_________________________]│
└─────────────────────────────────────────┘
```

**Implementation Plan:**
1. Добавить `LayoutSlot` enum в `ITuiRenderContext` (Top, Main, Bottom, Overlay)
2. Создать `PanelRenderContext` — абстракцию поверх `CellForgeRenderContext`
3. Разделить `RenderAsync` на:
   - `RenderStatusBar()` — всегда сверху
   - `RenderChatHistory()` — основной scrollable area
   - `RenderToolCard()` — collapsible cards для tool calls
   - `RenderInput()` — prompt внизу
4. Добавить `AlternateScreenBuffer` layout (уже есть `EnterAlternateScreen`/`ExitAlternateScreen`)

#### 5.1.2 Interactive Approval Prompts
**Задача:** Rich interactive prompts вместо простого `ReadLine`.
- Checkbox lists для multi-step plans (как Claude Code)
- Selection prompts с on-screen indicators
- Tool call approval: `[Y] Approve  [N] Deny  [E] Edit args  [S] Skip`

**Implementation Plan:**
1. Добавить `InteractivePrompt` абстракцию в `ITuiRenderContext`
2. Создать `CheckboxPrompt`, `SelectionPrompt`, `ConfirmPrompt`
3. Использовать `Console.CursorTop`/`Console.CursorLeft` для positioning
4. Keyboard input через `Console.ReadKey(true)` (не `ReadLine`)

#### 5.1.3 Streaming Markdown with Stable Layout
**Задача:** Предотвратить layout shift при streaming markdown.
- Резервировать код-блоки полной ширины при открытии ```
- Commit heading-level сразу при появлении `## `
- Defer inline formatting (bold, italic) до closing delimiter

**Implementation Plan:**
1. Создать `StreamingMarkdownParser` — incremental parser
2. Commit block-level decisions early (heading, list, code, paragraph)
3. Track known-width blocks (code fences, tables)
4. Diff at block level, не full re-render

#### 5.1.4 Thinking Tokens UX
**Задача:** Показывать thinking tokens collapsed по умолчанию с preview.
```
💭 Thinking... (Ctrl+T to expand)
└─ "The user wants to add authentication...
   [collapsed: 142 tokens]
```

**Implementation Plan:**
1. Добавить `ThinkingBlock` widget в `CellForgeTuiRenderer.RenderLiveToken`
2. Collapsed по умолчанию, `Ctrl+T` для toggle
3. Shimmer effect для активного thinking state

#### 5.1.5 Command Palette
**Задача:** Fuzzy search по командам, файлам, history.
- `Ctrl+P` → fuzzy search
- Recent commands, recent files, slash commands

**Implementation Plan:**
1. Адаптировать Avalonia `CommandPaletteView` логику для TUI
2. Использовать `Console.ReadKey` для input + ANSI cursor movement
3. Simple fuzzy match (Levenshtein distance или prefix matching)

---

### 5.2 Avalonia Desktop — High Impact (P0-P1)

#### 5.2.1 Inline Edit (Cmd+K / Ctrl+K)
**Задача:** Inline code editing через AI, как в Cursor.
- Select code → `Cmd+K` → describe change → inline diff → accept/reject

**Implementation Plan:**
1. Добавить `InlineEditOverlay` в `CodeEditorView.axaml`
2. Создать `InlineEditViewModel` с `SelectedCode`, `Prompt`, `GeneratedDiff`
3. Использовать AvaloniaEdit `TextEditor` selection API
4. Accept: apply diff → Revert: restore original

#### 5.2.2 Agent Manager Panel
**Задача:** Central hub для управления параллельными agent sessions.
- Grid/List активных agent runs
- Status indicators (running, waiting approval, completed, failed)
- Cancel / Pause / Resume controls
- Session detail с tool call timeline

**Implementation Plan:**
1. Создать `AgentManagerView.axaml` + `AgentManagerViewModel`
2. Подписаться на `AgentStartEvent`, `AgentEndEvent`, `TurnStartEvent`, `TurnEndEvent`
3. Использовать `ObservableCollection<AgentSessionViewModel>`
4. Добавить в Shell layout (left sidebar или right panel)

#### 5.2.3 Streaming Markdown without Layout Shift
**Задача:** Стабильный markdown streaming (как Claude, Cursor).
- Reserve code blocks при открытии ```
- Commit headings early
- Block-level diff, не full re-render

**Implementation Plan:**
1. Refactor `ChatMarkdown.cs` — incremental AST building
2. Track "reserved" blocks (code, tables) с fixed height
3. Commit block types на early tokens
4. Использовать `VirtualizingStackPanel` для message list

#### 5.2.4 Rich Tool Call Cards
**Задача:** Визуальные карточки для tool calls с collapse/expand.
- Collapsed: `→ read /README.md` (blue, clickable)
- Expanded: args + result + timing + token usage
- Retry / Edit / Copy buttons

**Implementation Plan:**
1. Создать `ToolCallCardView.axaml` + `ToolCallCardViewModel`
2. DataTemplate selector в `ChatView.axaml` (message vs tool_call)
3. Collapse/Expand animation (Avalonia `DoubleAnimation` opacity)
4. Action buttons: Retry (re-run tool), Edit (modify args), Copy

#### 5.2.5 Phase Indicators
**Задача:** Показывать фазу генерации (thinking → tool_call → answering).
- `💭 Thinking...` → `🔧 Using tool: read` → `✍️ Writing response...`

**Implementation Plan:**
1. Добавить `PhaseIndicator` в `ChatViewModel`
2. Подписаться на `MessageUpdateEvent.LlmEvent`
3. Track last `StepFinishEvent.Reason` (tool_use, end_turn, etc.)
4. Animate transitions (fade in/out)

#### 5.2.6 Thinking Toggle
**Задача:** Collapsible thinking blocks.
- По умолчанию collapsed: `💭 Thinking... (142 tokens) [Click to expand]`
- Expand: full thinking text в italic + dim

**Implementation Plan:**
1. Добавить `ThinkingBlock` control в `ChatMarkdown.cs`
2. `ToggleButton` с `Expanded`/`Collapsed` state
3. Animate height change (Avalonia `GridLength` animation)

#### 5.2.7 System Tray + Background Runs
**Задача:** Минимизировать в tray, продолжить работу в фоне.
- Tray icon с context menu (New Session, Settings, Quit)
- Notification на completion

**Implementation Plan:**
1. Добавить `TrayIcon` в `App.axaml`
2. Создать `BackgroundService` для agent runs
3. Подписаться на `AgentEndEvent` → toast notification

---

### 5.3 Cross-Cutting Improvements (P1-P2)

#### 5.3.1 Unified Event Stream Architecture
**Задача:** Один `AgentEvent` stream → оба renderer (CellForge + Avalonia).
- Это уже есть, но нужно formalize contract
- Добавить `RenderMetadata` для renderer-specific hints

**Implementation Plan:**
1. Добавить `RenderHints` в `AgentEvent` (optional)
2. CellForge: `RenderHints.PreferCompact = true`
3. Avalonia: `RenderHints.PreferRich = true`

#### 5.3.2 Context Indexing (Codebase Map)
**Задача:** Семантический индекс кодовой базы (как Aider Codebase Map, Cursor @Codebase).
- Index files on session start
- Provide `@codebase` context в chat

**Implementation Plan:**
1. Создать `ICodebaseIndex` abstraction в `Harbor.Abstractions`
2. Реализация: `EmbeddingCodebaseIndex` (local embeddings)
3. Интегрировать в `ChatViewModel` → `@codebase` autocomplete

#### 5.3.3 Git Integration Layer
**Задача:** Auto-commit, diff visualization, revert.
- Как Aider: auto-commit после каждого edit
- Diff timeline (как GitLens)

**Implementation Plan:**
1. Создать `IGitIntegration` service
2. `AutoCommitAfterToolCall` middleware
3. Diff timeline view в Avalonia

#### 5.3.4 Plugin Marketplace
**Задача:** Визуальный marketplace для плагинов (как Cursor Plugin Marketplace, Kilo MCP Marketplace).
- Browse, install, configure plugins
- One-click activation

**Implementation Plan:**
1. Создать `PluginMarketplaceView.axaml`
2. Catalog JSON с plugin metadata
3. One-click install → download DLL → register

---

## 6. Priority Roadmap

### Phase 1: Foundation (1-2 sprints)
1. ✅ Streaming markdown stable layout (Avalonia)
2. ✅ Rich tool call cards (Avalonia)
3. ✅ Phase indicators (Avalonia)
4. ✅ Thinking toggle (Avalonia)

### Phase 2: Agent UX (2-3 sprints)
1. ✅ Interactive approval prompts (CellForge)
2. ✅ Agent Manager panel (Avalonia)
3. ✅ Inline edit (Avalonia)
4. ✅ Command palette (CellForge)

### Phase 3: Polish & Scale (3-4 sprints)
1. ✅ Context indexing (Codebase Map)
2. ✅ Git integration layer
3. ✅ Plugin marketplace UI
4. ✅ System tray + background runs
5. ✅ Multi-window detachable panels

---

## 7. Summary

**Harbor's unique value proposition:**
- **Dual-surface strategy** (CellForge TUI + Avalonia Desktop) на одном event stream
- **Event-driven architecture** с полным lifecycle coverage
- **Modular, AOT-ready core** с zero-dep abstractions
- **Permission system** + plugin extensibility из коробки

**Biggest gaps vs. competitors:**
1. **Agent transparency** — отсутствие rich approval UI, plan visualization, subagent tracking
2. **Visual polish** — нет shimmer skeletons, stable markdown streaming, phase indicators
3. **Context awareness** — нет codebase indexing, project rules, semantic search
4. **Information architecture** — отсутствие центрального Agent Manager, multi-pane layout

**Quickest path to "max" level:**
1. **Stable markdown streaming** в Avalonia (1-2 дня)
2. **Rich tool call cards** с collapse/expand (2-3 дня)
3. **Phase indicators** + thinking toggle (1-2 дня)
4. **Agent Manager panel** (3-5 дней)
5. **Interactive approval prompts** в CellForge (3-4 дня)

Это даст immediate visual parity с Cursor/Windsurf, не меняя core architecture.
