# Roadmap

> Harbor development roadmap. Last updated: 2026-08-27 (post CE-5 / PROD-UI-0 sprints and the
> DOCS-ZERO docs pass; autonomous sprint-chain infra lives in [`.kilo-docs/`](../.kilo-docs/) —
> `sprint-chain.md` queue + `scripts/sprint-chain.sh` dispatcher, HEAD 3625e8e).
> See [CHANGELOG.md](../CHANGELOG.md) for the per-release change history.
>
> **Sprint-2 note:** optional UI/scripting components moved to [`contrib/`](../contrib/)
> (`contrib/tui/`, `contrib/apps/`, `contrib/scripting/`, `contrib/tests/`; build via
> `contrib/Contrib.slnx`). They still exist and work — just outside `Harbor.slnx`.

## Current State: v0.4.0-alpha → post-CE-5 (2026-08-27)

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

### ✅ Completed — UI (2 apps in `apps/` + 5 TUI projects in src/ + optional contrib components)

- **TUI (in `Harbor.slnx`)**: `Harbor.Tui.Ansi`, `Plain`, **`ConsoleEx`** (alt-screen cell-diff MVP, CE-0…CE-5), `Notifications`, + `Harbor.Tui.Abstractions`
- **TUI (optional, `contrib/tui/`)**: Spectre, Spectre.Fullscreen, SpectreTui, TerminalGui, Termina, RazorConsole, Sixel — wired via the `HARBOR_WITH_SPECTRE_TUI` build flag; interactive default TUI id is still `spectre-tui`
- **Desktop**: Avalonia (`apps/Harbor.App.Avalonia`); WPF, MAUI (`contrib/apps/`)
- **Web**: Blazor Server (`contrib/apps/Harbor.App.Blazor`)

### ✅ Completed — ConsoleEx MVP (CE-0…CE-5)

Second render path for the interactive REPL (`src/Harbor.Tui.ConsoleEx/`, opt-in only):

- **Input** (CE-0): kitty keyboard protocol, SGR mouse, bracketed paste with anti-injection; raw-mode via termios P/Invoke
- **Rendering** (CE-1): `ScreenBuffer` → `DiffEngine` cell-diff frames through `AnsiWriter` (SGR automaton, cursor elision, DECSYNC wrapper); resize policy à la ratatui
- **Widgets** (CE-2/3): virtualized chat timeline with byte-budget ring, streaming markdown with pacer-gated reveal, tool-call cards with unified-diff bodies, status segment bar + tick-driven spinner, multi-line composer
- **Live REPL wire-up** (CE-4): select via `HARBOR_TUI=consoleex` or `tui: "consoleex"` (+ kill-switch `ui.consoleEx.enabled`), `ConsoleExModule` DI graph, event pump keeps all timeline mutation on the frame thread, Ctrl+C = abort turn → second press quits, event-driven frames + 80 ms spinner heartbeat, golden E2E smoke (`tests/fixtures/celldiff/ce4-consoleex-repl.golden.txt`). See [README](../src/Harbor.Tui.ConsoleEx/README.md).
- **PTY hardening** (CE-5): `PtyHarness` runs the real process in a pseudo-terminal (`tests/Harbor.Tui.ConsoleEx.PtyTests/`, 8 scenarios — launch/submit/kitty/mouse/paste/resize/Ctrl+C/termios); fixed Termios struct size 49→60 bytes (kernel wrote past struct → stack corruption in raw-mode Enter, commit 1749841).

### ✅ Completed — Tools (14 builtin)

`read`, `write`, `edit`, `bash`, `glob`, `grep`, `ls`, `task`, `webfetch`, `patch`, `notebook`, `ripgrep`, `tree`, `mcp`

### ✅ Completed — Plugin System (R30 fix) + Plugin Host Decomposition (F-sprints)

- `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin` contracts (`src/Harbor.Abstractions/Plugins/IPlugin.cs`, incl. `RequiredHarborVersion`)
- Roslyn-based CS-source plugin compiler with disk-cache decorator (`Harbor.Plugins.Compilation/RoslynPluginCompiler.cs` + `CachingCompiler.cs`)
- Dedicated plugin projects: `Harbor.Plugins.{Abstractions, Storage, Compilation, Instantiation, Registration, Hosting, Runtime}` (source → compile → instantiate → register → host pipeline) + `Harbor.Plugins.Host` (out-of-process MCP plugin server exe)
- 4 sample CS-source plugins: `samples/plugins-cs/`; 4 legacy DLL samples: `samples/plugins/Harbor.Plugin.{WebSearch,TodoWrite,GitTools,FileTree}`
- **R30 bug fix**: Roslyn now sees `Harbor.Abstractions.Models.*` (types physically in Harbor.Domain.dll at the time; since renamed to `Harbor.Abstractions.Contracts`, see Decision Log) via explicit `typeof(Session).Assembly` reference

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

- ~1350 tests across 20+ test projects in `tests/` (latest full sweep 2026-08-22: 1333 passed / 15 known-fail / 1 skipped — see [PROJECT_STATUS.md](./PROJECT_STATUS.md) for the per-project table; ConsoleEx CE-3…CE-5 suites added since)
- 12 E2E tests (Avalonia headless + CLI) + ConsoleEx PTY e2e suite
- TUnit framework (source-generated, fastest .NET test runner)
- 7 analyzer packages, 0 warnings, 0 errors

---

## 📋 Roadmap — by version

### v0.5.0 — Plugin System & Sub-agents

> **Статус (обновлено 2026-08-27):** plugin-половина вехи выполнена раньше плана —
> декомпозирована в 8 проектов `Harbor.Plugins.*` в спринтах F/ROP-D (см. ✅ Completed
> выше и git log за 24–26.08). Sub-agent execution по-прежнему НЕ реализован.

**Plugin loading** *(выполнено, механизм отличается от исходного плана)*
- [x] Plugin host: `Harbor.Plugins.Hosting` (`PluginHost`, `PluginHostBuilder`) + out-of-process host `Harbor.Plugins.Host` (exe, MCP stdio server). Исходный план «AssemblyLoadContext (JIT mode)» заменён Roslyn CS-source pipeline: `Harbor.Plugins.Runtime/CsPluginLoader.cs` компилирует `.cs` из `~/.harbor/plugins/` in-memory; DLL-based путь остался только у legacy сэмплов
- [x] Plugin discovery from `~/.harbor/plugins/*.cs` (+ embedded-resource / in-memory / composite sources) — `Harbor.Plugins.Storage/FileSystemPluginSource.cs` et al.
- [x] Plugin trust prompt for project-local plugins — `IPluginTrustPolicy` + `TrustingPluginSource` fail-closed gate, `FileTrustPolicy` persist store path+sha256 (`~/.harbor/plugins/trust.json`, re-approval after edit), interactive y/N at startup; global scope implicitly trusted
- [x] Hot-reload via `FileSystemWatcher` — `DebouncedPluginWatcher` (generational debounce, last-event-wins) → `PluginReloadService` re-runs the startup pipeline into live registries; `/plugins reload` in REPL + auto-reload gated by `tooling.autoReloadPlugins`; edited/removed plugins still need a restart to fully rebind (unregister tracking is follow-up)
- [x] `harbor plugin install/list/uninstall` CLI commands
- [x] Build flag default: plain `dotnet build` now defines `HarborWithPlugins=true` (root `Directory.Build.props`) — previously the whole pipeline was dead code outside NUKE builds ("Feature flags: plugins=False")

**Sub-agent execution** *(частично: TaskTool валидирует sub-agent, но не запускает его — `src/Harbor.Tools.Builtin/Tools/Task/TaskTool.cs`: «does NOT run it yet… execution fails with an explicit "not implemented" error»)*
- [x] Wire `TaskTool` to agent registry — Tool внедряет `IAgentRegistry`, проверяет `IsSubAgent` (G4)
- [ ] Real sub-agent runner: sub-agent runs in isolated context with own session
- [ ] Result returned to parent agent as tool output
- [ ] Support `explore`, `plan`, custom sub-agents

**TUI plugins**
- [x] `ITuiPlugin.RegisterTui(ViewRegistry, ViewModelRegistry)` — контракт существует, панельный адаптер `Harbor.Plugins.Registration/PanelRegistryPluginAdapter.cs`
- [ ] Sample TUI plugin (e.g. token usage chart)
- [ ] Plugin views override builtin views

### v0.6.0 — MCP Integration

> **Статус (обновлено 2026-08-27):** ядро MCP-клиента реализовано раньше плана
> (собственная реализация без NuGet `ModelContextProtocol`) в `src/Harbor.Tools.Builtin/Tools/Mcp/`.
> Невыполненное оставлено как план.

- [x] MCP client — собственная реализация (не NuGet): `McpRegistry.cs`, `McpProcessClient.cs`, JSON-RPC транспорт `McpJsonRpcTransport.cs`, source-gen сериализация `McpJsonSerializerContext.cs`
- [x] Stdio transport (`McpServerStartInfo.cs`, `ProcessTree.cs`)
- [x] MCP tools → `ITool` adapter (`McpToolAdapter.cs`, surfaced via tool `mcp`; instructions aggregation — `McpRegistryInstructionsTests.cs`)
- [x] Config: loading `~/.harbor/mcp.json` (industry schema) — `McpServersConfigLoader.cs` + `McpServersConfig.cs`
- [ ] HTTP, SSE transports
- [ ] MCP resources as `read_mcp_resource` tools
- [ ] MCP prompts as slash-commands
- [x] OAuth for MCP servers (`McpOAuthConfig` auth block, PKCE browser flow with loopback redirect, refresh, file cache, `harbor mcp login/logout`, `HARBOR_MCP_OAUTH_TOKEN` static fallback)
- [ ] Lazy connect, reconnect on failure

### v0.7.0 — Skills & LSP

**Skills**
- [ ] `SKILL.md` format with YAML frontmatter
- [ ] Discovery from `~/.harbor/skills/` and `.harbor/skills/`
- [ ] `skill` tool to load skill content
- [ ] `<available_skills>` XML in system prompt
- [ ] `harbor skill install/list` CLI commands

**LSP Integration**
- [x] LSP client (own stdio JSON-RPC implementation, no NuGet — `LspManager`/`LspClient`/`LspServerSession` in `src/Harbor.Lsp/`, AOT-safe source-gen wire format)
- [x] 11 builtin language servers (TypeScript, Python, Go, Rust, C#, C/C++ via clangd, Java via jdtls, HTML/CSS/JSON via vscode-*-language-server, Lua)
- [x] Auto-spawn on file open (`LspManager` lazy sessions + `read` tool opens files, `edit` pushes changes)
- [x] `diagnostics`, `references`, `definition` tools (`lsp` tool, registered when `ILspService` present; graceful degradation with explanatory results when a binary is missing)
- [x] LSP-aware `read`/`edit` (reads auto-open supported files; edits notify + append a diagnostics summary line)

### v0.8.0 — Session Management Polish

- [x] Session branching (`harbor sessions fork <message-id>`) — `SessionForkRunner` copies history inclusive of the cut point into a NEW session (lineage via `ParentSessionId`, `(fork)` title); source untouched
- [ ] `/tree` slash-command for branch navigation
- [ ] Branch summaries (LLM-generated on branch switch)
- [x] Snapshot/revert (`harbor sessions revert <message-id>`)
- [x] Session search (`harbor sessions search <query>`)
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
| `Harbor.Providers.OpenAI/OpenAILlmClient.cs` | 656 | HTTP + SSE parsing + event mapping + models endpoint | Extract `OpenAiSseParser` + `OpenAiEventMapper` |
| `Harbor.Application/Configuration/HarborConfig.cs` | 492 | mega-record with every config field | Split into per-section records (`ProviderConfig`, `ToolConfig`, `UiConfig`, etc.) |
| `Harbor.Providers.Anthropic/AnthropicLlmClient.cs` | 562 | similar to OpenAI | Same decomposition pattern |
| `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` | 644 | DI registration + config + provider/tool wiring | Decompose into per-concern registrars (mirror Avalonia `Hosting/`) |
| `apps/Harbor.App.Avalonia/Services/SessionManager.cs` | 492 | orchestration + UI dispatch + agent lifecycle | Move to `Ui.Framework` once `TokenUsageViewModel` dependency abstracted |

### Architectural debt

- [x] `Harbor.Desktop.Abstractions` namespace drift: 4 files declared `namespace Harbor.App.Avalonia.ViewModels` while living in `Harbor.Desktop.Abstractions/ViewModels/`. **Verified fixed (ROP-D Z1, 25.08): every file in the project now declares `Harbor.Desktop.Abstractions.*`.**
- [x] Namespace drift `Harbor.Core.*` in Harbor.Application.dll (42 declarations) and Harbor.Registries.dll (3) — migrated to assembly-matching namespaces (ROP-D Z1).
- [x] Namespace drift `Harbor.Cli.*` in Harbor.App.Cli — 18 declarations + all references migrated to `Harbor.App.Cli.*`; remaining census findings documented as intentional at migration time (compat namespaces in Contracts/Extensions/Registries; same-family sub-namespaces in Ipc/Telemetry/Ui.Framework) (ROP-D Z1, 25.08; see commit 9ec5c7b).
- [x] BannedApi.txt was dead (never wired, wrong filename for the analyzer) — renamed to `BannedSymbols.txt`, wired via AdditionalFiles in Directory.Build.props; all 9 production GetResult sites resolved or pragma-exempted with a catalogued reason (ROP-D Z2, commit be81e42).
- [x] Arch tests probed the wrong assembly for "TuiAbstractions" (typeof(UiStore) from Ui.Framework.State); retargeted to Terminal.Abstractions + new UiFrameworkState rule (ROP-D Z2, commit 9060475).
- [x] ~28 src projects outside arch enforcement → FullLayerMatrixTests covers all 45 main-solution src assemblies (reference check + table guard + exception liveness + coverage guard); stale `dotnetarch.json` deleted in favor of the single enforcement surface (ROP-D Z2, commits 5d2df19/6566be2).
- [x] SystemPromptContext.McpInstructions hardwired null → IMcpRegistry.GetInstructions() aggregates mcp.json hints + initialize responses; WorkspaceContextSource.FormatMcpInstructions feeds the prompt via AgentLoop DI (ROP-D Z3, commit 64cbc0e).
- [x] ResultGuard survived the §4.5 deletion order and gained a new production call (ConfigStore.LoadCore) → deleted as the duplicate canon: ConfigStore load/save and TreeTool now ride CSE `Result.Try` + `ResultErrors.Message` (save path no longer masks OCE); tests ported to ResultErrorsTests (ROP-D Z3 tail, commit 9e954a5).
- [x] ROP-D final verification (25.08): `Harbor.slnx -c Release` builds with 0 errors; full cycle over all 20 `tests/Harbor.*.Tests` projects — итог 1184 tests: 1176–1178 passed, 6 skipped, 1–2 failed = the two pre-existing Avalonia headless flakes `ChatView_Inflates` / `TryGet_ReturnsNullForUnregistered` (61ee126; flake count varies per run). Re-verified in a fresh ROP-D close-out session: same numbers, both failures confined to the known-flake pair. Enforcement matrix: 47/50 src dirs under arch rules; the other 3 documented out-of-scope (CodeGen build tool, Plugins.Host exe, Providers.Shared linked source) (commit b62de73).
- [x] ROP-D tail-closure re-verification (25.08, after commit 9e954a5): Release build 0 errors (only pre-existing NU1903/NU1901 NuGet-audit advisories); full cycle over all 20 `tests/Harbor.*.Tests` projects — итог 1188 tests (+4 from ResultGuard→ResultErrors test port): 1180 passed, 6 skipped, 2 failed = the same known Avalonia flake pair only.
- [x] God-object decomposition `Harbor.Application/Agents/AgentLoop.cs`: tool dispatch → `ToolDispatcher` + `IToolDispatcher` seam (parallel / sequential fan-out, permission gating, per-call timeout), transient-failure retry with capped exponential backoff + jitter → `Resilience/RetryPolicy`, usage aggregation + O(1) compaction checks → `TokenTracker`/`ITokenTracker`; streaming buffering lives in `StreamingCoalescer`. All behind DI (`CoreModule`); public `IAgentLoop.RunAsync` contract unchanged (AGENTLOOP-DECOMP, commit 31f8859).
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
- [ ] Per-project READMEs: 19 of 51 src projects still have no README (coverage **32/51**; major groups rewritten against real code in DOCS-ZERO D1a/b, D2a/b, D3 — commits b9e6010…9ec5c7b, 2026-08-27) — write one when a project is next touched.
- [x] Architecture decision records exist: `DECISIONS.md` (ADR-001…ADR-008 — production stabilization, ROP rails, ConsoleEx, sub-agent `task`, plugin hosting split, MCP adapter, ProviderPresets catalog, F1 Abstractions.Contracts decoupling).

---

## 🎯 Killer Features (from competitor audit `docs/KILLER_FEATURES.md`)

### ✅ Implemented

- Animated streaming text with typewriter cursor (`TypewriterStreamingText` control — `apps/Harbor.App.Avalonia/Views/Controls/`)
- Collapsible tool-call cards with status + duration (`ToolCallCardView`)
- Token-usage sparkline in status bar (`Sparkline` control)
- Toast notifications with slide-in + auto-dismiss (`ToastService` + `ToastNotificationsView`; `src/Harbor.Tui.Notifications/` for the TUI side)
- Provider/model picker with search + auth status (`ProviderModelPickerViewModel`, `/model` rebind + provider health check from PROD-UI-0)
- Onboarding wizard with stepper dots (`OnboardingWindow.axaml`: "Progress stepper: 5 dots")
- Command palette with fuzzy filter + command/session/file sources (`CommandPaletteViewModelBase.FuzzyScore`, re-verified 2026-08-27)

### ⚠️ Partial

- Markdown rich editor (basic rendering, no TipTap-class editor)
- Intra-line word-diff highlighting (line-level only; ConsoleEx `DiffBlock` renders unified diffs by line)

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
| Source projects (`src/`) | 51 (+26 in `contrib/`) |
| Test projects (`tests/`, csproj dirs) | 27 (incl. benchmarks + E2E harnesses) |
| Unit tests passing | ~1350 (см. [PROJECT_STATUS.md](./PROJECT_STATUS.md)) |
| E2E tests passing | 12 + ConsoleEx PTY suite |
| Builtin tools | 14 |
| Sample plugins | 4 CS-source + 4 DLL legacy |
| TUI renderers | 5 in src/ (Ansi, Plain, ConsoleEx, Notifications, +Abstractions) + 7 in contrib/tui |
| Desktop platforms | Avalonia (`apps/`); WPF / MAUI (`contrib/apps/`) |
| Web platforms | Blazor Server (`contrib/apps/`) |
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
| **Reverse the split**: `Harbor.Domain.dll` renamed to `Harbor.Abstractions.Contracts` (F1 decoupling) | Full decoupling of Abstractions from external callers; see ADR-008 in [`DECISIONS.md`](../DECISIONS.md) and commit fa8d3ae (2026-08-24). Namespace stays `Harbor.Abstractions.Models` | 2026-08-24 |
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
