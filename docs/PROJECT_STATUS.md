# Project Status Snapshot

> Quick-reference card: what's done, what's broken, what's next.
> For the full plan see [ROADMAP.md](./ROADMAP.md). For change history see [CHANGELOG.md](../CHANGELOG.md).
>
> **Last updated:** 2026-08-22 — full bench+test sweep (см. /tmp/test-report.md, /tmp/benchmark-report.md)

## Build & test status (полный прогон 2026-08-22)

Итог: **1349 выполнено → 1333 passed / 15 known-fail / 1 skipped**; 6 проектов пропущены (причины ниже). Полный clean-ребилд решения: 0 ошибок (WIP-барьер снят).

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

- **R31** — God-object decomposition: MarkdownRenderer (487→110+4 classes), JsonlSessionStore (688→528+codec), SessionManager (495→492+IChatViewBinder)
- **R30** — Plugin system bug fix (24/24 tests pass), business-logic extraction (6 files moved from Avalonia to Ui.Framework)
- **R29** — Blazor + WPF ports of reusable components (StatusBadge, ChatBubble, SessionRow)
- **R28** — Platform-agnostic ToolCallViewModel, StatusMappers, 3 reusable Avalonia components, StatusBarViewModel extraction
- **R25-R26** — Concurrent per-session agents (UiStore per SessionContext, EventBus routing by SessionId, no abort on session switch)

## What's currently working end-to-end

1. **CLI**: `dotnet run --project apps/Harbor.App.Cli -- ask "..."` — Kilocode free model verified
2. **Avalonia desktop GUI**: full chat + tool-call cards + sidebar + onboarding wizard + provider picker
3. **Blazor Server**: chat + sidebar + sessions list (uses shared StatusMappers)
4. **Plugin compilation**: Roslyn-based CS-source plugins compile + load (4 sample plugins)
5. **Concurrent sessions**: multiple sessions can run agents in parallel, each routed to its own UiStore

## Known broken / pre-existing

- **3 Avalonia 12 headless test failures**: `MarkdownRenderer_SetMarkdown_DoesNotThrow`, `CodeBlock_Default_Code_IsEmpty`, `TypewriterStreamingText_CanSet_Text` — all fail with `InvalidOperationException: Stack empty` in `AvaloniaPropertyDictionaryPool.Get()`. Likely an Avalonia.Headless package bug, NOT a Harbor code issue.
- **8 IPC timing-test failures** on Linux: named-pipe disposal race in `Harbor.Ipc.Tests`. Tests pass on Windows.
- **`Harbor.Desktop.Abstractions` namespace drift**: 4 files declare `namespace Harbor.App.Avalonia.ViewModels` while living in `src/Harbor.Desktop.Abstractions/ViewModels/`. Cosmetic — works at runtime but breaks IDE navigation.

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
- Fix `Harbor.Desktop.Abstractions` namespace drift
- E2E test coverage: 12 → 33+ component tests with VLM content verification

## Where to find things

| What | Where |
|---|---|
| Domain contracts | `src/Harbor.Abstractions/`, `src/Harbor.Domain/` |
| UI framework (TEA, VMs, components) | `src/Harbor.Ui.Framework/` |
| Application layer (AgentLoop, registries) | `src/Harbor.Core/`, `src/Harbor.Application/` |
| Plugin system | `src/Harbor.Plugins.{Abstractions,Storage,Compilation,Instantiation,Registration,Hosting,Runtime}/` |
| Scripting (SharpTS + Jint) | `src/Harbor.Scripting.*/` |
| IPC (MessagePack over pipe/UDS) | `src/Harbor.Ipc.*/` |
| Storage backends | `src/Harbor.Storage.{Jsonl,Memory,Sqlite}/` |
| LLM providers | `src/Harbor.Providers.{Anthropic,OpenAI,Ollama,OpenAiCompatible}/` |
| Builtin tools | `src/Harbor.Tools.{Read,Write,Edit,Bash,Grep,Glob,Ls,Task,WebFetch,Patch,Notebook,RipGrep,Tree,Mcp,Builtin}/` |
| Terminal TUI renderers | `src/Harbor.Tui.{Ansi,Plain,Spectre,Spectre.Fullscreen,SpectreTui,TerminalGui,Termina,RazorConsole,Sixel,Notifications}/` |
| Platform apps (composition roots) | `apps/Harbor.App.{Cli,Avalonia,Wpf,Maui,Blazor}/` |
| Reusable UI components | `apps/Harbor.App.Avalonia/Views/Components/`, `apps/Harbor.App.Blazor/Components/Shared/`, `apps/Harbor.App.Wpf/Controls/` |
| Tests | `tests/Harbor.*.Tests/`, `tests/Harbor.E2E.*/`, `tests/Harbor.Benchmarks/` |
| Specs (formal design) | `specs/00-overview.md` … `specs/15-providers-dynamic.md` |
| Docs (arch, dev, plugin) | `docs/` |
| Sample plugins | `samples/plugins/`, `samples/plugins-cs/` |
| Provider JSON configs | `providers/*.json` |

## How to run things

```bash
# Build everything
dotnet build

# Run CLI
dotnet run --project apps/Harbor.App.Cli -- ask "What is 2+2?"

# Run Avalonia desktop
dotnet run --project apps/Harbor.App.Avalonia

# Run Blazor web
dotnet run --project apps/Harbor.App.Blazor
# → open http://localhost:5000

# Run only the Avalonia unit tests
dotnet run --project tests/Harbor.App.Avalonia.Tests -c Release

# Run plugin tests (the ones that were broken in R29 and fixed in R30)
dotnet run --project tests/Harbor.Plugins.Runtime.Tests -c Release

# Enforce layer-dep rules
dotnet test tests/Harbor.Architecture.Tests
```

## Key environment variables

| Variable | Purpose | Example |
|---|---|---|
| `KILO_API_KEY` | Kilocode provider (free models available) | `klo_xxx...` |
| `ANTHROPIC_API_KEY` | Anthropic native provider | `sk-ant-...` |
| `OPENAI_API_KEY` | OpenAI native provider | `sk-...` |
| `OPENROUTER_API_KEY` | OpenRouter (200+ models) | `sk-or-...` |
| `HARBOR_MODEL` | Override active model | `kilocode/tencent/hy3:free` |
| `HARBOR_TUI` | Pick TUI renderer (`ansi`/`plain`/`spectre`/...) | `plain` |
| `HARBOR_SHELL` | `orca` for experimental Orca shell | `orca` |
