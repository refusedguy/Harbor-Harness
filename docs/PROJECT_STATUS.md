# Project Status Snapshot

> Quick-reference card: what's done, what's broken, what's next.
> For the full plan see [ROADMAP.md](./ROADMAP.md). For change history see [CHANGELOG.md](../CHANGELOG.md).
>
> **Last updated:** 2026-08-27 (после спринтов F1-decoupling, PROD-UI-0, ConsoleEx CE-3…CE-5, docs-pass DOCS-ZERO и добавления автономной спринт-цепочки `.kilo-docs/sprint-chain.{md,sh}`; HEAD 3625e8e). Последний полный bench+test sweep — 2026-08-22.

## Build & test status (полный прогон 2026-08-22)

Итог: **1349 выполнено → 1333 passed / 15 known-fail / 1 skipped**; 6 проектов пропущены (причины ниже). Полный clean-ребилд решения: 0 ошибок (WIP-барьер снят).

После свипа добавлены тестовые наборы ConsoleEx (`Harbor.Tui.ConsoleEx.Tests`, `Harbor.Tui.ConsoleEx.PtyTests` — CE-3…CE-5, включая golden-фикстуры и 8 PTY-сценариев) и тесты PROD-UI-0 — текущий суммарный счёт выше см. в [ROADMAP.md § Metrics](./ROADMAP.md#-metrics).

| Check | Status |
|---|---|
| `dotnet build tests/Harbor.Benchmarks -c Release` | ✅ 0 errors / 0 warnings |
| Harbor.Tui.Tests | ✅ 285/285 |
| Harbor.App.Avalonia.Tests | ✅ 211/211 |
| Harbor.Core.Tests | ✅ 73/73 |
| Harbor.Architecture.Tests | ✅ 54/54 |
| Harbor.Scripting.Tests | ✅ 51/51 |
| Harbor.Providers.Tests | ✅ 39/39 |
| Harbor.Config.Tests | ✅ 36/36 |
| Harbor.E2E.Tui.SpectreTui + Tui.E2E + E2E.Cli/Blazor/Framework | ✅ 132/132 |
| Harbor.Tools.Builtin.Tests | ✅ 138/139 (1 skip) |
| Harbor.Ipc.Tests | ⚠️ 19/27 — 8 pre-existing Linux pipe-disposal race; хост не завершается |
| Harbor.E2E.Tui.Termina | ⚠️ 34/41 — scenario-тесты рендера требуют triage |
| Harbor.Application.Tests | ✅ 34/34 |
| Harbor.App.Maui.Tests | ⛔ SKIP: global.json форсирует MTP, проект на VSTest |
| Harbor.App.Wpf.Tests | ⛔ SKIP: net10.0-windows apphost не строится на Linux |
| Harbor.E2E.App.Avalonia, E2E.Tui.TerminalGui | ⛔ SKIP: зависание headless-хоста ×2 |
| Harbor.Ui.Framework.Tests | ✅ 47/47 (файлы проекта исправлены; полный clean-ребилд блокирует только чужой WIP в AgentLoop.cs) |

## Benchmarks (23 класса, Release, 2026-08-22)

Топ bottlenecks: AppReducer streaming O(N²) (19.4 MB/1000 дельт), MessageConverter large-msg
(1.2 MB/msg), Compaction full-scan per turn (598 µs @1000), EventBroadcaster (8 MB/1000 событий),
EventBus fixed alloc (8.1 KB/publish). Полная таблица и план P0–P3 — docs/BENCHMARKS.md.

## Recent milestones

- **DOCS-ZERO + sprint chain** (27.08) — кросс-чек документации по коду (ROADMAP/PROJECT_STATUS, README+PLAN групп проектов, AGENTS/CLAUDE/README корневые, 233 ссылки — 0 битых); добавлена автономная спринт-цепочка `.kilo-docs/sprint-chain.md` (очередь `sprint|NAME|MODEL|PROMPT_PATH`) + `scripts/sprint-chain.sh` (диспетчер через kilo-dispatch с проверкой прогресса по SHA)
- **ConsoleEx CE-3…CE-5** (26.08) — второй рендер REPL: virtualized timeline, streaming markdown, DiffBlock, StatusSegmentBar/SpinnerStrip, perf-бюджеты (0 alloc steady-state), живой REPL c `ui.consoleEx{enabled,syncUpdates}`, PTY e2e (`PtyHarness`), фикс Termios struct 49→60 байт
- **PROD-UI-0** (25–26.08) — единый каталог провайдеров из `ProviderPresets`, `IProviderHealthCheck` («Test connection»), `/model` перепривязывает сессию без рестарта REPL, живые списки моделей в onboarding-визардах
- **F1 decoupling** (24.08) — `Harbor.Domain.dll` → `Harbor.Abstractions.Contracts`; ADR-008 (DECISIONS.md); замеры rebuild-set в docs/BUILD.md (commit 7449cd0)
- **R31** — God-object decomposition: MarkdownRenderer (487→110+4 classes), JsonlSessionStore (688→528+codec), SessionManager (495→492+IChatViewBinder)
- **R30** — Plugin system bug fix (24/24 tests pass), business-logic extraction (6 files moved from Avalonia to Ui.Framework)
- **R29** — Blazor + WPF ports of reusable components (StatusBadge, ChatBubble, SessionRow)
- **R28** — Platform-agnostic ToolCallViewModel, StatusMappers, 3 reusable Avalonia components, StatusBarViewModel extraction
- **R25-R26** — Concurrent per-session agents (UiStore per SessionContext, EventBus routing by SessionId, no abort on session switch)

## What's currently working end-to-end

1. **CLI**: `dotnet run --project apps/Harbor.App.Cli -- ask "..."` — Kilocode free model verified
2. **Avalonia desktop GUI**: full chat + tool-call cards + sidebar + onboarding wizard + provider picker
3. **ConsoleEx REPL** (opt-in): `HARBOR_TUI=consoleex` или `tui: "consoleex"` в `~/.harbor/config.json` — alt-screen cell-diff рендер, kitty keyboard/mouse/paste, Ctrl+C = abort → повтор = выход (CE-4)
4. **Blazor Server** (`contrib/apps/`): chat + sidebar + sessions list (uses shared StatusMappers)
5. **Plugin compilation**: Roslyn CS-source plugins compile + load (CS-samples; 8 проектов `Harbor.Plugins.*`)
6. **Concurrent sessions**: multiple sessions can run agents in parallel, each routed to its own UiStore

## Known broken / pre-existing

- **CI `build` job red on `dev` (Sep 2026, fixed in worktree):** unresolved merge markers (`CS8300`) in `src/Harbor.CodeGen/{EscapeCodeGenerator,MoodFrameGenerator}.cs` from the codegen-boilerplate merge, a missing CodeGen-analyzer reference in `Harbor.Terminal.Abstractions`, HEAD-side consumers (`Harbor.CodeGen.*Attribute`, hand-rolled `EscapeCodes`) vs sprint-side contracts, plus Avalonia-12 API drift (`Selection.StartOffset`, `Dispatcher.UIThread`, `HierarchicalDataTemplate`/`TreeView.Virtualize`). All fixed; `dotnet build Harbor.slnx -c Release` is green (0 errors).
- **`dotnet test` discovers zero tests repo-wide** (MTP bridge exits 5 with one silent discovery error; same DLLs run green via `dotnet run --project` / direct exec). CI + NUKE + docs switched to `dotnet run --project tests/<X> -- --minimum-expected-tests 1` (issue #24).
- **Golden CRLF trap on Windows:** `*.golden.txt` blobs are LF; without normalization Windows checkouts (CRLF) fail string compares. Fixed via `Golden.Normalize` in both helpers + `.gitattributes` (`*.golden.txt text eol=lf`).
- **Windows-only test isolation leak:** `GetFolderPath(UserProfile)` ignores a swapped `USERPROFILE` env, so `Build_Registers_CommonConfig` read the dev-box config. Fixed via `HARBOR_HOME` override (`HarborPaths`) + test isolation.
- **3 Avalonia 12 headless test failures** (на момент свипа 22.08): `MarkdownRenderer_SetMarkdown_DoesNotThrow`, `CodeBlock_Default_Code_IsEmpty`, `TypewriterStreamingText_CanSet_Text` — fail with `InvalidOperationException: Stack empty` in `AvaloniaPropertyDictionaryPool.Get()`. Похоже на баг пакета Avalonia.Headless, не Harbor-кода; re-check ROP-D (25.08) фиксировал красными только пару флакующих `ChatView_Inflates` / `TryGet_ReturnsNullForUnregistered` (introduced 61ee126).
- **8 IPC timing-test failures** on Linux: named-pipe disposal race in `Harbor.Ipc.Tests`. Tests pass on Windows.
- **Pre-existing reds verified on Windows (untouched by current work):** `PatchTool` ×3 (`AppliesSimpleAdditionPatch`, `AppliesModificationPatch`, `PreviewIncludesAddedAndRemovedLines`), `McpProcessClientStartInfoTests.Register_StartInfo_SpawnsWithArgsEnvCwd` (`KeyNotFoundException 'cwd'`), markdown `TokenByToken_EqualsWholeDocument_AtWidth` ×3 + `RandomChunkSplits_ProduceIdenticalFinalLines`, PTY `Enter_WithNonTtyStdin` (`DllNotFoundException 'libc'`), permission `CheckAsync_PersistedAllowDecision_SecondCallDoesNotPromptAgain` (fails on pristine tree too) + `ConcurrentStress`/`StealStorm` flakes.

## Top 3 next steps (recommended priority)

1. **Decompose `AgentLoop.cs` (681 lines)** — extract `ToolDispatcher` (parallel/sequential tool execution) + `RetryPolicy` (exponential backoff for transient LLM errors) + `TokenTracker` (usage aggregation). Same pattern as R31.
2. **Decompose `OpenAILlmClient.cs` (656 lines)** — extract `OpenAiSseParser` (SSE line parsing) + `OpenAiEventMapper` (chunk → LlmEvent mapping). Will also benefit `AnthropicLlmClient` (562 lines).
3. **Wire reusable components into MainWindow.axaml** — currently `StatusBadge`/`ChatBubble`/`SessionRow` are defined but not yet used in the actual chat view / sidebar. Replace inline templates with `<comp:StatusBadge .../>` etc.

## Tech-debt backlog

See [ROADMAP.md § Tech Debt](./ROADMAP.md#-tech-debt--refactor-backlog-post-r31) for the
full backlog. Highlights:

- `HarborConfig.cs` (492 lines) — split into per-section records
- `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` (644 lines) — decompose into per-concern registrars (mirror Avalonia `Hosting/`)
- Move `SessionManager` to `Ui.Framework` once `TokenUsageViewModel` dependency abstracted
- E2E test coverage: 12 → 33+ component tests with VLM content verification

## Where to find things

| What | Where |
|---|---|
| Domain contracts | `src/Harbor.Abstractions/`, `src/Harbor.Abstractions.Contracts/` (бывший Harbor.Domain, F1 decoupling) |
| UI framework (TEA, VMs, components) | `src/Harbor.Ui.Framework.*` (9 проектов: State, ViewModels, Services, Sessions, Projection…) |
| Application layer (AgentLoop, registries) | `src/Harbor.Core/`, `src/Harbor.Application/`, `src/Harbor.Registries/` |
| Plugin system | `src/Harbor.Plugins.{Abstractions,Storage,Compilation,Instantiation,Registration,Hosting,Runtime}/` + out-of-process `Harbor.Plugins.Host/` (exe) |
| Scripting (SharpTS + Jint) | `contrib/scripting/Harbor.Scripting.*/` (moved to contrib) |
| IPC (MessagePack over pipe/UDS) | `src/Harbor.Ipc.{Abstractions,Client,Server,InProcess}/` + `src/Harbor.Transport.Remote/` |
| Storage backends | `src/Harbor.Storage.{Jsonl,Memory,Sqlite}/` |
| LLM providers | `src/Harbor.Providers.{Anthropic,OpenAI,Ollama,OpenAiCompatible,Shared}/` |
| Builtin tools (18) | `src/Harbor.Tools.Builtin/Tools/` (read/write/edit/bash/glob/grep/ls/task/webfetch/patch/notebook/ripgrep/tree/mcp/skill/read_mcp_resource/mcp_prompt и др.) |
| Terminal TUI renderers | in-solution: `src/Harbor.Tui.{Ansi,Plain,ConsoleEx,Notifications}/`; optional (`contrib/tui/`): Spectre, Spectre.Fullscreen, SpectreTui, TerminalGui, Termina, RazorConsole, Sixel |
| Platform apps (composition roots) | `apps/Harbor.App.{Cli,Avalonia}/`; WPF / MAUI / Blazor — `contrib/apps/` |
| Reusable UI components | `apps/Harbor.App.Avalonia/Views/Components/`, `contrib/apps/Harbor.App.Blazor/Components/Shared/`, `contrib/apps/Harbor.App.Wpf/Controls/` |
| Tests | `tests/Harbor.*.Tests/`, `tests/Harbor.E2E.*/`, `tests/Harbor.Benchmarks/` (+ contrib-наборы в `contrib/tests/`) |
| Specs (formal design) | `specs/00-overview.md` … `specs/15-providers-dynamic.md` |
| Docs (arch, dev, plugin) | `docs/` |
| Sample plugins | `samples/plugins/` (DLL legacy), `samples/plugins-cs/` (Roslyn CS-source) |
| Provider JSON configs | `providers/*.json` |

## How to run things

```bash
# Build everything (main solution = .slnx; .sln не существует)
dotnet build

# Run CLI
dotnet run --project apps/Harbor.App.Cli -- ask "What is 2+2?"

# Run Avalonia desktop
dotnet run --project apps/Harbor.App.Avalonia

# Run Blazor web (contributes component moved out of main solution)
dotnet run --project contrib/apps/Harbor.App.Blazor
# → open http://localhost:5000

# Запуск тестов ВАЖНО: `dotnet test` в этом репо находит НОЛЬ тестов (сломанный
# MTP-bridge: хост выходит с кодом 5 и одной silent-ошибкой discovery).
# Прогоняйте per-project как обычные exe:
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --minimum-expected-tests 1
dotnet run --project tests/Harbor.Tui.ConsoleEx.PtyTests -c Release --no-build   # CE-5 PTY e2e

# Run plugin tests (the ones that were broken in R29 and fixed in R30)
dotnet run --project tests/Harbor.Plugins.Runtime.Tests -c Release --no-build -- --minimum-expected-tests 1

# Enforce layer-dep rules
dotnet run --project tests/Harbor.Architecture.Tests -c Release --no-build -- --minimum-expected-tests 1
```

## Key environment variables

| Variable | Purpose | Example |
|---|---|---|
| `KILO_API_KEY` | Kilocode provider (free models available) | `klo_xxx...` |
| `ANTHROPIC_API_KEY` | Anthropic native provider | `sk-ant-...` |
| `OPENAI_API_KEY` | OpenAI native provider | `sk-...` |
| `OPENROUTER_API_KEY` | OpenRouter (200+ models) | `sk-or-...` |
| `HARBOR_MODEL` | Override active model | `kilocode/tencent/hy3:free` |
| `HARBOR_TUI` | Pick TUI renderer (`ansi`/`plain`/`consoleex`; `spectre-tui` по умолчанию, семейство Spectre живёт в `contrib/tui`) | `plain` |
| `HARBOR_SHELL` | `orca` for experimental Orca shell | `orca` |
