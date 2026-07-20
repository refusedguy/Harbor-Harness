# Project Status Snapshot

> Quick-reference card: what's done, what's broken, what's next.
> For the full plan see [ROADMAP.md](./ROADMAP.md). For change history see [CHANGELOG.md](../CHANGELOG.md).
>
> **Last updated:** R31 (v0.4.0-alpha)

## Build & test status

| Check | Status |
|---|---|
| `dotnet build` (full solution) | ✅ 0 errors / 0 warnings |
| `Harbor.Abstractions.Tests` | ✅ 35/35 pass |
| `Harbor.Core.Tests` | ✅ 55/55 pass |
| `Harbor.Storage.Tests` | ✅ 27/27 pass |
| `Harbor.Storage.Jsonl.Tests` | ✅ 5/5 pass |
| `Harbor.Config.Tests` | ✅ 36/36 pass |
| `Harbor.Tools.Builtin.Tests` | ✅ 88/89 pass (1 skipped) |
| `Harbor.Providers.Tests` | ✅ 39/39 pass |
| `Harbor.Ipc.Tests` | ⚠️ 27/35 pass (8 pre-existing timing bugs) |
| `Harbor.App.Avalonia.Tests` | ⚠️ 137/138 pass (1 pre-existing Avalonia 12 headless bug) |
| `Harbor.App.Blazor.Tests` | ✅ 20/20 pass |
| `Harbor.Plugins.Runtime.Tests` | ✅ 24/24 pass (fixed in R30) |
| `Harbor.Tui.Tests` | ✅ 75/75 pass |
| `Harbor.Architecture.Tests` | ✅ All layer-dep rules enforced |
| E2E (Avalonia headless) | ✅ 12/12 pass |

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
