# Development Guide

## Prerequisites

- .NET 10 SDK (https://dot.net)
- Git
- Any modern IDE (VS Code, Rider, Visual Studio)

## Getting started

```bash
git clone https://github.com/harbor-sh/harbor
cd harbor
dotnet build
dotnet test tests/Harbor.Core.Tests -c Release --no-build
```

## Project structure

See [docs/ARCHITECTURE.md](./ARCHITECTURE.md) for the full layout.

## Build commands

```bash
# Build all projects
dotnet build

# Build a specific project
dotnet build src/Harbor.Core
dotnet build apps/Harbor.App.Avalonia

# Build Release configuration
dotnet build -c Release

# Clean build artifacts
dotnet clean

# Disk-space-constrained sandbox / CI: clear bin/obj before a full build
find . -type d \( -name bin -o -name obj \) -not -path '*/node_modules/*' -exec rm -rf {} +
```

## Test commands

> **Known limitation:** whole-solution `dotnet test` over `Harbor.slnx` breaks under
> the MTP host. Always run **per project**: `dotnet test tests/<Project> -c Release --no-build`.

```bash
# Build everything first (tests use --no-build)
dotnet build

# Run a specific test project (known-good pattern)
dotnet test tests/Harbor.Core.Tests -c Release --no-build
dotnet test tests/Harbor.Plugins.Runtime.Tests -c Release --no-build

# Run one test class — TUnit uses --treenode-filter, not --filter
dotnet test tests/Harbor.Abstractions.Tests -c Release --no-build \
  --treenode-filter "/*/*/IdentifiersTests/*"

# Detailed output
dotnet test tests/Harbor.Core.Tests -c Release --no-build --logger "console;verbosity=detailed"

# Enforce layer-dep rules (architecture tests)
dotnet test tests/Harbor.Architecture.Tests -c Release --no-build
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) v1.61.0 with Microsoft Testing Platform v2.3.2. Test files are in `tests/<Project>.Tests/`.

### Known test status

- **Pre-existing Avalonia 12 headless failures** (`MarkdownRenderer_SetMarkdown_DoesNotThrow`, `CodeBlock_Default_Code_IsEmpty`, `TypewriterStreamingText_CanSet_Text` — "Stack empty" in `AvaloniaPropertyDictionaryPool.Get()`), plus an occasional flaky pair `ChatView_Inflates` / `TryGet_ReturnsNullForUnregistered`. Not Harbor bugs — see ROADMAP backlog.
- **IPC named-pipe event-stream tests on Linux** (`Harbor.Ipc.Tests`) self-skip unless `HARBOR_IPC_EVENTSTREAM=1` is set; some timing flakes remain.

## Running the CLI

```bash
# Set API key
export ANTHROPIC_API_KEY=sk-ant-...

# Run interactive REPL
dotnet run --project apps/Harbor.App.Cli

# One-shot prompt
dotnet run --project apps/Harbor.App.Cli -- ask "What is 2+2?"

# List providers
dotnet run --project apps/Harbor.App.Cli -- providers

# List models
dotnet run --project apps/Harbor.App.Cli -- models
dotnet run --project apps/Harbor.App.Cli -- models anthropic

# Show help
dotnet run --project apps/Harbor.App.Cli -- help
```

## Configuration

### Provider configs

Located in `providers/` (project-local) or `~/.harbor/providers/` (user-global):

```jsonc
{
  "id": "openrouter",
  "displayName": "OpenRouter",
  "baseUrl": "https://openrouter.ai/api/v1",
  "apiType": "openai-compatible",
  "authType": "bearer",
  "authEnvVar": "OPENROUTER_API_KEY",
  "modelsUrl": "https://openrouter.ai/api/v1/models"
}
```

### API keys

Set via env vars (conventional names):

```bash
export ANTHROPIC_API_KEY=sk-ant-...
export OPENAI_API_KEY=sk-...
export OPENROUTER_API_KEY=sk-or-...
export DEEPSEEK_API_KEY=...
export GROQ_API_KEY=gsk_...
# etc.
```

### Default model

```bash
export HARBOR_MODEL=anthropic/claude-sonnet-4-20250514
# or
export HARBOR_MODEL=openai/gpt-4o
# or
export HARBOR_MODEL=openrouter/anthropic/claude-3.5-sonnet
```

## Common development tasks

### Add a new builtin tool

1. Create `src/Harbor.Tools.Builtin/Tools/<Name>/<Name>Tool.cs` (sealed class, implements `ITool`).
2. Register in `src/Harbor.Hosting/Modules/ToolsCatalog.cs` —
   `tb.AddTool(lf => new YourTool(lf.CreateLogger<YourTool>()));`.
3. Add a rule to `PermissionRuleset.Default`
   (`src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs`).
4. Add tests in `tests/Harbor.Tools.Builtin.Tests/YourToolTests.cs`.
5. `dotnet build && dotnet test tests/Harbor.Tools.Builtin.Tests -c Release --no-build`.

See [TOOLS_CATALOG.md §5](./TOOLS_CATALOG.md#5-building-your-own-tool--webfetchtool-walkthrough) for the full walkthrough.

### Add a new LLM provider (JSON-only)

Create `providers/<name>.json` and set env var. Done.

### Add a new test

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

### Add a new project

1. Create `src/Harbor.<Name>/` directory.
2. Create `.csproj` file referencing `Harbor.Abstractions` (and `Harbor.Core` if needed).
3. Add `GlobalUsings.cs` if helpful.
4. `dotnet sln add src/Harbor.<Name>/Harbor.<Name>.csproj`.
5. Create corresponding test project in `tests/Harbor.<Name>.Tests/`.

## Workflows — end-to-end recipes

### Workflow: add a feature end-to-end (example: `time` tool)

Покажем все 7 шагов на примере добавления `time` tool (возвращает текущее UTC время).

#### Step 1: Create the tool

```bash
mkdir -p src/Harbor.Tools.Builtin/Tools/Time
cat > src/Harbor.Tools.Builtin/Tools/Time/TimeTool.cs << 'EOF'
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Builtin;

public sealed class TimeTool : ITool
{
    private readonly ILogger<TimeTool> _logger;
    public TimeTool(ILogger<TimeTool> logger) => _logger = logger;

    public ToolName Name => ToolName.Create("time");
    public string DisplayName => "Time";
    public string Description => "Returns the current UTC time in ISO-8601.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "time: Get current UTC time";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public JsonDocument ParameterSchema { get; } =
        JsonDocument.Parse("""{"type":"object","properties":{}}""");

    public Result ValidateArguments(JsonElement args) => Result.Success();

    public Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext context, CancellationToken ct = default)
    {
        _logger.LogDebug("Time tool invoked");
        var now = DateTimeOffset.UtcNow.ToString("O");
        return Task.FromResult(ToolResult.Success(now, new { iso8601 = now }));
    }
}
EOF
```

#### Step 2: Register in DI

Edit `src/Harbor.Hosting/Modules/ToolsCatalog.cs` (`CreateToolRegistry`):

```csharp
internal static ToolRegistry CreateToolRegistry(
    HarborCompositionContext ctx, IMcpRegistry mcpRegistry, IAgentRegistry agentRegistry)
{
    var registry = new ToolRegistry();
    var tb = new ToolRegistryBuilder(registry, ctx.LoggerFactory);
    // ... existing tools
    tb.AddTool(lf => new TimeTool(lf.CreateLogger<TimeTool>()));  // ← NEW
    registry.Freeze();
    return registry;
}
```

#### Step 3: Write tests

```bash
cat > tests/Harbor.Tools.Builtin.Tests/TimeToolTests.cs << 'EOF'
using Harbor.Tools.Builtin;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

public class TimeToolTests
{
    [Test]
    public async Task Execute_Returns_Current_Utc_Time()
    {
        var tool = new TimeTool(NullLogger<TimeTool>.Instance);
        var args = JsonDocument.Parse("{}").RootElement;
        var ctx = new ToolContext(
            SessionId: "test", MessageId: "test", CallId: "test", Agent: "code",
            Abort: CancellationToken.None, Messages: Array.Empty<AgentMessage>(),
            ReportProgress: (_, _) => Task.CompletedTask,
            Ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
            Services: null!);

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("T");   // ISO-8601 has "T"
    }

    [Test]
    public async Task ValidateArguments_Accepts_Empty()
    {
        var tool = new TimeTool(NullLogger<TimeTool>.Instance);
        var args = JsonDocument.Parse("{}").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsSuccess).IsTrue();
    }
}
EOF
```

#### Step 4: Build

```bash
$ dotnet build
  Harbor.Tools.Builtin succeeded.
  Harbor.App.Cli succeeded.
  Harbor.Tools.Builtin.Tests succeeded.
  0 Warning(s)  0 Error(s)
```

#### Step 5: Run tests

```bash
$ dotnet test tests/Harbor.Tools.Builtin.Tests -c Release --no-build \
    --treenode-filter "/*/*/TimeToolTests/*"

Passed: 2
Failed: 0
Skipped: 0
Duration: ~1.2s
```

#### Step 6: Manual smoke test

```bash
$ export KILO_API_KEY=klo_...
$ export HARBOR_MODEL=kilocode/tencent/hy3:free
$ dotnet run --project apps/Harbor.App.Cli -- ask "What time is it?"
[tool_execution_start] id=tc_1 tool=time args={}
[tool_execution_end]   id=tc_1 ok=true
The current UTC time is 2026-07-16T14:23:45.1234567Z.
```

#### Step 7: PR checklist

```bash
# 0 warnings, 0 errors
dotnet build -c Release

# Affected test projects pass (per-project; whole-slnx test runs break under MTP)
dotnet test tests/Harbor.Tools.Builtin.Tests -c Release --no-build

# Code review checklist (see CLAUDE.md §Code review checklist)
# - [ ] CancellationToken threaded through
# - [ ] Result<T> for expected failures
# - [ ] No LINQ on hot path
# - [ ] Tests added
# - [ ] XML doc comments on public APIs
```

Commit:

```bash
git add src/Harbor.Tools.Builtin/Tools/Time/ tests/Harbor.Tools.Builtin.Tests/TimeToolTests.cs \
        src/Harbor.Hosting/Modules/ToolsCatalog.cs
git commit -m "feat: add 'time' builtin tool returning current UTC time"
```

### Workflow: debug a failing test

Пример: тест `EventBusTests.Publish_DeadSubscriber_Removed` падает с
`Assert.That(deadCount).IsEqualTo(1) → got 0`.

#### Step 1: Reproduce in isolation

```bash
$ dotnet test tests/Harbor.Core.Tests -c Release --no-build \
    --treenode-filter "/*/*/EventBusTests/Publish_DeadSubscriber_Removed" \
    --logger "console;verbosity=detailed"

Starting test execution, please wait...
TUnit ... Publish_DeadSubscriber_Removed FAILED.
  Expected: 1
  But was:  0
  at EventBusTests.Publish_DeadSubscriber_Removed() in /path/tests/Harbor.Core.Tests/EventBusTests.cs:line 42

Failed: Publish_DeadSubscriber_Removed
Passed: 0  Failed: 1  Skipped: 0  Duration: 0.8s
```

#### Step 2: Read the test

```csharp
// tests/Harbor.Core.Tests/EventBusTests.cs
[Test]
public async Task Publish_DeadSubscriber_Removed()
{
    var bus = new InMemoryEventBus();
    int deadCount = 0;
    bus.Subscribe(async (e, ct) => { deadCount++; throw new InvalidOperationException("boom"); });
    bus.Subscribe(async (e, ct) => { /* healthy */ await Task.CompletedTask; });

    await bus.PublishAsync(new AgentStartEvent("s", Array.Empty<AgentMessage>()), default);

    await Assert.That(deadCount).IsEqualTo(1);   // ← got 0
}
```

#### Step 3: Add diagnostic output

```csharp
[Test]
public async Task Publish_DeadSubscriber_Removed()
{
    var bus = new InMemoryEventBus();
    int deadCount = 0;
    int healthyCount = 0;
    bus.Subscribe(async (e, ct) => { deadCount++; throw new InvalidOperationException("boom"); });
    bus.Subscribe(async (e, ct) => { healthyCount++; await Task.CompletedTask; });

    await bus.PublishAsync(new AgentStartEvent("s", Array.Empty<AgentMessage>()), default);

    TestContext.Current.TestOutputWriter.WriteLine($"dead={deadCount} healthy={healthyCount}");
    await Assert.That(deadCount).IsEqualTo(1);
}
```

Re-run:

```
dead=0 healthy=1
```

The dead subscriber was never called — looks like `PublishAsync` short-circuited
*before* the throwing handler. Read `InMemoryEventBus.PublishAsync`
(`src/Harbor.Registries/Events/InMemoryEventBus.cs`):

```csharp
public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
{
    var snapshot = _subscriptions;
    if (snapshot.IsEmpty) return;
    // ...
}
```

Wait — `snapshot.IsEmpty` returns true if there are 0 subscribers. We have 2
subscribers. Let me check the `Subscribe` method:

```csharp
public void Subscribe(Func<AgentEvent, CancellationToken, Task> handler)
{
    ImmutableInterlocked.Update(ref _subscriptions,
        arr => arr.Add(new Subscription(handler, typeof(AgentEvent).IsAssignableFrom)));
}
```

Ah — `typeof(AgentEvent).IsAssignableFrom` is `false` because we passed a handler
`Func<AgentEvent, ...>` but the Subscription filter checks if `typeof(AgentEvent)`
is assignable from itself, which should be true. Let me check the Subscription
record more carefully... 

Actually, looking at the test failure (`dead=0`), the throwing handler was
**not called** — but `healthy=1` means the healthy one was called. So `PublishAsync`
*did* dispatch... but to only one subscriber.

Look again at the snapshot:

```csharp
var snapshot = _subscriptions;
```

This reads the `ImmutableArray<Subscription>`. If the second `Subscribe` call
hasn't been observed yet (race), we'd see only one. But this is a unit test, no
concurrency. So `ImmutableInterlocked.Update` *should* be working.

Let me add more logging:

```csharp
TestContext.Current.TestOutputWriter.WriteLine(
    $"subscriptions count = {bus.GetType().GetField("_subscriptions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bus)}");
```

Output:

```
subscriptions count = System.Collections.Immutable.ImmutableArray<Subscription>[1]
```

Only 1 subscriber registered! That's the bug. Looking at the Subscribe code:
the `Subscription` constructor signature was changed to take a single arg
(removing the second bool). The test still uses the old call site.

#### Step 4: Add the assertion / fix

Fix the test:

```csharp
bus.Subscribe(async (e, ct) => { deadCount++; throw new InvalidOperationException("boom"); });
bus.Subscribe(async (e, ct) => { /* healthy */ await Task.CompletedTask; });
// ^ both calls now use the same single-arg Subscribe, both register.
```

Re-run:

```bash
$ dotnet test tests/Harbor.Core.Tests -c Release --no-build \
    --treenode-filter "/*/*/EventBusTests/Publish_DeadSubscriber_Removed"
Passed: 1  Failed: 0
```

#### Step 5: Regression test

Add an assertion that BOTH subscribers are called (the regression):

```csharp
await Assert.That(healthyCount).IsEqualTo(1);   // both should be called
```

### Workflow: profile a hot path

Пример: `ToolRegistry.ResolveTools` замедлился после изменения. Покажем как
сделать BenchmarkDotNet benchmark и прочитать результаты.

#### Step 1: Write the benchmark

```csharp
// tests/Harbor.Benchmarks/ToolRegistryBenchmark.cs
using BenchmarkDotNet.Attributes;

namespace Harbor.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, invocationCount: 10_000)]
public class ToolRegistryBenchmark
{
    private ToolRegistry _registry = null!;
    private AgentName _agent = null!;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(_registry);
        tb.AddTool<ReadTool>();
        tb.AddTool<WriteTool>();
        tb.AddTool<BashTool>();
        tb.AddTool<GlobTool>();
        _registry.Freeze();
        _agent = AgentName.Create("code");
    }

    [Benchmark]
    public IReadOnlyList<ToolDescriptor> ResolveTools_NoPermission()
        => _registry.ResolveTools(_agent.Value, null);

    [Benchmark]
    public IReadOnlyList<ToolDescriptor> ResolveTools_WithPermission()
        => _registry.ResolveTools(_agent.Value, PermissionRuleset.Empty);

    [Benchmark]
    public Result<ITool> GetTool_Frozen()
        => _registry.GetTool(ToolName.Create("read"));
}
```

#### Step 2: Run

```bash
$ cd tests/Harbor.Benchmarks
$ dotnet run -c Release

// BenchmarkDotNet=v0.14.0
// Job: Throughput invocationCount=10000

| Method                       | Mean      | Error    | StdDev   | Allocated |
|----------------------------- |----------:|---------:|---------:|----------:|
| ResolveTools_NoPermission    |  0.42 µs  | 0.005 µs | 0.004 µs |     144 B |
| ResolveTools_WithPermission  |  0.48 µs  | 0.006 µs | 0.005 µs |     144 B |
| GetTool_Frozen               |  0.18 µs  | 0.002 µs | 0.002 µs |      24 B |
```

#### Step 3: Read the results

- `Mean` — среднее время на 1 invocation.
- `Error` — half of 99.9% confidence interval.
- `StdDev` — standard deviation across iterations.
- `Allocated` — bytes allocated per invocation (GC pressure).

`GetTool_Frozen` = 0.18 µs, allocated 24 B — это `Result<ITool>` struct boxing
into `object?[]` for the `Result.Failure<ITool>` path? Let's check...

Actually 24 B is the `ToolDescriptor` allocation in `ResolveTools`. For `GetTool`
frozen, it should be 0 bytes allocated. The 24 B is the `Result<ITool>` returning
a reference to existing tool — that's `ITool` reference + `Result`'s error string
storage. Hmm, actually `Result.Success(tool)` should not allocate.

Profile with `dotnet-counters`:

```bash
$ dotnet tool install -g dotnet-counters
$ dotnet-counters monitor -n Harbor.Benchmarks \
    --counters System.Runtime[System.Runtime]
```

Watch `gen-0-heap-size` and `alloc-rate` while running. If `alloc-rate` is high,
we're allocating more than expected.

#### Step 4: Compare to baseline

If a previous run gave `0.18 µs` and now it's `0.55 µs`, that's a 3× regression.
Bisect with git:

```bash
git bisect start
git bisect bad HEAD
git bisect good v0.2.0
# run benchmark after each bisect step
```

#### Step 5: Document the result

Add to `docs/BENCHMARKS.md`:

```markdown
| Operation | Mean | Allocated | Notes |
|---|---|---|---|
| ToolRegistry.GetTool (frozen) | 0.18 µs | 24 B | baseline |
| ToolRegistry.ResolveTools (4 tools, no perm) | 0.42 µs | 144 B | includes ToolDescriptor[] |
```

### Workflow: contribute a plugin

> See [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md) for the full guide.

Краткий workflow:

#### Option A: Roslyn .cs plugin (recommended, v0.3+)

```bash
# 1. Write a .cs file with both plugin class + tool class
cat > ~/.harbor/plugins/hello.cs << 'EOF'
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;

public sealed class HelloPlugin : IToolPlugin
{
    public string Name => "hello";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 3, 0);
    public string Description => "Says hello";
    public void Initialize(PluginContext context) { }
    public void RegisterTools(IToolRegistryBuilder b) => b.AddTool<HelloTool>();
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class HelloTool : ITool
{
    public ToolName Name => ToolName.Create("hello");
    public string DisplayName => "Hello";
    public string Description => "Returns a hello message";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "hello: Say hello";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
    public JsonDocument ParameterSchema =>
        JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");
    public Result ValidateArguments(JsonElement args) => Result.Success();
    public Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct = default)
    {
        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "world" : "world";
        return Task.FromResult(ToolResult.Success($"Hello, {name}!"));
    }
}
EOF

# 2. Restart Harbor — the plugin runtime compiles it on startup (see log line
#    «Loaded N CS plugin(s)» in ~/.harbor/logs/harbor-cli-*.log)
dotnet run --project apps/Harbor.App.Cli
```

#### Option B: DLL plugin (samples/plugins/)

См. [PLUGIN_DEVELOPMENT.md §Migration from DLL](./PLUGIN_DEVELOPMENT.md#migration-from-dll-to-cs).
Note: `~/.harbor/plugins/` only scans `*.cs` — compiled DLLs must be registered
by host code or served out-of-process via `src/Harbor.Plugins.Host`.

#### Option C: MCP server

Если ваш инструмент уже MCP-сервер, Harbor оборачивает его через builtin `mcp`.
Опишите сервер в `~/.harbor/mcp.json` (или `<project>/.harbor/mcp.json`):

```jsonc
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "${harborHome}"]
    }
  }
}
```

Детали: [TOOLS_CATALOG.md §9](./TOOLS_CATALOG.md#9-mcp-integration--adding-an-mcp-server).

### Workflow: profile a memory leak

If RSS grows over time on a long-running session:

```bash
# 1. Run Harbor with the problematic session
HARBOR_TUI=plain dotnet run --project apps/Harbor.App.Cli -- ask "Long task..."

# 2. In another terminal, find the Harbor process
ps aux | grep Harbor.App.Cli

# 3. Take a heap dump
dotnet tool install -g dotnet-gcdump
dotnet-gcdump collect -n Harbor.App.Cli -o harbor.dump

# 4. Analyze with PerfView or dotnet-heapstat
dotnet-heapstat harbor.dump | sort -k 2 -n -r | head -20
# Top types by retained bytes
```

Look for growing counts of: `AssistantMessage`, `ToolCallPart`,
`ImmutableArray<AgentMessage>`, `Subscription`.

Common leak causes in Harbor:
- Plugin subscribing to `IEventBus` on each tool call (forgetting to unsubscribe).
- `ConcurrentDictionary<sessionId, T>` growing without cleanup (e.g. TodoWritePlugin).
- Streaming `StringBuilder` not returned to pool (forgot `using var`).

## Code style

Enforced by `.editorconfig` and analyzers (`Roslynator`, `SonarAnalyzer`).

- 4-space indentation.
- File-scoped namespaces.
- `var` only when type is obvious.
- `async`/`await` everywhere, no `.Result`/`.Wait()`.
- `ConfigureAwait(false)` in library code.
- XML doc comments on public APIs.
- Treat warnings as errors.

See [CLAUDE.md](../CLAUDE.md) for full conventions.

## Debugging

### In VS Code

```bash
# Launch config (.vscode/launch.json)
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Harbor CLI",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/apps/Harbor.App.Cli/bin/Debug/net10.0/Harbor.App.Cli.dll",
      "args": ["ask", "Hello"],
      "cwd": "${workspaceFolder}",
      "console": "internalConsole",
      "stopAtEntry": false,
      "env": {
        "ANTHROPIC_API_KEY": "sk-ant-..."
      }
    }
  ]
}
```

### Print debugging

For console output in tests:
```csharp
TestContext.Current.TestOutputWriter.WriteLine($"Debug: {value}");
```

For console output in CLI (when TUI is not active):
```csharp
Console.Error.WriteLine($"Debug: {value}");
```

## Release process

(Planned — not yet implemented)

1. Update version in `Directory.Build.props`.
2. Update `CHANGELOG.md`.
3. Tag: `git tag v0.X.0 && git push --tags`.
4. CI builds NativeAOT binaries for all RIDs.
5. Publish to NuGet as `dotnet tool install harbor`.

## Performance profiling

```bash
# Build Release
dotnet build -c Release

# Run with dotnet-counters
dotnet tool install -g dotnet-counters
dotnet-counters monitor -n harbor --counters System.Runtime

# Heap dump
dotnet tool install -g dotnet-gcdump
dotnet-gcdump collect -n harbor
```

## Principles checklist

Перед PR прогоните код через этот чек-лист. Полный разбор каждого пункта — в [docs/CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md).

### OOP / SOLID

- [ ] **SRP**: класс делает одну вещь. Если класс > 300 строк или > 8 public методов — подозрение.
- [ ] **OCP**: новые типы добавляются через `interface` + новую реализацию, не через `switch` на существующем типе.
- [ ] **LSP**: подкласс / interface-impl ведёт себя идентично базовому контракту (включая thread-safety).
- [ ] **ISP**: интерфейс не заставляет реализовывать неиспользуемые методы. Если в impl 5+ `throw new NotSupportedException()` — разделить интерфейс.
- [ ] **DIP**: зависимости только через `Harbor.Abstractions` интерфейсы, не через конкретные классы.
- [ ] **Sealed** по умолчанию. Открытый для наследования класс — явный design decision.
- [ ] **Encapsulation**: после конструирования объект в валидном состоянии. Никаких `sessionId = "" // populated by caller`.

### GoF

- [ ] Паттерн выбран осознанно, не "потому что модно". Registry ≠ Builder ≠ Factory.
- [ ] Strategy без mutating state. Если strategy держит state — это либо параметризованный singleton, либо неправильно выбранный паттерн.
- [ ] Observer (`IEventBus`) — subscribers не бросают исключения (event bus их не ловит, кроме `InMemoryEventBus.RemoveDeadSubscriptions`).

### FP

- [ ] **Pure functions**: `UiReducer.Reduce` — эталон. Если функция mutates — это должно быть явно (mutating cache, pool, state).
- [ ] **Immutability**: доменные модели — `record` / `record struct`. `with` для модификации.
- [ ] **No side-effects in render path**: `ChatScreen.Render` не должен мутировать `UiState` — только читать.
- [ ] **No fire-and-forget**: `_ = SomeAsync()` — запрещён, кроме случаев с `.ContinueWith(OnlyOnFaulted)`.

### ROP

- [ ] Все public APIs, которые могут ошибиться — возвращают `Result<T>` / `Result`.
- [ ] **Никогда** не бросать исключение на expected failure (file not found, invalid arg, provider not registered).
- [ ] **Никогда** не вызывать `.Value` на `Result` без проверки `IsSuccess` (это краш).
- [ ] `.Bind()` / `.Map()` / `.Ensure()` — предпочтительнее `if (result.IsFailure) return ...`.
- [ ] Ошибки не теряются: `null` вместо `Result` — запрещён в новых API.

### Performance

- [ ] Hot path (`AgentLoop.RunAsync`, `StreamAsync`, `Render`, `Dispatch`) — без `LINQ`, без `new StringBuilder()`, без `string.Split`.
- [ ] `ArrayPool<T>.Shared.Rent` + `using var rented = ...` для временных буферов.
- [ ] `StringBuilderPool.Rent` для конкатенации.
- [ ] `FrozenDictionary` для read-only коллекций, построенных один раз.
- [ ] `NonBlocking.ConcurrentDictionary` для write-heavy concurrent state.
- [ ] `for (int i = 0; i < arr.Length; i++)` вместо `foreach` на hot path (избегает enumerator allocation).
- [ ] `ConfigureAwait(false)` в library code.
- [ ] `ILogger.IsEnabled(LogLevel.Trace)` guard перед `LogTrace` с expensive args.
- [ ] `CancellationToken` пробрасывается во все async методы.

### Low-level / байтоебля

- [ ] `Utf8JsonReader` вместо `JsonDocument.Parse` на hot path (SSE-чанки, JSONL-строки).
- [ ] `JsonSerializerContext` (source-gen) вместо reflection-based `JsonSerializer.Serialize<T>`.
- [ ] `Span<T>` / `ReadOnlySpan<T>` + `IndexOf` вместо `string.Split`.
- [ ] `string.Create(Length, state, (span, s) => ...)` вместо `string.Format` / interpolation на hot path.
- [ ] `Interlocked.CompareExchange` для lock-free CAS (если реально узкое горлышко).
- [ ] Pooled buffer clear перед return (`Array.Clear(arr, 0, count)`), чтобы не держать references alive.

### Concurrency

- [ ] `ILlmClient` и `ITool` impls — thread-safe для concurrent calls.
- [ ] Mutable instance state в singleton-сервисе — либо `lock`, либо `Interlocked`, либо per-call state в local'ах.
- [ ] `Channel<T>` для producer-consumer, `ImmutableArray<T>` + `ImmutableInterlocked.Update` для atomic snapshots.
- [ ] `async`/`await`, не `ContinueWith` (кроме fire-and-forget с `OnlyOnFaulted`).

### NativeAOT

- [ ] `JsonSerializer.Serialize/Deserialize<T>` — только через `JsonSerializerContext` source-gen.
- [ ] No reflection: `Type.GetProperties()`, `Activator.CreateInstance` — запрещены в Core/Storage/Providers.
- [ ] No `Assembly.Load` / `AssemblyLoadContext` collectible (использовать out-of-process plugins).
- [ ] `dotnet build -c Release` — 0 IL2026 warnings.

## SpectreTUI development (contrib)

Если меняете `contrib/tui/Harbor.Tui.SpectreTui/` — обязательно прочитайте [docs/SPECTRE_TUI_DEEP_DIVE.md](./SPECTRE_TUI_DEEP_DIVE.md):
архитектура render-loop, layout tree, scroll conventions, и квесты из opencode/kilocode/pi-agent (diff-view, slash-completion, file-tree).
Проект живёт в contrib с sprint-2 и собирается через `contrib/Contrib.slnx`; при этом дефолтная CLI-сборка
референсит альтернативные рендереры через `HarborWithSpectreTui` (включён по умолчанию). Вторая интерактивная
оболочка — `src/Harbor.Tui.ConsoleEx/` (opt-in: `HARBOR_TUI=consoleex`, см. README проекта).

## Troubleshooting


### Build fails with NU1603

Package version mismatch. Run `dotnet restore --force` and check `Directory.Build.props`.

### Tests fail with timeout

Check if `GetScrollback_ReturnsRecentEvents` is hanging — it's skipped by default due to blocking channel read. Other tests should be fast (<1s each).

### Provider not found

- Check `providers/<name>.json` exists.
- Check `id` field is lowercase alphanumeric.
- Run `dotnet run --project apps/Harbor.App.Cli -- providers` to see what's loaded.

### API key not found

- Check env var name matches `authEnvVar` in provider config.
- Conventional names: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, etc.
- Run `echo $ANTHROPIC_API_KEY` to verify.

## Contributing

1. Fork the repo.
2. Create a branch: `git checkout -b feature/my-feature`.
3. Make changes following [CLAUDE.md](../CLAUDE.md) conventions.
4. `dotnet build && dotnet test` — must pass with 0 warnings.
5. Commit with conventional commits: `feat: add X`, `fix: Y`, `docs: Z`.
6. Open a PR.

## License

MIT — see [LICENSE](../LICENSE).
