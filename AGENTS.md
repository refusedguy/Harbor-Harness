# AGENTS.md — Guide for AI agents working on Harbor

> This file is read by AI coding agents (Claude Code, Cursor, etc.) when making changes to Harbor. It contains operational guidance, not just conventions.

## What Harbor is

A modular .NET 10 AI coding harness. Modular = every concern behind an interface. Inspired by kilocode/opencode/pi-agent/crush but written from scratch in C# with NativeAOT in mind.

## Before you start

1. Read [CLAUDE.md](./CLAUDE.md) for code conventions.
2. Read [specs/14-architecture-revised.md](./specs/14-architecture-revised.md) for current architecture.
3. Read [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md) for high-level design.
4. Run `dotnet build` to make sure the project compiles.
5. Run `dotnet test` to make sure tests pass.

## Project structure quick reference

```
src/Harbor.Abstractions/     — interfaces & models (zero deps, depends on CSharpFunctionalExtensions only)
src/Harbor.Core/             — base implementations (EventBus, AgentLoop, registries)
src/Harbor.Tui.Abstractions/ — TUI interfaces, view models, views, renderers, MVVM base
src/Harbor.Storage.Jsonl/    — JSONL session store
src/Harbor.Providers.OpenAiCompatible/ — generic LLM client
src/Harbor.Tools.Builtin/    — 8 builtin tools
src/Harbor.Tui.Ansi/         — ANSI renderer
src/Harbor.Cli/              — entry point

providers/   — JSON configs for 13 LLM providers (including kilocode with FREE models)
specs/       — 16 design documents
tests/       — TUnit tests (242+ passing)
```

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

All events published via `IEventBus`. Subscribers (TUI, loggers, plugins) receive typed events:

- `AgentStartEvent`, `AgentEndEvent`
- `TurnStartEvent`, `TurnEndEvent`
- `MessageStartEvent`, `MessageUpdateEvent`, `MessageEndEvent`
- `ToolExecutionStartEvent`, `ToolExecutionUpdateEvent`, `ToolExecutionEndEvent`
- `CompactionStartedEvent`, `CompactionCompletedEvent`
- `SessionStatsEvent`, `AgentErrorEvent`

### Identifiers

All IDs are strongly-typed `ValueObject`s:
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

## Common tasks

### Add a builtin tool

```csharp
// src/Harbor.Tools.Builtin/MyTool/MyTool.cs
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

Register in `src/Harbor.Cli/Program.cs`:
```csharp
builder.AddTool<MyTool>();
```

Add tests in `tests/Harbor.Tools.Builtin.Tests/ToolTests.cs`.

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

Run: `dotnet test tests/Harbor.YourNamespace.Tests`

### Add a TUI view model

1. Create `src/Harbor.Tui.Abstractions/ViewModels/MyViewModel.cs`.
2. Inherit `ObservableObject` and implement `ITuiViewModel`.
3. Use `[ObservableProperty]` on private `_camelCase` fields to get INPC for free.
4. Use `[RelayCommand]` on private methods to expose `IRelayCommand` properties.
5. Implement `UpdateFromEventAsync` to react to `AgentEvent`s (pattern-match by type).
6. See `Harbor.Tui.Abstractions/ViewModels/TuiViewModels.cs` for examples.

> Never reference `Harbor.Core` from a TUI assembly. All agent state arrives via `AgentEvent`.

### Add a TUI view

1. Create `src/Harbor.Tui.Abstractions/Views/MyView.cs`.
2. Inherit `TuiViewBase<TViewModel>` (or implement `ITuiView` directly).
3. Set a unique `Id`, `DisplayName`, and `TuiViewPlacement`.
4. Override `RenderAsync(ITuiRenderContext, ct)` to draw.
5. Optionally override `HandleKey` and `OnEventAsync`.
6. Register in a renderer (or via `ITuiPlugin`) — the base renderer auto-binds the VM by id.

### Add a TUI plugin

1. Create a class library project referencing `Harbor.Tui.Abstractions`.
2. Implement `ITuiPlugin` — set `Name`, `Version`, `Description`.
3. In `RegisterTui(ViewRegistry, ViewModelRegistry)`, register any custom views / view models.
4. Register *before* `BaseTuiRenderer.InitializeAsync` to override builtins.
5. See `samples/plugins/` for examples.

## E2E testing

End-to-end tests verify the full agent pipeline against a real provider. Harbor's
**E2E-verified** provider is **Kilocode** with the free `tencent/hy3:free` model —
no credit card required, $0 cost per call.

### Running the E2E smoke test

```bash
export KILO_API_KEY=klo_xxxxxxxxxxxxxxxxxxxxxx
export HARBOR_MODEL=kilocode/tencent/hy3:free
export HARBOR_TUI=plain   # easy to capture stdout

dotnet run --project src/Harbor.Cli -- ask "Print hello world in 3 languages"
```

### Expected output (capture for regression diffs)

```
[agent_start] session=…
[turn_start] turn=1
[message_start] id=…
Hello! Here are three ways to print "Hello, World!":
  1. Python:  print("Hello, World!")
  2. Rust:    println!("Hello, World!");
  3. Go:      fmt.Println("Hello, World!")
[message_end] id=…
[turn_end] turn=1
[agent_end] new_messages=1
status: kilocode/tencent/hy3:free | agent: code | $0.0000 | 142↑ 87↓ | idle
```

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
| 242 tests | **~12 s** |
| `ProviderRegistry.GetClient` (frozen) | **0.18 µs** |
| `ToolRegistry.ResolveTools` (4 tools) | **0.42 µs** |
| `PermissionRuleset.Evaluate` | **0.27 µs** |

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

## What NOT to do

1. **Don't add dependencies to `Harbor.Abstractions`** — it must stay zero-dep (only CSharpFunctionalExtensions).
2. **Don't use `Newtonsoft.Json`** — use `System.Text.Json` only.
3. **Don't use EF Core** — not AOT-compatible. Use Dapper or raw ADO.NET.
4. **Don't throw exceptions for expected failures** — use `Result<T>`.
5. **Don't use `xUnit` or `NUnit`** — TUnit only.
6. **Don't use `AssemblyLoadContext` collectible under AOT** — use out-of-process plugins.
7. **Don't add `Spectre.Console.Cli`** — not AOT-compatible. Use `ConsoleAppFramework` if needed.
8. **Don't suppress warnings with `#pragma warning disable`** — fix the code or add to `.editorconfig`.
9. **Don't break the build** — `dotnet build` must succeed with 0 warnings (treat as errors).
10. **Don't break tests** — `dotnet test` must pass before commit.

## Build & test commands

```bash
# Build everything
dotnet build

# Run all tests
dotnet test

# Run specific project tests
dotnet test tests/Harbor.Core.Tests

# Run CLI
dotnet run --project src/Harbor.Cli

# List providers
dotnet run --project src/Harbor.Cli -- providers

# List models
dotnet run --project src/Harbor.Cli -- models
```

## Testing a change

1. Make the change.
2. `dotnet build` — must succeed with 0 warnings.
3. `dotnet test` — all tests must pass (currently 242 passed, 1 skipped).
4. If you added a new tool — add tests for it.
5. If you changed an interface — update all implementations.
6. Run the CLI manually to verify: `dotnet run --project src/Harbor.Cli -- help`.
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
- Read existing code — `src/Harbor.Tools.Builtin/Read/ReadTool.cs` is a good reference for tools.
- Read tests — `tests/Harbor.Abstractions.Tests/IdentifiersTests.cs` for assertion patterns.
- Read `CLAUDE.md` for code conventions.

## File encoding

- All files UTF-8, LF line endings.
- No BOM.
- 4-space indentation (2 for JSON, XML, props).
- Trim trailing whitespace.
- Final newline at EOF.

These are enforced by `.editorconfig`.
