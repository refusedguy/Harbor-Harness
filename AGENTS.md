# AGENTS.md — Guide for AI agents working on Harbor

> This file is read by AI coding agents (Claude Code, Cursor, etc.) when making changes to Harbor. It contains operational guidance, not just conventions. Cross-references [CLAUDE.md](./CLAUDE.md) for conventions.
>
> **Quick state (branch dev):**
> - Solutions are `.slnx` files: `Harbor.slnx` (main) and `Harbor.Samples.slnx`; no plain `.sln`.
> - Tests must be run **per project** (`dotnet test tests/<Project> -c Release --no-build`); whole-solution test invocations currently break under the Microsoft.Testing.Platform (MTP) host — do not rely on them.
> - Known/flaky tests historically cited (verify against `docs/ROADMAP.md` before counting on current numbers): Avalonia-12 headless `MarkdownRenderer`/`CodeBlock`/`TypewriterStreamingText` ("Stack empty" in `SetInheritanceParent`), the IPC named-pipe event-stream class on Linux (self-skips unless `HARBOR_IPC_EVENTSTREAM=1`), and an occasional `ChatView_Inflates` ListBoxItem `StaticResource` flake.
> - ConsoleEx (second in-process terminal renderer) MVP is complete and opt-in via `HARBOR_TUI=consoleex`; MCP tools ship out-of-process; plugin hosting is split across the `Harbor.Plugins.*` projects.
>
> **Связанные документы:**
> - [.ai-factory/DESCRIPTION.md](./.ai-factory/DESCRIPTION.md) — спецификация проекта и стек
> - [.ai-factory/rules/base.md](./.ai-factory/rules/base.md) — базовые правила и конвенции
> - [docs/ROADMAP.md](./docs/ROADMAP.md) — full roadmap with priorities + tech-debt backlog
> - [docs/COMPONENT_CATALOG.md](./docs/COMPONENT_CATALOG.md) — reusable UI components (Avalonia/Blazor/WPF)
> - [docs/PATTERNS.md](./docs/PATTERNS.md) — 18 pattern catalog with real code.
> - [docs/ANTIPATTERNS.md](./docs/ANTIPATTERNS.md) — 38 antipatterns we forbid.
> - [docs/EXAMPLES.md](./docs/EXAMPLES.md) — 40+ recipes ("How do I...?").
> - [docs/PLUGIN_DEVELOPMENT.md](./docs/PLUGIN_DEVELOPMENT.md) — Roslyn plugin system.

## What Harbor is

A modular .NET 10 AI coding harness. Modular = every concern behind an interface. Inspired by kilocode/opencode/pi-agent/crush but written from scratch in C# with NativeAOT in mind.

## Before you start

1. Read [CLAUDE.md](./CLAUDE.md) for code conventions (it cross-references this file).
2. Read [docs/ARCHITECTURE_LAYERS.md](./docs/ARCHITECTURE_LAYERS.md) for the canonical
   Clean / Hexagonal / Onion layering rules. **Before adding any `<ProjectReference>` to
   a `.csproj`, check the allowed/forbidden matrix in §2.** The rules are mechanically
   enforced by `tests/Harbor.Architecture.Tests/` (46 tests: 21 reflection-based +
   25 NetArchTest-based — see §5 of that doc) — run it (`dotnet test
   tests/Harbor.Architecture.Tests/ -c Release --no-build`) after every
   project-reference change.
3. Read [docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md) for known
   principle violations — don't introduce more of the same kind. Search the codebase
   for `TODO(principles)` to see existing markers. The §ARCH-001..§ARCH-NNN section
   covers layering violations found and fixed.
4. Read [specs/14-architecture-revised.md](./specs/14-architecture-revised.md) for current architecture.
5. Read [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md) for high-level design + principles summary.
6. Read [docs/DEVELOPMENT.md](./docs/DEVELOPMENT.md) for development workflow + **principles checklist** for PRs.
7. Read [docs/ROADMAP.md](./docs/ROADMAP.md) for current state + planned next steps.
8. If touching the interactive shell (`contrib/tui/Harbor.Tui.SpectreTui`, compiled into the default CLI build) or any renderer — read [docs/SPECTRE_TUI_DEEP_DIVE.md](./docs/SPECTRE_TUI_DEEP_DIVE.md) for render-loop anatomy + recipes for opencode/kilocode/pi-agent features.
9. Run `dotnet build` to make sure the project compiles.
10. Run the affected test projects individually (`dotnet test tests/<Project> -c Release --no-build`) — **including `tests/Harbor.Architecture.Tests/`** after every project-reference change. Whole-solution `dotnet test` is unreliable under the MTP host.

## Project structure quick reference

```
src/Harbor.Abstractions/              — base contracts (zero deps)
src/Harbor.Abstractions.Contracts/    — models, events, ValueObjects, PermissionRuleset
src/Harbor.Core/                      — EventBus, AgentLoop, config, onboarding, compaction
src/Harbor.Registries/                — Agent/Tool/Provider registries (builtin agents: code, plan, explore)
src/Harbor.Application/               — sessions, permissions, configuration
src/Harbor.Hosting/                   — DI modules wired by the CLI (TuiModule, StorageModule, CoreModule, ...)
src/Harbor.Terminal.Abstractions/     — ITuiRenderer, ITuiRenderContext, BaseTuiRenderer, views/VMs
src/Harbor.Tui.Ansi/                  — ANSI streaming renderer
src/Harbor.Tui.Plain/                 — plain text renderer (pipes/CI, HARBOR_TUI=plain)
src/Harbor.Tui.ConsoleEx/             — second in-process terminal renderer (raw-mode input,
                                        cell-diff output; opt-in via HARBOR_TUI=consoleex)
src/Harbor.Tui.Notifications/         — desktop OS notifications renderer
src/Harbor.Ui.Framework*/             — TEA-style UI state/reducers/projection/services shared by apps
src/Harbor.Desktop.{Abstractions,Shared,Animations} — desktop app support
src/Harbor.Storage.{Jsonl,Memory,Sqlite}/ — session stores (HARBOR_STORAGE=jsonl|memory|sqlite)
src/Harbor.Providers.{Anthropic,OpenAI,Ollama,OpenAiCompatible,Shared}/ — LLM clients + shared-source compat layer
src/Harbor.Tools.Builtin/             — 14 builtin tools under Tools/ (read/write/edit/bash/glob/grep/
                                        ls/task/webfetch/patch/notebook/ripgrep/tree/mcp)
src/Harbor.Plugins.*                  — plugin hosting split: Abstractions, Compilation (Roslyn),
                                        Instantiation, Registration, Hosting, Runtime (CS loader),
                                        Host, Storage
src/Harbor.Ipc.{Abstractions,InProcess,Client,Server}/ — daemon/remote IPC
src/Harbor.Transport.Remote/, Harbor.Telemetry.{Core,Otlp}/, Harbor.CodeGen/, Harbor.Logging/,
src/Harbor.Extensions/, Harbor.Diagnostics.Abstractions/

apps/Harbor.App.Cli/                  — CLI entry point, slash commands, REPL, HostBuilder DI root
apps/Harbor.App.Avalonia/             — cross-platform desktop GUI

contrib/tui/                          — extra interactive shells compiled into the default CLI build:
                                        Harbor.Tui.SpectreTui (interactive shell), Harbor.Tui.Spectre.Fullscreen,
                                        Harbor.Tui.{Spectre,TerminalGui,Termina,RazorConsole}
contrib/apps/, contrib/scripting/, contrib/tests/ — WPF/Maui/Blazor apps, scripting stack, their tests

samples/plugins/                      — 4 DLL-based sample plugins (WebSearch, TodoWrite, GitTools, FileTree)
samples/plugins-cs/                   — CS-source sample plugins (HelloWorldPlugin.cs), compiled at startup
samples/mcp/                          — sample MCP servers (node/python/rust/csharp-hello)
providers/                            — 13 JSON LLM provider configs (embedded via <EmbedProviders>)
specs/                                — ~17 design specification documents
docs/                                 — ~35 docs (architecture, tools catalog, roadmap, patterns, ...)
tests/                                — 27 test/bench project directories (25 runnable TUnit suites)
                                        incl. shared Harbor.TestKit and Harbor.Benchmarks
```

Two solution files exist: `Harbor.slnx` (main) and `Harbor.Samples.slnx` (samples). Building works normally (`dotnet build`); testing must be done per project — see [Build & test commands](#build--test-commands).

### Env var quick reference

| Provider | Env var |
|---|---|
| `kilocode` (FREE models available) | `KILO_API_KEY` |
| `anthropic` | `ANTHROPIC_API_KEY` |
| `openai` | `OPENAI_API_KEY` |
| `openrouter` | `OPENROUTER_API_KEY` |
| `deepseek` | `DEEPSEEK_API_KEY` |
| `groq` | `GROQ_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `xai` | `XAI_API_KEY` |
| `together` | `TOGETHER_API_KEY` |
| `fireworks` | `FIREWORKS_API_KEY` |
| `cerebras` | `CEREBRAS_API_KEY` |
| `ollama` (local) | (none) |
| `vllm` (local) | (none) |

> **Common gotcha**: Kilocode's env var is `KILO_API_KEY` (not `KILOCODE_API_KEY`).
> The free model is `kilocode/tencent/hy3:free` — no credit card required.

## Key concepts

### Agent loop

```
UserMessage → SystemPromptBuilder → LlmClient.StreamAsync → render tokens
                                              ↓
                                        ToolCallEvent
                                              ↓
                                    ToolRegistry.GetTool → Execute
                                              ↓
                                    ToolResult → next turn
                                              ↓
                                    (repeat until no tool calls)
                                              ↓
                                    AgentEnd
```

### Event bus

All events published via `IEventBus` (defined in `Harbor.Abstractions`, event records in
`Harbor.Abstractions.Contracts.Events`). Subscribers (TUI renderers, loggers, plugins) receive typed events:

- `AgentStartEvent`, `AgentEndEvent`
- `TurnStartEvent`, `TurnEndEvent`
- `MessageStartEvent`, `MessageUpdateEvent`, `MessageEndEvent`
- `ToolExecutionStartEvent`, `ToolExecutionUpdateEvent`, `ToolExecutionEndEvent`
- `CompactionStartedEvent`, `CompactionCompletedEvent`, `CompactionFailedEvent`
- `SessionStatsEvent`, `SessionChangedEvent`, `AgentErrorEvent`

LLM-level events (`TextDeltaEvent`, `ToolCallStartEvent`, `StepFinishEvent`, …) surface inside `MessageUpdateEvent.LlmEvent`.

### Identifiers

All IDs are strongly-typed `ValueObject`s defined in
`Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs`:
- `SessionId`, `MessageId`, `ToolCallId`
- `ProviderId`, `ModelRef` (e.g. `anthropic/claude-opus-4`)
- `ToolName`, `AgentName`

Use `ProviderId.TryCreate(string?)` for parsing. Returns `Result<ProviderId>`.

### Result<T>

Operations that can fail return `Result<T>`:
- `Result.Success(value)` / `Result.Failure<T>(error)`
- `result.IsSuccess` / `result.IsFailure`
- `result.Value` (throws if failure) / `result.Error` (throws if success)

Don't throw exceptions for expected failures (file not found, invalid args, etc.).

## Decision tree — "I want to do X"

```
I want to...
├── ...add a tool the LLM can call
│   └─→ docs/EXAMPLES.md §1 (Add a `time` builtin tool)
│       or docs/PLUGIN_DEVELOPMENT.md §Quickstart (drop a .cs in ~/.harbor/plugins/)
│
├── ...add a new LLM provider
│   ├─ If it's OpenAI-compat (90% of providers)
│   │  └─→ docs/EXAMPLES.md §9 (Add a new OpenAI-compatible provider)
│   │      Just create providers/<name>.json — no code.
│   │
│   └─ If it needs special API (Anthropic cache_control, OpenAI Responses API)
│      └─→ docs/EXAMPLES.md §10 (Add a native LLM provider)
│         Implement ILlmClient, register in HostBuilder.cs.
│
├── ...add a new storage backend (Redis, Postgres, ...)
│   └─→ docs/EXAMPLES.md §17 (Implement a custom session store)
│      Implement ISessionStore, register in HostBuilder.RegisterStorage.
│
├── ...add a new TUI renderer (GUI, web, ...)
│   └─→ docs/EXAMPLES.md §18 (Switch TUI renderer) + AGENTS.md §Add a TUI view
│      Implement ITuiRenderer, register in src/Harbor.Hosting/Modules/TuiModule.cs.
│
├── ...add a TUI view (status panel, file tree, diagnostics, ...)
│   └─→ docs/EXAMPLES.md §19 (Add a TUI view model) + §20 (Add a TUI view)
│      Implement ITuiViewModel (MVVM) + TuiViewBase<T>, register via ITuiPlugin.
│
├── ...write a plugin (tool, provider, agent, TUI)
│   └─→ docs/PLUGIN_DEVELOPMENT.md (full guide + 5 examples)
│      Drop a .cs in ~/.harbor/plugins/, restart Harbor.
│
├── ...understand the codebase
│   └─→ docs/PATTERNS.md (18 patterns with real code)
│      Then docs/ARCHITECTURE.md (concrete code flow + sequence diagrams)
│
├── ...understand what NOT to do
│   └─→ docs/ANTIPATTERNS.md (38 antipatterns with before/after code)
│      Then docs/CODE_PRINCIPLES_AUDIT.md (41 known violations to not repeat)
│
├── ...debug a failing test
│   └─→ docs/DEVELOPMENT.md §Workflow: debug a failing test
│
├── ...profile a hot path
│   └─→ docs/DEVELOPMENT.md §Workflow: profile a hot path (BenchmarkDotNet)
│
├── ...trigger compaction / debug memory
│   └─→ docs/EXAMPLES.md §27 (Trigger compaction manually)
│
├── ...set up permissions (deny bash for plan agent, ask on .env)
│   └─→ docs/EXAMPLES.md §29-31 (Permissions recipes)
│
├── ...run E2E test against real provider
│   └─→ AGENTS.md §E2E testing (Kilocode free model)
│
└── ...contribute a fix
    └─→ CLAUDE.md §Code review checklist (must pass before PR)
       Then docs/DEVELOPMENT.md §Contributing
```

## Common tasks

### Add a builtin tool

> **Full reference:** [docs/TOOLS_CATALOG.md](./docs/TOOLS_CATALOG.md) — every builtin
> tool's args schema, 3+ examples per tool, "when to use X vs Y" matrix, and a complete
> WebFetchTool walkthrough showing how to design a tool from scratch.

```csharp
// src/Harbor.Tools.Builtin/Tools/MyTool/MyTool.cs
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;

namespace Harbor.Tools.Builtin;

public sealed class MyTool : ITool
{
    public ToolName Name => ToolName.Create("my_tool");
    public string DisplayName => "My Tool";
    public string Description => "Does something useful";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "my_tool: Does something";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public JsonDocument ParameterSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "input": { "type": "string", "description": "Input value" }
          },
          "required": ["input"]
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing 'input' argument.");
        return Result.Success();
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var input = args.GetProperty("input").GetString()!;
        return Task.FromResult(ToolResult.Success($"Processed: {input}"));
    }
}
```

Register in `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` (in `CreateToolRegistry`):
```csharp
tb.AddTool(() => new MyTool(loggerFactory.CreateLogger<MyTool>()));
```

If the tool needs a DI dependency (e.g. `McpToolTool` needs `IMcpRegistry`), construct it
eagerly and pass the instance — `ToolContext.Services` is not populated by the default
`AgentLoop`:
```csharp
tb.AddTool(new MyTool(sp.GetRequiredService<IMyDep>(), loggerFactory.CreateLogger<MyTool>()));
```

Also add a permission rule to `PermissionRuleset.Default` in
`src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs`:
```csharp
new("my_tool", "*", PermissionAction.Allow),  // or Ask / Deny
```

Add tests in `tests/Harbor.Tools.Builtin.Tests/` (one file per tool — see
`WebFetchToolTests.cs`, `McpToolToolTests.cs` for the canonical patterns).

### Add a JSON-only LLM provider

Create `providers/<name>.json`:

```jsonc
{
  "id": "myprovider",
  "displayName": "My Provider",
  "baseUrl": "https://api.myprovider.com/v1",
  "apiType": "openai-compatible",
  "authType": "bearer",
  "authEnvVar": "MYPROVIDER_API_KEY",
  "modelsUrl": "https://api.myprovider.com/v1/models",
  "modelsPath": "data",
  "modelMapping": {
    "id": "id",
    "displayName": "name"
  }
}
```

Set `MYPROVIDER_API_KEY` env var. Done.

### Add a test

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.YourNamespace.Tests;

public class YourTests
{
    [Test]
    public async Task Method_State_ExpectedResult()
    {
        var result = SomeMethod();
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("expected");
    }
}
```

Run: `dotnet test tests/Harbor.YourNamespace.Tests -c Release --no-build` (run `dotnet build` first; whole-solution test runs are unreliable under the MTP host).

### Add a TUI view model

1. Create `src/Harbor.Terminal.Abstractions/ViewModels/MyViewModel.cs`.
2. Inherit `ObservableObject` and implement `ITuiViewModel`.
3. Use `[ObservableProperty]` on private `_camelCase` fields to get INPC for free.
4. Use `[RelayCommand]` on private methods to expose `IRelayCommand` properties.
5. Implement `UpdateFromEventAsync` to react to `AgentEvent`s (pattern-match by type).
6. See `Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs` for examples.

> Never reference `Harbor.Core` from a TUI assembly. All agent state arrives via `AgentEvent`.

### Add a TUI view

1. Create `src/Harbor.Terminal.Abstractions/Views/MyView.cs`.
2. Inherit `TuiViewBase<TViewModel>` (or implement `ITuiView` directly).
3. Set a unique `Id`, `DisplayName`, and `TuiViewPlacement`.
4. Override `RenderAsync(ITuiRenderContext, ct)` to draw.
5. Optionally override `HandleKey` and `OnEventAsync`.
6. Register in a renderer (or via `ITuiPlugin`) — the base renderer auto-binds the VM by id.

### Add a CS-source plugin (preferred)

1. Drop a `.cs` file into `~/.harbor/plugins/` (global) or `<project>/.harbor/plugins/` (project-local).
2. Implement `IPlugin` (and `IToolPlugin` / `IProviderPlugin` / `IAgentPlugin` / `ITuiPlugin`) with a parameterless constructor.
3. The plugin can reference any type already loaded in the host AppDomain (Harbor.Abstractions, System.Text.Json, etc.).
4. See `samples/plugins-cs/HelloWorldPlugin.cs` for a canonical example.
5. Read [docs/PLUGIN_SYSTEM.md](./docs/PLUGIN_SYSTEM.md) for the full reference (caching, debugging, security).

CS plugins are compiled in-memory via Roslyn at startup. Cached by source SHA-256 in `~/.harbor/plugins/cache/`. Run in-process with full trust — only drop reviewed source files.

### Add a TUI plugin (DLL-based, legacy path)

1. Create a class library project referencing `Harbor.Tui.Abstractions`.
2. Implement `ITuiPlugin` — set `Name`, `Version`, `Description`.
3. In `RegisterTui(ViewRegistry, ViewModelRegistry)`, register any custom views / view models.
4. Register *before* `BaseTuiRenderer.InitializeAsync` to override builtins.
5. See `samples/plugins/` for examples.
6. Note: TUI plugins can also be contributed via CS-source — declare a class implementing both `IPlugin` and `ITuiPlugin` in a `.cs` file under `~/.harbor/plugins/`.

### Add a SpectreTUI feature (diff-view, slash-popup, file-tree, etc.)

Если фича — в `contrib/tui/Harbor.Tui.SpectreTui/` (компилируется в дефолтную CLI-сборку; исходники физически в contrib после миграции sprint-2), **обязательно** прочтите [docs/SPECTRE_TUI_DEEP_DIVE.md](./docs/SPECTRE_TUI_DEEP_DIVE.md) целиком. Краткий workflow:

1. **State** для фичи → добавить в `UiState` (immutable record), обновлять через `with`.
2. **Transitions** → в `UiReducer.Update` (pattern match на `UiMsg`). Не мутить state в renderer'е.
3. **View** → `internal sealed class` в `View/`, не более 100 строк, не трогает `Harbor.Core`.
4. **Layout slot** → в `ChatLayoutShell.Create()` добавить `new Layout("MySlot").Size(N)`, в `BuildWidgets()` возвращать виджет по ключу `"MySlot"`.
5. **SyncLayout** → копировать из `UiState` в `ChatViewProjector` (pass-through property).
6. **Hot path** → `for (int i = ...)` вместо `foreach`/LINQ, `StringBuilderPool.Rent()` вместо `new StringBuilder()`.
7. **Tests** → `tests/Harbor.Tui.Tests/MyViewTests.cs` — что виджет строится без исключений.
8. **E2E** → `tests/Harbor.Tui.E2E.Tests/` — если меняется видимый output.

Рецепты для конкретных фич (diff-view, slash-completion popup, file-tree sidebar, LSP diagnostics inline, token-usage breakdown) — [docs/SPECTRE_TUI_DEEP_DIVE.md §11](./docs/SPECTRE_TUI_DEEP_DIVE.md#11-где-и-как-добавлять-фичи-из-opencode--kilocode--pi-agent).

### Mark a principle violation in code

Если заметили нарушение принципов и не можете исправить прямо сейчас, пометьте его TODO-комментарием:

```csharp
// TODO(principles)[CATEGORY, severity]: описание нарушения
// Fix: рекомендация
// См. аудит §XXX-NNN.
```

Пример:
```csharp
// TODO(principles)[OCP, STRATEGY]: switch on ProviderId violates Open/Closed.
// Fix: extract to IProviderCompatFlag Strategy, inject via DI.
// См. аудит §OOP-002.
```

Категории: `OOP, SOLID, GoF, FP, ROP, PERF, байтоебля, CONCURRENCY, AOT, ENCAPSULATION, THREAD-SAFETY, CRASH-SAFETY`. Поиск всех TODO: `grep -rn "TODO(principles)" src/`.

## E2E testing

End-to-end tests verify the full agent pipeline against a real provider. Harbor's
**E2E-verified** provider is **Kilocode** with the free `tencent/hy3:free` model —
no credit card required, $0 cost per call.

### Running the E2E smoke test

```bash
export KILO_API_KEY=klo_xxxxxxxxxxxxxxxxxxxxxx
export HARBOR_MODEL=kilocode/tencent/hy3:free
export HARBOR_TUI=plain   # easy to capture stdout

dotnet run --project apps/Harbor.App.Cli -- ask "Print hello world in 3 languages"
```

### Expected output (capture for regression diffs)

```
[agent_start] session=8f3c2a01e9d74f5f9b8c1a2b3c4d5e6f
[turn_start] turn=1
[message_start] id=01HN1234567890abcdefghijklm
Hello! Here are three ways to print "Hello, World!":
  1. Python:  print("Hello, World!")
  2. Rust:    println!("Hello, World!");
  3. Go:      fmt.Println("Hello, World!")
[message_end] id=01HN1234567890abcdefghijklm
[turn_end] turn=1
[agent_end] new_messages=1
status: kilocode/tencent/hy3:free | agent: code | $0.0000 | 142↑ 87↓ | idle
```

**What each line means** (so you can debug regressions):

- `[agent_start]` — `AgentLoop.RunAsync` published `AgentStartEvent`.
- `[turn_start] turn=1` — first turn began (1 turn = 1 LLM call + any tool calls).
- `[message_start]` — LLM started streaming, `MessageStartEvent` published.
- The text — `TextDeltaEvent`s coalesced in `StringBuilderPool`.
- `[message_end]` — LLM finished, `MessageEndEvent` published.
- `[turn_end] turn=1` — all tool calls executed (none in this case), `TurnEndEvent` published.
- `[agent_end] new_messages=1` — loop ended, `AgentEndEvent` published with 1 new message.
- `status:` line — `StatusBarView` rendered from `StatusBarViewModel` state.

If any line is missing in a regression, the corresponding event isn't being published.
Read `AgentLoop.RunAsync` to find where the event should be emitted.

### E2E with tool calls (more realistic)

```bash
$ dotnet run --project apps/Harbor.App.Cli -- ask "Read the first 10 lines of README.md"

[agent_start] session=abc123
[turn_start] turn=1
[message_start] id=m1
[message_update] event=tool_call_start id=tc_1 tool=read
[message_update] event=tool_call_delta id=tc_1 args_delta='{"path":"README.md","limit":10}'
[message_update] event=step_finish index=1 reason=tool_use
[message_end] id=m1
[tool_execution_start] id=tc_1 tool=read args={"path":"README.md","limit":10}
[tool_execution_end]   id=tc_1 ok=true
[turn_end] turn=1
[turn_start] turn=2
[message_start] id=m2
Here are the first 10 lines of README.md:

[0001] # Harbor
[0002] 
[0003] > Modular .NET 10 AI coding agent harness
...
[message_end] id=m2
[turn_end] turn=2
[agent_end] new_messages=3
status: kilocode/tencent/hy3:free | agent: code | $0.0000 | 312↑ 187↓ | idle
```

Note `new_messages=3` — user message, assistant tool-call message, tool result
message, assistant final message. Each turn emits its own `MessageStartEvent`
and `MessageEndEvent`.

### E2E checklist

When making changes to:
- **`AgentLoop`** — run E2E to verify the event sequence (`agent_start` →
  `turn_start` → `message_start` → `message_update` (zero or more) → `message_end` →
  `turn_end` → `agent_end`) is intact.
- **Any `ILlmClient`** — run E2E against that provider's free tier (Kilocode for
  OpenAI-compat providers; Ollama for local).
- **`MessageConverter`** — verify messages round-trip through JSONL storage and reload.
- **`CompactionService`** — craft a session that exceeds the context window and verify
  the summary message lands in storage.
- **`PermissionService`** — verify a `deny` rule produces a `PermissionDenied` tool result.
- **TUI renderers** — capture plain-renderer output and diff against the expected output above.

### Adding a new E2E smoke test

1. Add a `*Tests.cs` file in the relevant test project.
2. Mark with `[Test]` and guard with `if (Environment.GetEnvironmentVariable("HARBOR_E2E") is null) return;`
   so the test is skipped in CI by default.
3. Use the plain TUI renderer and capture stdout via `CaptureRenderContext`.
4. Assert that the captured output contains the expected event markers.
5. Document the test in this file.

## Benchmarks

Harbor benchmarks live in `docs/BENCHMARKS.md`. Key numbers:

| Metric | Value |
|---|---|
| Cold start (Debug JIT) | **38 ms** |
| RSS idle | **28 MB** |
| Binary size | **5 MB** |
| `ProviderRegistry.GetClient` (frozen) | **0.18 µs** |
| `ToolRegistry.ResolveTools` (4 tools) | **0.42 µs** |
| `PermissionRuleset.Evaluate` | **0.27 µs** |

Historical spot-checks from `docs/BENCHMARKS.md`; re-measure before quoting on hot-path PRs.

### Adding a benchmark

1. Create `tests/<Project>.Benchmarks/` project (or add to existing).
2. Use [BenchmarkDotNet](https://benchmarkdotnet.org/) — add the package.
3. Mark benchmark methods with `[Benchmark]`.
4. Run `dotnet run -c Release` to measure.
5. Add the result to `docs/BENCHMARKS.md`.

### Benchmark methodology

- Always run in `Release` mode (`dotnet run -c Release`).
- Disable parallelism (`[SimpleJob(RunStrategy.Throughput, invocationCount: 10000)]`).
- Pin to a single core via `Job.Default.WithEnvironmentVariable("DOTNET_ThreadPool_UnboundedMaxThreads", "1")`.
- Warm up 3 iterations, measure 10 iterations.
- Report mean ± std dev, plus allocation count.
- Compare against the previous version when refactoring hot paths.

## Code principles — quick reference

Harbor следует принципам OOP/SOLID/GoF/FP/ROP/perf. Полный аудит и чек-лист для PR:

- **[docs/CODE_PRINCIPLES_AUDIT.md](./docs/CODE_PRINCIPLES_AUDIT.md)** — 41 нарушение, 11 критических, приоритизированный план рефакторинга.
- **[docs/DEVELOPMENT.md §Principles checklist](./docs/DEVELOPMENT.md#principles-checklist)** — чек-лист для PR по 8 категориям.
- **[CLAUDE.md §Code review checklist](./CLAUDE.md#code-review-checklist)** — расширенный чек-лист (там же).

### Топ-7 критических нарушений (не повторять)

1. **§ROP-002** — `PermissionService.CheckAsync` вызывает `.Value` на `Result` без проверки → краш. **Никогда** не делайте `.Value` на `Result` без `IsSuccess`.
2. **§OOP-001** — `OpenAiCompatibleLlmClient._toolCallIndexToId` — instance-level mutable state в singleton-сервисе → гонка. **Singleton + mutable state = lock или per-call local.**
3. **§FP-003 / §FP-006** — `_ = SomeAsync()` fire-and-forget → теряются ошибки, зависает state. **Никогда** без `.ContinueWith(OnlyOnFaulted)` или `await`.
4. **§ROP-001** — `DeserializeMessage` возвращает `null` на ошибку → теряется диагностика. **Возвращайте `Result<T>`**, не `null`.
5. **§PERF-002** — `JsonSerializer.Serialize<Dictionary<string,object?>>` → reflection, IL2026 под AOT. **Только `Utf8JsonWriter` или `JsonSerializerContext`** на hot paths.
6. **§PERF-005** — `JsonDocument.Parse(line)` на каждой строке JSONL → 10k аллокаций. **Только `Utf8JsonReader`** для per-line parsing.
7. **§OOP-002** — `switch (ProviderId.Value) { case "deepseek": ... }` → OCP violation. **Strategy-паттерн через `IProviderCompatFlag`**, не строковый switch.

Поиск существующих TODO: `grep -rn "TODO(principles)" src/`

## What NOT to do

1. **Don't add dependencies to `Harbor.Abstractions`** — it must stay zero-dep (only CSharpFunctionalExtensions).
2. **Don't use `Newtonsoft.Json`** — use `System.Text.Json` only.
3. **Don't use EF Core** — not AOT-compatible. Use Dapper or raw ADO.NET.
4. **Don't throw exceptions for expected failures** — use `Result<T>`.
5. **Don't use `xUnit` or `NUnit`** — TUnit only.
6. **Don't use `AssemblyLoadContext` collectible under AOT** — use out-of-process plugins. **Roslyn CS-source plugins also do NOT work under AOT** — `Microsoft.CodeAnalysis.CSharp` requires JIT. Use the DLL-based or out-of-process plugin path for AOT.
7. **Don't add `Spectre.Console.Cli`** — not AOT-compatible. Use `ConsoleAppFramework` if needed.
8. **Don't suppress warnings with `#pragma warning disable`** — fix the code or add to `.editorconfig`.
9. **Don't create C# design-token classes** (`*Tokens.cs`, `*Theme.cs`, `*Palette.cs`) in the UI layer. The source of truth is the XAML `ResourceDictionary`. Dual ownership causes sync drift, memory leaks on theme switch, and AOT breaks.
10. **Don't break the build** — `dotnet build` must succeed with 0 warnings (treat as errors).
11. **Don't break tests** — run affected test projects individually before commit (`dotnet test tests/<Project> -c Release --no-build`).

## Build & test commands

```bash
# Build everything
dotnet build

# Run a specific test project (recommended way to test).
# TUnit uses --treenode-filter for filtering, NOT --filter.
dotnet test tests/Harbor.Core.Tests -c Release --no-build
dotnet test tests/Harbor.Tui.Tests --treenode-filter "/*/*/DefaultUiProjectorTests/*"

# WARNING: do NOT run dotnet test across the whole Harbor.slnx — whole-solution
# invocations currently break under the Microsoft.Testing.Platform (MTP) host.
# Always target one test project directory at a time.

# Run CLI
dotnet run --project apps/Harbor.App.Cli

# CLI verbs: ask <prompt> | setup | auth | config | providers | models [provider] |
#            sessions | tui | storage | logs | daemon/status/logs-commands | help | version
dotnet run --project apps/Harbor.App.Cli -- help
dotnet run --project apps/Harbor.App.Cli -- providers
dotnet run --project apps/Harbor.App.Cli -- models kilocode
dotnet run --project apps/Harbor.App.Cli -- sessions
```

## Testing a change

1. Make the change.
2. `dotnet build` — must succeed with 0 warnings.
3. Run the affected test projects individually with `dotnet test tests/<Project> -c Release --no-build` — all tests in them must pass. Do not run solution-wide `dotnet test` (see MTP warning above).
4. If you added a new tool — add tests for it.
5. If you changed an interface — update all implementations.
6. Run the CLI manually to verify: `dotnet run --project apps/Harbor.App.Cli -- help`.
7. For changes to `AgentLoop` or any `ILlmClient` — run the E2E smoke test against
   Kilocode free model (see E2E testing section above).
8. For hot-path changes — add a benchmark to `docs/BENCHMARKS.md` (see benchmarks section).

## Architecture decisions you should respect

1. **Two-process architecture** (planned v0.7) — Core (AOT) + TUI (JIT) communicate via UDS. Don't tightly couple them.
2. **JSONL-first storage** — no SQLite in MVP. `ISessionStore` abstracts storage.
3. **Event bus decoupling** — TUI subscribes to events, doesn't call Core directly.
4. **Plugin system** — extensions go in `IPlugin`, not in Core.
5. **NativeAOT-readiness** — Core code must be AOT-compatible. No reflection emit, no `Assembly.Load`.

## When stuck

- Read the spec — `specs/14-architecture-revised.md`.
- Read existing code — `src/Harbor.Tools.Builtin/Tools/Read/ReadTool.cs` is a good reference for tools.
- Read the tool reference — `docs/TOOLS_CATALOG.md` has every builtin's args schema, 3+ examples, the "when to use X vs Y" matrix, and a full WebFetchTool walkthrough.
- Read tests — `tests/Harbor.Abstractions.Tests/IdentifiersTests.cs` for assertion patterns.
- Read `CLAUDE.md` for code conventions.

### Debugging tips (real scenarios)

#### "Build fails with weird analyzer error"

```bash
$ dotnet build
src/Harbor.Core/Agents/AgentLoop.cs(123,45): error MA0046: Unsafe code is not allowed.
```

`MA0046` is the analyzer that forbids `unsafe`. Even though we have `0% unsafe`,
sometimes a dependency uses it. Check:

```bash
$ rg "unsafe" src/ tests/ --type cs
# Should return 0 matches. If there's a match, remove the unsafe block.
```

#### "Test hangs forever"

Likely a deadlock from `.Result` or `.Wait()` on async code, or a `Channel<T>`
reader without a writer. Add a timeout:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var result = await SomeAsync(cts.Token);
```

If the timeout fires, the stack trace shows where it's stuck. Common culprits:
- `.Result` on a Task in a context with `SynchronizationContext` (UI, old ASP.NET).
- `Channel.Reader.ReadAsync` without `Writer.Complete()` on the failure path.
- `_subscriptions` lock held by a dead subscriber (see §FP-003 antipattern).

#### "Plugin doesn't load"

```bash
$ ls ~/.harbor/plugins/
myplugin.cs

$ dotnet run --project apps/Harbor.App.Cli
harbor> /plugins
(no plugins listed)
```

Check the log (`~/.harbor/logs/harbor-cli-*.log`, or just run `harbor logs --last`):

```bash
$ dotnet run --project apps/Harbor.App.Cli -- logs --last | grep -i plugin
warn: Harbor.Plugins.Runtime.CsPluginLoader[0]
      Failed to compile ~/.harbor/plugins/myplugin.cs:
      (5, 18): error CS0246: The type 'IToolPlugin' could not be found.
```

The auto-injected usings include `Harbor.Abstractions.Plugins`, so this error
shouldn't happen — but if you put the file in a sub-namespace, it might not pick
up. Make sure your plugin class is `public` and at the file's top level (not
nested in a namespace).

#### "LLM tool call args are wrong"

LLM called `read` with `{"path": 123}` (number instead of string). The
`ValidateArguments` should catch this:

```csharp
public Result ValidateArguments(JsonElement args)
{
    if (!args.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
        return Result.Failure("'path' must be a string.");
    return Result.Success();
}
```

The tool result goes back to the LLM, which retries with correct args. If the
LLM keeps failing, your `ParameterSchema` may be misleading — check that
`"type": "string"` is in the schema.

#### "Streaming freezes after 30 seconds"

Likely the LLM provider times out. Increase the HTTP timeout:

```csharp
builder.Services.AddHttpClient("providers", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);   // default is 100s
});
```

Or check the provider's docs — Kilocode free tier has rate limits.

#### "Permission denied for tool 'X'"

The agent's `PermissionRuleset` denied the call. Check `agent.Permission.Evaluate(toolName, argPath)`:

```csharp
var agent = registry.GetAgent(AgentName.Create("plan").Value).Value;
var action = agent.Permission.Evaluate("bash", "rm -rf /");
// action = Deny (plan agent denies bash)
```

To allow: add a rule in `~/.harbor/config.json`:

```jsonc
{
  "agents": {
    "plan": { "permission": { "bash": { "*": "ask" } } }
  }
}
```

#### "Compaction runs every turn (too aggressive)"

`HeuristicTokenEstimator` may be overestimating tokens. Check the model's
`ContextWindow`:

```csharp
var model = await client.GetModelsAsync(ct);
// model.ContextWindow should be e.g. 128_000, not 0
```

If `ContextWindow == 0`, the provider's `/models` endpoint didn't return
`context_length`. The estimator defaults to a small window, triggering compaction
on every turn. Fix the provider's `modelMapping` in `providers/<name>.json`:

```jsonc
{ "modelMapping": { "contextWindow": "context_length" } }
```

## File encoding

- All files UTF-8, LF line endings.
- No BOM.
- 4-space indentation (2 for JSON, XML, props).
- Trim trailing whitespace.
- Final newline at EOF.

These are enforced by `.editorconfig`.
