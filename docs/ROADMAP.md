# Roadmap

> Harbor development roadmap. Last updated: v0.4.0-alpha, R31 (post-decomposition).
> See [CHANGELOG.md](../CHANGELOG.md) for the per-release change history.
>
> **Sprint-2 note:** optional UI/scripting components moved to [`contrib/`](../contrib/)
> (`contrib/tui/`, `contrib/apps/`, `contrib/scripting/`, `contrib/tests/`; build via
> `contrib/Contrib.slnx`). They still exist and work — just outside `Harbor.slnx`.

## Current State: v0.4.0-alpha (R31)

### ✅ Completed — Core

- **Architecture**: Clean/Hexagonal/Onion layering enforced via analyzers
- **TEA state machine** (`UiStore` + `UiReducer` + `UiMsg`): Elm-style unidirectional data flow
- **EventBus**: pub/sub decoupling between agent loop and UI
- **Result<T>**: Railway Oriented Programming throughout, no exceptions for expected failures
- **Zero unsafe code**: 100% safe, no `unsafe` blocks
- **NativeAOT-ready**: core can be AOT-compiled (TUI runs JIT separately)

### ✅ Completed — Providers (4 native + 13 JSON)

- Native: `Anthropic` (cache_control, extended thinking), `OpenAI` (Chat + Responses API for o1/o3), `Ollama` (local NDJSON), `OpenAiCompatible`
- JSON: OpenRouter, Kilo Code (with **FREE** models), DeepSeek, Groq, Mistral, xAI, Together, Fireworks, Cerebras, vLLM, + all OpenAI-compat

### ✅ Completed — Storage (3 backends)

- `Jsonl` (default, zero deps, append-only, atomic writes, parsed-message cache) — **decomposed in R31** (codec extracted)
- `Memory` (tests)
- `Sqlite` (indexed queries)

### ✅ Completed — UI (4 platforms + 14 TUI renderers)

- **TUI**: Ansi, Plain, Spectre, Spectre.Fullscreen, SpectreTui, TerminalGui, Termina, RazorConsole, Sixel, Notifications
- **Desktop**: WPF, Avalonia, MAUI
- **Web**: Blazor Server

### ✅ Completed — Tools (14 builtin)

`read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls`, `task`, `web_fetch`, `patch`, `notebook`, `ripgrep`, `tree`, `mcp`

### ✅ Completed — Plugin System (R30 fix)

- `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin` contracts
- Roslyn-based CS-source plugin compiler with disk-cache decorator
- 4 sample plugins: `WebSearch`, `TodoWrite`, `GitTools`, `FileTree`
- **R30 bug fix**: Roslyn now sees `Harbor.Abstractions.Models.*` (types physically in Harbor.Domain.dll) via explicit `typeof(Session).Assembly` reference

### ✅ Completed — UI Component Decomposition (R28-R31)

- **Reusable components** (`StatusBadge`, `ChatBubble`, `SessionRow`) implemented in 3 platforms (Avalonia / Blazor / WPF) with identical prop names + shared `StatusMappers` helpers
- **Platform-agnostic `ToolCallViewModel`** in `Harbor.Ui.Framework` (replaced `IBrush` with `string StatusBrushKey`)
- **`StatusMappers`** in `Harbor.Ui.Framework.Converters` — single source of truth for status→brush-key / status→label / time-ago / token-compact / cost formatting
- **`ChatLineViewModel`** extended with `TimestampUtc` + `TimestampText` + `Preview` (80-char truncation)
- **`StatusBarViewModel`** extracted from `MainViewModel`

### ✅ Completed — Business Logic Extraction (R30-R31)

Moved platform-agnostic logic out of `Harbor.App.Avalonia` into `Harbor.Ui.Framework`:

| File | Old location (Avalonia) | New location (Ui.Framework) |
|---|---|---|
| ChatMessageRenderer | Services/ | Rendering/ |
| ChatStreamingPresenter | Services/ | Rendering/ |
| SessionContext | Services/ | Sessions/ |
| SessionFactory | Services/ | Sessions/ |
| SessionSwitcher | Services/ | Sessions/ |
| SessionGitTracker | Services/ | Sessions/ |
| `ICommonConfigReader` | — | Configuration/ (new abstraction) |

### ✅ Completed — God-Object Decomposition (R31)

- **MarkdownRenderer**: 487 → 110 lines (control) + 4 specialized classes (`MarkdownBlockRenderer`, `MarkdownInlineRenderer`, `MarkdownTextExtractor`, `MarkdownResourceResolver`)
- **JsonlSessionStore**: 688 → 528 lines + new `JsonlMessageCodec` (215 lines, stateless)
- **SessionManager**: 495 → 492 lines but 2 fewer concerns via new `IChatViewBinder` interface + `AvaloniaChatViewBinder` adapter

### ✅ Completed — Concurrent Agents (R25-R26)

- Per-session `UiStore` in `SessionContext`
- `SessionManager._contexts` Dictionary<string, SessionContext>
- `AppHost` EventBus subscriber routes agent events by `SessionId`
- `ChatViewModel.RebindToStore` swaps dispatcher binding on session switch
- Background agents keep running when user switches sessions (no abort)

### ✅ Completed — Onboarding Wizard + Provider Picker

- 5-step wizard (welcome → provider → API key → model → working dir)
- `ProviderModelPicker` with search + auth-status indicators + pricing
- Persisted `CommonConfig.ApiKeys` with `CommonConfigAuthResolver` fallback to env vars

### ✅ Completed — Tests

- 242+ unit tests across 9 test projects (Harbor.Abstractions, Core, Storage, IPC, Tools.Builtin, Config, Plugins.Runtime, App.Avalonia, App.Blazor)
- 12 E2E tests (Avalonia headless + CLI)
- TUnit framework (source-generated, fastest .NET test runner)
- 7 analyzer packages, 0 warnings, 0 errors

---

## 📋 Roadmap — by version

### v0.5.0 — Plugin System & Sub-agents

**Plugin loading from DLLs**
- [ ] `IPluginHost` implementation with `AssemblyLoadContext` (JIT mode)
- [ ] Plugin discovery from `~/.harbor/plugins/*.dll`
- [ ] Plugin trust prompt for project-local plugins
- [ ] Hot-reload via `FileSystemWatcher` (JIT only)
- [ ] `harbor plugin install/list/uninstall` CLI commands

**Sub-agent execution**
- [ ] Wire `TaskTool` to `IAgent` via `ToolContext.Services`
- [ ] Sub-agent runs in isolated context with own session
- [ ] Result returned to parent agent as tool output
- [ ] Support `explore`, `plan`, custom sub-agents

**TUI plugins**
- [ ] `ITuiPlugin.RegisterTui(ViewRegistry, ViewModelRegistry)`
- [ ] Sample TUI plugin (e.g. token usage chart)
- [ ] Plugin views override builtin views

### v0.6.0 — MCP Integration

- [ ] MCP client via `ModelContextProtocol` NuGet
- [ ] Stdio, HTTP, SSE transports
- [ ] MCP tools → `ITool` adapter
- [ ] MCP resources as `read_mcp_resource` tools
- [ ] MCP prompts as slash-commands
- [ ] OAuth for MCP servers
- [ ] Lazy connect, reconnect on failure
- [ ] Config: `mcp` section in `config.json`

### v0.7.0 — Skills & LSP

**Skills**
- [ ] `SKILL.md` format with YAML frontmatter
- [ ] Discovery from `~/.harbor/skills/` and `.harbor/skills/`
- [ ] `skill` tool to load skill content
- [ ] `<available_skills>` XML in system prompt
- [ ] `harbor skill install/list` CLI commands

**LSP Integration**
- [ ] LSP client via `OmniSharp.Extensions.LanguageServer.Client`
- [ ] 10+ builtin language servers (TypeScript, Python, Go, Rust, C#)
- [ ] Auto-spawn on file open
- [ ] `diagnostics`, `references`, `definition` tools
- [ ] LSP-aware `read`/`edit` (inject diagnostics)

### v0.8.0 — Session Management Polish

- [ ] Session branching (`harbor session fork <message-id>`)
- [ ] `/tree` slash-command for branch navigation
- [ ] Branch summaries (LLM-generated on branch switch)
- [ ] Snapshot/revert (`harbor session revert <message-id>`)
- [ ] Session search (`harbor session search <query>`)
- [ ] JSONL import/export
- [ ] Session rename (ISessionStore metadata-update API)

### v0.9.0 — Two-Process Architecture

- [ ] Core (NativeAOT) + TUI (JIT) in separate processes
- [ ] NDJSON over Unix Domain Socket wire protocol
- [ ] Late-attach with scrollback replay
- [ ] Multi-client support (terminal + IDE + web)
- [ ] `harbor serve` for headless mode
- [ ] `harbor tui` to attach to running core

### v1.0.0 — Stabilization & Polish

- [ ] API freeze for `Harbor.Abstractions`
- [ ] NuGet packages published for all `Harbor.*` libraries
- [ ] Comprehensive docs site (MdDocs / DocFX)
- [ ] Performance benchmarks vs baseline
- [ ] Security audit
- [ ] Accessibility audit (WCAG 2.1 AA for Blazor/Avalonia)
- [ ] Internationalization (i18n) for UI strings

---

## 🛠 Tech Debt — Refactor backlog (post-R31)

### God-objects still pending decomposition

| File | Lines | Concerns mixed | Plan |
|---|---|---|---|
| `Harbor.Application/Agents/AgentLoop.cs` | 681 | orchestration + retry + tool dispatch + token tracking + events | Extract `ToolDispatcher` + `RetryPolicy` + `TokenTracker` |
| `Harbor.Providers.OpenAI/OpenAILlmClient.cs` | 656 | HTTP + SSE parsing + event mapping + models endpoint | Extract `OpenAiSseParser` + `OpenAiEventMapper` |
| `Harbor.Application/Configuration/HarborConfig.cs` | 492 | mega-record with every config field | Split into per-section records (`ProviderConfig`, `ToolConfig`, `UiConfig`, etc.) |
| `Harbor.Providers.Anthropic/AnthropicLlmClient.cs` | 562 | similar to OpenAI | Same decomposition pattern |
| `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` | 644 | DI registration + config + provider/tool wiring | Decompose into per-concern registrars (mirror Avalonia `Hosting/`) |
| `apps/Harbor.App.Avalonia/Services/SessionManager.cs` | 492 | orchestration + UI dispatch + agent lifecycle | Move to `Ui.Framework` once `TokenUsageViewModel` dependency abstracted |

### Architectural debt

- [x] `Harbor.Desktop.Abstractions` namespace drift: 4 files declared `namespace Harbor.App.Avalonia.ViewModels` while living in `Harbor.Desktop.Abstractions/ViewModels/`. **Verified fixed (ROP-D Z1, 25.08): every file in the project now declares `Harbor.Desktop.Abstractions.*`.**
- [x] Namespace drift `Harbor.Core.*` in Harbor.Application.dll (42 declarations) and Harbor.Registries.dll (3) — migrated to assembly-matching namespaces (ROP-D Z1).
- [x] Namespace drift `Harbor.Cli.*` in Harbor.App.Cli — 18 declarations + all references migrated to `Harbor.App.Cli.*`; remaining census findings documented as intentional (ADR-007 compat namespaces in Contracts/Extensions/Registries; same-family sub-namespaces in Ipc/Telemetry/Ui.Framework) (ROP-D Z1, 25.08).
- [x] BannedApi.txt was dead (never wired, wrong filename for the analyzer) — renamed to `BannedSymbols.txt`, wired via AdditionalFiles in Directory.Build.props; all 9 production GetResult sites resolved or pragma-exempted with a catalogued reason (ROP-D Z2, commit be81e42).
- [x] Arch tests probed the wrong assembly for "TuiAbstractions" (typeof(UiStore) from Ui.Framework.State); retargeted to Terminal.Abstractions + new UiFrameworkState rule (ROP-D Z2, commit 9060475).
- [x] ~28 src projects outside arch enforcement → FullLayerMatrixTests covers all 45 main-solution src assemblies (reference check + table guard + exception liveness + coverage guard); stale `dotnetarch.json` deleted in favor of the single enforcement surface (ROP-D Z2, commits 5d2df19/6566be2).
- [x] SystemPromptContext.McpInstructions hardwired null → IMcpRegistry.GetInstructions() aggregates mcp.json hints + initialize responses; WorkspaceContextSource.FormatMcpInstructions feeds the prompt via AgentLoop DI (ROP-D Z3, commit 64cbc0e).
- [x] ResultGuard survived the §4.5 deletion order and gained a new production call (ConfigStore.LoadCore) → deleted as the duplicate canon: ConfigStore load/save and TreeTool now ride CSE `Result.Try` + `ResultErrors.Message` (save path no longer masks OCE); tests ported to ResultErrorsTests (ROP-D Z3 tail, commit 9e954a5).
- [x] ROP-D final verification (25.08): `Harbor.slnx -c Release` builds with 0 errors; full cycle over all 20 `tests/Harbor.*.Tests` projects — итог 1184 tests: 1176–1178 passed, 6 skipped, 1–2 failed = the two pre-existing Avalonia headless flakes `ChatView_Inflates` / `TryGet_ReturnsNullForUnregistered` (61ee126; flake count varies per run). Re-verified in a fresh ROP-D close-out session: same numbers, both failures confined to the known-flake pair. Enforcement matrix: 47/50 src dirs under arch rules; the other 3 documented out-of-scope (CodeGen build tool, Plugins.Host exe, Providers.Shared linked source) (commit b62de73).
- [ ] Circular project reference workaround: `ICommonConfigReader` in Ui.Framework because Ui.Framework can't reference Desktop.Abstractions (Desktop.Abstractions → Terminal.Abstractions → Ui.Framework). Consider merging Desktop.Abstractions into Ui.Framework, or splitting Terminal.Abstractions.
- [ ] Pre-existing Avalonia 12 headless test failures: 3 tests (`MarkdownRenderer_SetMarkdown_DoesNotThrow`, `CodeBlock_Default_Code_IsEmpty`, `TypewriterStreamingText_CanSet_Text`) fail with "Stack empty" in `SetInheritanceParent`. Needs investigation — likely an Avalonia.Headless bug.
- [ ] IPC tests: 8/35 failing — timing issues with named-pipe disposal on Linux.

### Testing debt

- [ ] E2E test coverage: only 12 E2E tests across 4 apps. Need: 33+ component tests with VLM content verification.
- [ ] No integration tests for plugin compilation against real-world plugin sources (only the hello-world sample).
- [ ] No load tests for concurrent multi-session scenarios.
- [ ] No screenshot-diff tests for UI components.

### Documentation debt

- [ ] API docs not published (DocFX / MdDocs pipeline stubbed but not wired).
- [ ] Per-project README.md files are stale — many reference v0.1.0 architecture.
- [ ] No architecture decision records (ADRs) for the major splits (Domain/Abstractions, Ui.Framework/Tui.Abstractions, etc.).

---

## 🎯 Killer Features (from competitor audit `docs/KILLER_FEATURES.md`)

### ✅ Implemented

- Animated streaming text with typewriter cursor (`TypewriterStreamingText` control)
- Collapsible tool-call cards with status + duration (`ToolCallCardView`)
- Token-usage sparkline in status bar (`Sparkline` control)
- Toast notifications with slide-in + auto-dismiss (`ToastService` + `ToastNotificationsView`)
- Provider/model picker with search + auth status (`ProviderModelPicker`)
- Onboarding wizard with stepper dots

### ⚠️ Partial

- Command palette with recent items + fuzzy (basic structure, needs fuzzy search)
- Markdown rich editor (basic rendering, no TipTap-class editor)
- Intra-line word-diff highlighting (line-level only)

### ❌ Not started

- Tab-strip with drag-reorder + close-gesture
- Worktree jump palette (Cmd-J / Ctrl+J)
- Agent pet mascot that reacts to agent state
- Image preview inline in chat
- Skill freshness pill
- Setup-guide progress ring + checklist
- Dictation / speech-to-text input
- Browser/markup overlay for screenshots

---

## 📊 Metrics

| Metric | Value |
|---|---|
| .NET SDK | 10.0.302 |
| Source projects | 70+ |
| Test projects | 9 + benchmarks |
| Unit tests passing | 240+ |
| E2E tests passing | 12 |
| Builtin tools | 14 |
| Sample plugins | 4 |
| TUI renderers | 10 |
| Desktop platforms | 3 (Avalonia / WPF / MAUI) |
| Web platforms | 1 (Blazor Server) |
| Native LLM providers | 4 |
| JSON-config providers | 13 |
| Storage backends | 3 |
| Analyzer packages | 7 |
| `unsafe` blocks | 0 |
| Warnings | 0 |
| Errors | 0 |

---

## 🧭 Decision Log (high-level)

| Decision | Rationale | Date |
|---|---|---|
| Split `Harbor.Abstractions.Models` types into `Harbor.Domain.dll` | Domain layer should hold value objects + entities; Abstractions is just interfaces | v0.3 |
| Extract `Harbor.Ui.Framework` from `Harbor.Tui.Abstractions` | TEA + Panel system is shared by TUI and desktop GUIs; terminal-specific stuff stays separate | v0.4 (R6) |
| Per-session `UiStore` instead of singleton | User wanted concurrent agents: "agents don't stop when I switch sessions" | v0.4 (R25) |
| Move `ToolCallViewModel` to `Harbor.Ui.Framework.ViewModels` | Same VM reusable by Avalonia / WPF / MAUI / Blazor; replace `IBrush` with `string StatusBrushKey` | v0.4 (R28) |
| Create `ICommonConfigReader` in Ui.Framework | Circular dep: Ui.Framework → Desktop.Abstractions → Terminal.Abstractions → Ui.Framework; narrow interface avoids the cycle | v0.4 (R30) |
| Create `IChatViewBinder` interface | Lets `SessionManager` depend on an interface instead of `ChatViewModel` + `Dispatcher.UIThread` (Avalonia-specifics) | v0.4 (R31) |
| Decompose `MarkdownRenderer` into 4 classes | God-object (487 lines, 4 concerns) — easier to test + evolve each concern | v0.4 (R31) |
| Extract `JsonlMessageCodec` from `JsonlSessionStore` | (De)serialization is orthogonal to file I/O; codec is stateless and testable | v0.4 (R31) |

---

## 🤝 Contributing

See [AGENTS.md](../AGENTS.md) and [CLAUDE.md](../CLAUDE.md) for the working agreement.
See [docs/DEVELOPMENT.md](./DEVELOPMENT.md) for local setup.
See [docs/ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) for the canonical layering rules.
