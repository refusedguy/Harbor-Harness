# CLAUDE.md — Conventions for AI assistants working on Harbor

> This file is read by Claude Code (and other AI agents) when working on this codebase. It encodes the project's conventions, patterns, and gotchas.

## Project overview

Harbor is a modular .NET 10 AI coding agent harness. The architecture prioritizes:
1. **Modularity** — every concern behind an interface, swappable via DI.
2. **NativeAOT-readiness** — Core can be published as NativeAOT; TUI runs JIT.
3. **Low memory footprint** — <30MB RSS idle target (vs 1GB+ for Node.js equivalents).
4. **Plugin extensibility** — tools, providers, agents, UI all extensible.
5. **Performance** — `FrozenDictionary`, `ArrayPool`, `IReadOnlyCollection`, zero unsafe.

## Solution layout

```
src/
├── Harbor.Abstractions/              — interfaces, models, events (zero deps)
├── Harbor.Core/                      — EventBus, AgentLoop, registries, config, onboarding
├── Harbor.Storage.Jsonl/             — JSONL session store (default, zero native deps)
├── Harbor.Storage.Memory/            — in-memory store (tests/ephemeral)
├── Harbor.Storage.Sqlite/            — SQLite store (indexed queries)
├── Harbor.Providers.Anthropic/       — native Anthropic Messages API (cache_control, thinking)
├── Harbor.Providers.OpenAI/          — native OpenAI (Chat Completions + Responses API)
├── Harbor.Providers.Ollama/          — native Ollama (NDJSON, local)
├── Harbor.Providers.OpenAiCompatible/— generic OpenAI-compat adapter (13 JSON configs)
├── Harbor.Tools.Builtin/             — 8 builtin tools (read/write/edit/bash/glob/grep/ls/task)
├── Harbor.Tui.Abstractions/          — MVVM: Views + ViewModels + ITuiRenderContext
├── Harbor.Tui.Ansi/                  — ANSI streaming renderer (AOT-compatible fallback)
├── Harbor.Tui.Plain/                 — plain text renderer (pipes/CI)
├── Harbor.Tui.Spectre/               — Spectre.Console renderer
├── Harbor.Tui.Spectre.Fullscreen/    — Full-screen interactive renderer (scroll, hotkeys, markdown)
├── Harbor.Tui.SpectreTui/            — Spectre.TUI widget renderer (CLI DEFAULT)
├── Harbor.Tui.Termina/               — experimental Termina renderer
├── Harbor.Tui.TerminalGui/           — experimental Terminal.Gui v2 renderer
├── Harbor.Tui.RazorConsole/          — experimental RazorConsole template renderer
└── Harbor.Cli/                       — entry point, DI wiring, onboarding, slash-commands

samples/plugins/
├── Harbor.Plugin.WebSearch/          — DuckDuckGo search (no API key)
├── Harbor.Plugin.TodoWrite/          — per-session todo list
├── Harbor.Plugin.GitTools/           — safe git wrapper
└── Harbor.Plugin.FileTree/           — directory tree visualization

tests/
├── Harbor.Abstractions.Tests/        — 35 tests (identifiers, sessions, permissions)
├── Harbor.Core.Tests/                — 53 tests (event bus, registries, agent loop, compaction, converter, permissions, system prompt)
├── Harbor.Tools.Builtin.Tests/       — 29 tests (all 8 tools + task tool)
├── Harbor.Storage.Jsonl.Tests/       — 5 tests (JSONL CRUD)
├── Harbor.Storage.Tests/             — 27 tests (Memory + SQLite CRUD)
├── Harbor.Providers.Tests/           — 39 tests (models, config, auth, catalog, provider IDs)
├── Harbor.Config.Tests/              — 36 tests (config store, auth store, presets, onboarding wizard)
├── Harbor.Tui.Tests/                 — 199 tests (view models, render context, view registry, builtin views) [5 fail]
├── Harbor.Tui.E2E.Tests/             — 57 tests (renderer output verification)
└── Harbor.Benchmarks/                — BenchmarkDotNet benchmarks

providers/   — 13 JSON LLM provider configs (embedded + filesystem)
specs/       — 17 design specification documents (incl. specs/README.md)
docs/        — architecture, benchmarks, build, dev, getting started, plugin dev, roadmap
```

**Stats (verified 2026-07-17, Harbor v0.4.0-alpha): 480 tests total — 469 passed / 10 failed / 1 skipped across 10 test projects. `src/` builds with 0 warnings, 0 errors, 0% unsafe code. 106 warnings exist in `tests/Harbor.Tui.Tests` (below the treat-as-errors gate), so the suite is currently RED.**

## Code conventions

### Naming
- **PascalCase** for public members, types, namespaces.
- **camelCase** for locals, parameters.
- **_camelCase** for private fields.
- File-scoped namespaces everywhere.
- `var` only when type is obvious.

### Patterns
- **CSharpFunctionalExtensions** `Result<T>` for operations that can fail. Avoid throwing exceptions for expected failures.
- **ValueObject** for strongly-typed identifiers: `SessionId`, `ProviderId`, `ToolName`, etc.
- **Records** for immutable data: `Session`, `AgentMessage`, `LlmEvent`.
- **Sealed classes** by default. Open for inheritance only when explicitly designed for it.
- **Interfaces** start with `I` (e.g. `ITool`, `ILlmClient`).

### Railway Oriented Programming (ROP)

Every operation that can fail returns `Result<T>` (or `Result`) with **two tracks**: a
*success* track and a *failure* track. The failure track short-circuits through `.Bind()`
/ `.Ensure()` / `.Map()` without nested `try`/`catch`. The success track is the happy path.

```csharp
// ❌ Don't throw for expected failures:
public Session Load(string id)
{
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("id");
    return _store.Load(id) ?? throw new NotFoundException(id);
}

// ✅ Return Result<T> and chain:
public Result<Session> Load(string? id) =>
    SessionId.TryCreate(id)
        .Bind(sid => _store.GetAsync(sid))
        .Ensure(s => s is not null, "session not found");

// ✅ Caller pattern-matches:
var result = Load(maybeId);
if (result.IsFailure) { logger.LogWarning(result.Error); return; }
UseSession(result.Value);
```

Rules:
- **All** public APIs that can fail return `Result<T>` / `Result`.
- Throw exceptions only for *truly exceptional* conditions (corrupted state, NRE).
- Use `Result.Success(value)` / `Result.Failure<T>(error)`.
- Chain with `.Bind()` (flatMap), `.Map()` (select), `.Ensure(predicate, error)`.
- `Value` throws if accessed on a failure; check `IsSuccess`/`IsFailure` first.
- Don't `catch` and re-wrap exceptions inside library code — propagate the `Result`.

### ZLinq

[ZLinq](https://github.com/Cysharp/ZLinq) is a zero-allocation LINQ replacement built on
`Span<T>`. Harbor's existing hot paths use manual `for` loops and pooled buffers to avoid
LINQ's per-call iterator + delegate allocations. **For new hot-path code, prefer ZLinq
over `System.Linq`** when:

- The sequence is enumerated >1,000 times per second.
- A benchmark shows `IEnumerable<T>` allocations dominating.
- The pipeline chains 3+ operators (`Select` → `Where` → `OrderBy` → …).

```csharp
using ZLinq;

// Standard LINQ — allocates iterator + delegate per call.
var names = tools.Where(t => ...).Select(t => t.Name.Value).ToArray();

// ZLinq — zero allocation on the hot path.
var names = tools.AsValueEnumerable().Where(t => ...).Select(t => t.Name.Value).ToArray();
```

Harbor does not yet hard-depend on ZLinq; add the package to a project only when a
benchmark justifies it. The library code is structured so that swapping `using System.Linq;`
for `using ZLinq;` + `.AsValueEnumerable()` is mechanical.

### CommunityToolkit.Mvvm patterns

TUI view models live in `Harbor.Tui.Abstractions/ViewModels/` and inherit from
`ObservableObject`. Use the source generators — don't write INPC by hand:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

public sealed partial class MyViewModel : ObservableObject, ITuiViewModel
{
    // Generates a public observable property `Text` backed by `_text`.
    [ObservableProperty]
    private string _text = string.Empty;

    // Generates a `SubmitCommand` property of type IRelayCommand.
    [RelayCommand]
    private void Submit() { /* ... */ }

    // CanExecute is wired automatically from the named method.
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() { /* ... */ }
    private bool CanNext() => true;

    // Cross-property command invalidation:
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private int _currentIndex = -1;

    public string Id => "my-vm";
    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // Pattern-match on the event type and mutate observable properties.
        return Task.CompletedTask;
    }
}
```

Rules:
- The class MUST be `partial` (the source generator writes the other half).
- The backing field MUST be `_camelCase` and named `<PropertyName>` with leading underscore.
- Commands surface as `<MethodName>Command` (e.g. `Submit()` → `SubmitCommand`).
- View models are renderer-agnostic — never reference `Console` or `ITuiRenderContext`.
- `ITuiViewModel.UpdateFromEventAsync` is the **only** way agent events enter a VM.

### TUI plugin architecture (views / view models / renderers)

The TUI layer is a strict MVVM triad: **renderers** dispatch events to **view models**,
which hold state; **views** read VM state at render time and emit characters through the
**render context**. Nothing in this triad references `Harbor.Core` — agent state flows in
only through `AgentEvent` from `Harbor.Abstractions.Events`.

```
        ┌────────────────────────────────────────────────────────────┐
        │                     ITuiRenderer                           │
        │   (AnsiTuiRenderer / PlainTuiRenderer / SpectreTuiRenderer) │
        │                                                            │
        │   ┌────────────────┐      ┌──────────────────┐              │
        │   │  ViewRegistry  │◀────▶│ ViewModelRegistry│              │
        │   └────────────────┘      └──────────────────┘              │
        │           │                          ▲                       │
        │           │ binds by id              │ UpdateFromEventAsync │
        │           ▼                          │                       │
        │   ┌────────────────┐                  │                       │
        │   │   ITuiView     │──────────────────┘                       │
        │   │ (StatusBar,    │                                          │
        │   │  ChatHistory,  │   RenderAsync(ITuiRenderContext)         │
        │   │  Input, Diff)  │──────────────────────────────────────────▶│
        │   └────────────────┘                                          │
        └────────────────────────────────────────────────────────────┘
                                       ▲
                                       │ ITuiPlugin.RegisterTui(views, viewModels)
                                       │
                              ┌────────────────────┐
                              │   ITuiPlugin       │
                              │  (e.g. ClockPlugin)│
                              └────────────────────┘
```

Extension points:

- **Add a view** — implement `ITuiView` (or derive from `TuiViewBase<TViewModel>`), set a
  unique `Id` and `TuiViewPlacement`, and register it via `ViewRegistry.Register`. The
  base renderer auto-binds it to the matching VM by id.
- **Override a builtin view** — register a view with one of the builtin ids
  (`status-bar`, `chat-history`, `input`, `diff-preview`) *before*
  `BaseTuiRenderer.InitializeAsync` runs. The builtin is skipped when the id is taken
  (override-before-builtin).
- **Add a view model** — implement `ITuiViewModel` (or derive from `ObservableObject` +
  `ITuiViewModel`) and register it via `ViewModelRegistry.Register`. The base renderer
  fans every `AgentEvent` out to all VMs via `UpdateFromEventAsync`.
- **Custom placement logic** — override `BaseTuiRenderer.ShouldRenderPlacement` to control
  which events trigger a repaint of each placement.

Decoupling contract: **never** import `Harbor.Core` from a TUI assembly. All agent state
arrives via `AgentEvent`; all rendering goes through `ITuiRenderContext`.

### SOLID principles
- **S**ingle Responsibility — each class does one thing.
- **O**pen/Closed — extend via plugins, don't modify core.
- **L**iskov Substitution — any `ITool`/`ILlmClient`/`ITuiRenderer`/`ISessionStore` can replace another.
- **I**nterface Segregation — small, focused interfaces (`ITool`, `IToolPlugin`, `IProviderPlugin`).
- **D**ependency Inversion — depend on `Harbor.Abstractions`, not implementations.

### GOF patterns used
- **Strategy**: `ILlmClient`, `ITool`, `ITuiRenderer`, `ISessionStore` — swap implementations.
- **Registry**: `ProviderRegistry`, `ToolRegistry`, `AgentRegistry`, `ViewRegistry`, `ViewModelRegistry` — with `FrozenDictionary` for O(1) lookups.
- **Observer**: `IEventBus`, `InMemoryEventBus` — pub/sub with `ImmutableArray` snapshot.
- **Builder**: `ISystemPromptBuilder`, `IToolRegistryBuilder`, `IProviderRegistryBuilder`, `IAgentRegistryBuilder`.
- **Adapter**: `MessageConverter`, `OpenAiCompatibleLlmClient`, `ConfigAuthResolver`.
- **Command**: `IAgent`, `DefaultAgent`, `TaskTool`, `ISlashCommand` implementations.
- **Specification**: `PermissionRuleset` — encapsulates permission evaluation logic.
- **Value Object**: `SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName` (7 types via CSharpFunctionalExtensions).
- **Factory Method**: `Session.Create`, `ToolResult.Success/Error`, `ProviderId.TryCreate`.
- **Plugin**: `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin` (5 contracts).
- **Repository**: `ISessionStore` — abstracts persistence (Jsonl, Memory, Sqlite).
- **Chain of Responsibility**: `AgentLoop` (prompt → LLM stream → tool execution → next turn → compaction).
- **Flyweight**: `StringPool.Shared.GetOrAdd()` for tool name interning in `AgentLoop`.
- **Object Pool**: `StringBuilderPool`, `ArrayPool<T>.Shared` via `ArrayPoolExtensions.RentScoped`.
- **MVVM**: `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` via CommunityToolkit.Mvvm (28 usages).
- **Decorator**: `BaseTuiRenderer` decorates concrete renderers with view dispatch.

### Functional programming (FP)
- `Result<T>` for error handling — Railway Oriented Programming (ROP), no exceptions for expected failures.
- Immutable `record` types for messages, events, value objects.
- Pure functions where possible (e.g. `HeuristicTokenEstimator.Estimate`).
- `IAsyncEnumerable<T>` for streaming (LLM events, tool progress).
- Pattern matching with `switch` expressions on discriminated unions (`AgentEvent`, `LlmEvent`).
- `Channel<T>` for lock-free producer-consumer (FP-inspired message passing).

### Async
- `async`/`await` everywhere. No `.Result`, no `.Wait()`.
- `CancellationToken` passed to all async methods.
- `IAsyncEnumerable<T>` for streaming (LLM events, tool progress).
- `ConfigureAwait(false)` in library code.
- Use `Channel<T>` for producer-consumer (see `OpenAiCompatibleLlmClient`).

### JSON
- `System.Text.Json` only. No Newtonsoft.
- Source-gen `JsonSerializerContext` for AOT-compatibility (planned).
- `JsonPropertyName` attributes for snake_case JSON properties on internal records.

### Error handling
- `Result.Success(value)` / `Result.Failure<T>(error)` for expected failures.
- Throw exceptions only for truly exceptional conditions (null refs, corrupted state).
- `try`/`catch` only at boundaries (HTTP, file I/O, process spawn).
- Log caught exceptions with `ILogger<T>`.
- **Never use `yield` inside `try`/`catch`** — C# forbids it. Extract to a separate method (see `MapChunkFromDocument` pattern).

### Performance

Harbor is performance-obsessed. These techniques are applied throughout:

| Technique | Where | Count |
|---|---|---|
| `FrozenDictionary` / `FrozenSet` | ProviderRegistry, ToolRegistry (after `Freeze()`) | 20 refs |
| `NonBlocking.ConcurrentDictionary` | ProviderRegistry, ToolRegistry, AgentRegistry (lock-free) | 9 refs |
| `ArrayPool<T>.Shared` | InMemoryEventBus (dead subscribers), ProviderRegistry (task array) | 12 refs |
| `StringBuilderPool` | AgentLoop (streaming coalesce), CompactionService, SystemPromptBuilder | 6 refs |
| `Channel<T>` | EventBus scrollback, DefaultAgent steering/followup, all LLM clients | 7 refs |
| `ImmutableArray<T>` | InMemoryEventBus subscriptions (atomic lock-free snapshot) | 6 refs |
| `StringPool.Shared` | AgentLoop (tool name interning / flyweight) | 3 refs |
| `[StructLayout(Sequential)]` | InMemoryEventBus.Subscription (cache-friendly iteration) | 1 ref |
| `ConfigureAwait(false)` | All async library code | 193 refs |
| `MemoryPack` `[MemoryPackable]` | All domain models (Session, Messages, Usage, etc.) | 27 refs |
| `ZLinq` drop-in | Harbor.Core (replaces System.Linq) | 4 refs |
| `IReadOnlyCollection<T>` | Public APIs (no defensive copies) | 43 refs |
| Pre-sized `List<T>(capacity)` | All List allocations in Core | verified |

Rules:
- Use `FrozenDictionary` / `FrozenSet` for read-only collections built once.
- Use `IReadOnlyCollection<T>` / `IReadOnlyList<T>` in public APIs.
- Use `ArrayPool<T>.Shared` for rented buffers (see `ArrayPoolExtensions`).
- Use `StringBuilderPool.Rent()` for concatenation in hot paths.
- Use `Channel<T>` for backpressure-aware streaming.
- Prefer manual `for` loops or ZLinq over `System.Linq` in hot paths.
- Use `ArgumentList.Add(arg)` instead of `Arguments = "..."` for `ProcessStartInfo`.
- **No `unsafe` code** — verified by `MA0046` analyzer (set to error).

### Full-screen TUI development

`FullscreenTuiRenderer` (in `Harbor.Tui.Spectre.Fullscreen`) is a full-screen interactive
renderer using Spectre.Console 0.57.2 `Live` display. It owns the REPL lifecycle.

| Hotkey | Action |
|---|---|
| `Enter` | Submit prompt |
| `Alt+Enter` | Newline (multi-line input) |
| `↑` / `↓` | Input history navigation (when idle) / scroll chat (when agent running) |
| `PageUp` / `PageDown` | Scroll chat history by 10 lines |
| `Home` / `End` | Scroll to top / bottom of chat |
| `Tab` | Autocomplete slash-commands |
| `Ctrl+L` | Refresh screen |
| `Ctrl+C` | Force quit |
| `Esc` | Abort agent (when running) / clear input (when typing) / quit (when empty) |

Features:
- **Scrollable chat history** with `▲ N lines above` / `▼ N lines below` indicators.
- **Markdown rendering**: code blocks (```` ``` ````), `**bold**`, `*italic*`, `` `code` ``,
  `# headers`, `- lists`, `1. numbered`, `[links](url)`.
- **Word-aware wrapping** (breaks at word boundaries, falls back to char wrap for long tokens).
- **Input history** persisted per session.
- **Visual polish**: boxed messages with role icons (👤 🤖 🔧 📦 ❌ ⚙️), color-coded borders.
- **Status bar**: provider/model, agent, cost, tokens, status with color-coded icon.

When adding a new interactive renderer, implement `IInteractiveTuiRenderer` — `Program.cs`
will delegate the REPL to it instead of running the default line-buffered loop.

## Build & test

```bash
# Build everything
dotnet build

# Run all tests
dotnet test

# Run a specific test project
dotnet test tests/Harbor.Core.Tests

# Run the CLI
dotnet run --project src/Harbor.Cli -- help
```

## Common tasks

### Add a new builtin tool

1. Create `src/Harbor.Tools.Builtin/<Name>/<Name>Tool.cs`.
2. Implement `ITool` interface.
3. Register in `src/Harbor.Cli/Program.cs` — `builder.AddTool<YourTool>()`.
4. Add tests in `tests/Harbor.Tools.Builtin.Tests/ToolTests.cs`.
5. Build + test.

See `src/Harbor.Tools.Builtin/Read/ReadTool.cs` as a reference.

### Add a new LLM provider (JSON-only)

1. Create `providers/<name>.json` with provider config.
2. Set env var `<NAME>_API_KEY`.
3. Done. Provider is auto-discovered at startup.

### Add a new native LLM provider

1. Create `src/Harbor.Providers.<Name>/` project.
2. Reference `Harbor.Abstractions` and `Harbor.Core`.
3. Implement `ILlmClient`.
4. Register in `src/Harbor.Cli/Program.cs` `BuildHost()`.
5. Add to solution: `dotnet sln add src/Harbor.Providers.<Name>/...`.

### Add a new storage backend

1. Create `src/Harbor.Storage.<Name>/` project.
2. Implement `ISessionStore`.
3. Register in `src/Harbor.Cli/Program.cs` — add to the `switch` on `HARBOR_STORAGE` env var.
4. Add tests.

### Add a new TUI renderer

1. Create `src/Harbor.Tui.<Framework>/` project.
2. Implement `ITuiRenderer` from `Harbor.Tui.Abstractions`.
3. Register in DI: add to the `switch` on `HARBOR_TUI` env var.
4. Done — no other changes needed (event-bus decoupling).

### Add a sample plugin

1. Create `samples/plugins/Harbor.Plugin.<Name>/`.
2. Implement `IPlugin` (and `IToolPlugin` / `IProviderPlugin` / `IAgentPlugin`).
3. Reference `Harbor.Abstractions`.
4. Add `Microsoft.Extensions.Logging` package.
5. Add `using Harbor.Abstractions.Models.Identifiers;` if using `ToolName`.
6. Add `using Microsoft.Extensions.Logging;` for `LogInformation` extension.
7. Add to solution.

## Testing conventions

- **TUnit** is the only test framework. No xUnit, no NUnit.
- Tests live in `tests/<Project>.Tests/`.
- Test methods: `public async Task MethodName_StateUnderTest_ExpectedBehavior()`.
- Use `Assert.That(actual).IsEqualTo(expected)` style.
- For expected exceptions, use `try`/`catch` (TUnit 0.50 `.Throws<T>()` has limitations).
- Tests must be deterministic — no `DateTime.Now`, no network calls (except integration tests).
- Use `Path.GetTempPath()` + GUID for temp files. Clean up in `finally`.

## Gotchas

### NativeAOT
- `AssemblyLoadContext` collectible does NOT work under NativeAOT. Use out-of-process plugins.
- Reflection-based JSON serialization gives IL2026 warnings. Always use source-gen.
- `Microsoft.Data.Sqlite` pulls in native `e_sqlite3`. JSONL storage avoids this.

### CSharpFunctionalExtensions
- `ValueObject` is a class, not a record. Cannot use record syntax for inheritance.
- `Result<T>` is a struct — be careful with boxing.

### TUnit 0.50
- `Assert.That(() => method()).Throws<T>()` doesn't compile. Use try/catch.
- `IsNotNull()` doesn't work for value types. Use `.IsTrue()` or `string.IsNullOrEmpty()` instead.
- Source-generated test discovery — tests must be `public async Task` in a `public class`.

### JSON serialization
- `JsonSerializerDefaults.Web` uses camelCase. Internal records need `[JsonPropertyName]` for PascalCase.
- `JsonElement` from `JsonDocument.Parse` must be consumed before the document is disposed.

### Process invocation
- `ProcessStartInfo.Arguments` is a single string — quoting is tricky.
- Use `ArgumentList.Add(arg)` for safe per-arg passing.
- Always `RedirectStandardError = true` even if you don't expect errors.

### Streaming with `IAsyncEnumerable`
- **Cannot use `yield` inside `try`/`catch`** — C# forbids it (CS1626).
- Pattern: extract the body to a separate method that returns `IEnumerable<T>`, call it from the `try` block.
- Or use `Channel<T>` for full backpressure-aware streaming (see `OpenAiCompatibleLlmClient`).

### Plugin development
- Add `using Harbor.Abstractions.Models.Identifiers;` — `ToolName` lives there, not in `Tools`.
- Add `using Microsoft.Extensions.Logging;` — `LogInformation` is an extension method.
- Use `internal static HttpClient` (not `private`) if accessed from a separate tool class.

### Provider config
- `id` field must be lowercase alphanumeric + dash.
- JSON configs are embedded as resources via `<EmbedProviders>true</EmbedProviders>` in `Harbor.Cli.csproj`.
- After `dotnet publish`, builtin providers work without external JSON files.

## Architecture decision records

See `specs/` for the full design rationale. Key decisions:

1. **Two-process architecture** (v2 spec) — Core (NativeAOT) + TUI (JIT) communicate via Unix domain sockets.
2. **JSONL-first storage** — no native deps, append-only, git-friendly. SQLite is optional.
3. **Generic OpenAI-compatible adapter** — covers 90% of providers without code.
4. **Native providers for special cases** — Anthropic (cache_control), OpenAI (Responses API), Ollama (NDJSON).
5. **Event bus decoupling** — TUI subscribes to events, can be replaced/swapped.
6. **Result<T> for error handling** — no exceptions for expected failures.
7. **FrozenDictionary for registries** — O(1) lookups after `Freeze()` is called.
8. **No unsafe code** — verified by `MA0046` analyzer set to error.

## Code review checklist

- [ ] Builds without warnings (treat warnings as errors).
- [ ] All async methods accept `CancellationToken`.
- [ ] No `.Result` or `.Wait()`.
- [ ] Public APIs have XML doc comments (`<summary>`, `<param>`, `<returns>`, `<remarks>`).
- [ ] New types follow SOLID principles.
- [ ] Tests added for new functionality.
- [ ] No secrets in code or config.
- [ ] Uses `Result<T>` for failure cases, not exceptions (Railway Oriented Programming).
- [ ] No `yield` inside `try`/`catch`.
- [ ] No `unsafe` code.
- [ ] Uses `FrozenDictionary` / `IReadOnlyCollection` where appropriate.
- [ ] Uses `ArgumentList.Add()` for process args.
- [ ] New view models use `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`).
- [ ] New TUI views never reach into `Harbor.Core` — only subscribe to `AgentEvent`.
- [ ] Hot paths use manual `for` loops or ZLinq, not `System.Linq`.

## When in doubt

- Read the spec — `specs/14-architecture-revised.md` for current architecture.
- Check existing patterns — `Harbor.Tools.Builtin/Read/ReadTool.cs` is a good reference.
- Look at tests — `tests/Harbor.Abstractions.Tests/` for assertion patterns.
- Check `.editorconfig` for analyzer suppressions.
- Open an issue — don't break the architecture for a quick fix.
