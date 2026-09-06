# Harbor Test Patterns & Templates

> **Audience**: Harbor developers writing unit/integration tests.  
> **Framework**: TUnit 1.61.0, `TUnit.Assertions`.  
> **Convention**: Tests run as plain executables — never `dotnet test`.  
> **Location**: `tests/<Project>.Tests/` — one test class per production class.

---

## 1. Current State Snapshot

After reviewing 15+ test files across 10 test projects, the dominant patterns are:

| Pattern | Frequency | Pain point |
|---------|-----------|------------|
| `ToolContext` constructor boilerplate | 5 files | 12-parameter constructor repeated verbatim |
| Temp dir/file creation + `try/finally` cleanup | 8 files | Easy to forget cleanup; verbose |
| `JsonElement` arg construction via `JsonDocument.Parse(...)` | 4 files | Error-prone string interpolation for paths |
| Single-case `[Test]` methods | 100% | No data-driven coverage; copy-paste proliferation |
| Per-project duplicate fakes (`FakeAgentRegistry`, `FakeToolRegistry`) | 3 projects | `Harbor.TestKit` exists but isn't always used |
| Event subscription + collection inside each test | 6 files | Repeated `var received = new List<T>()` pattern |

Harbor already has excellent foundations:
- **`Harbor.TestKit`** provides `ScriptedLlmClient`, `FakeAgentRegistry`, `FakeToolRegistry`, `CountingTool`, `FakeEventBus`.
- **Fakes per project** (`AgentLoopFakes.cs`, `DefaultAgentFakes.cs`) extend the kit for lifecycle/steering tests.
- **Assertions** are consistent: `await Assert.That(result.IsSuccess).IsTrue()`.

The goal of this document is to **reduce boilerplate by 40-60%** using TUnit's data-driven features and shared fixtures, while keeping tests readable.

---

## 2. TUnit Data-Driven Features We Should Use

### 2.1 `[Arguments]` — Inline data rows

Use for **3+ similar cases** (valid/invalid args, allow/deny/ask rules, error messages).

```csharp
[Test]
[Arguments("read", "some/file.txt", PermissionAction.Allow)]
[Arguments("edit", ".env", PermissionAction.Deny)]
[Arguments("bash", "rm -rf /", PermissionAction.Deny)]
public async Task CheckAsync_RuleEvaluatesCorrectly(string tool, string argPath, PermissionAction expected)
{
    var agent = AgentWithRuleset(new PermissionRule(tool, "*", expected));
    var (svc, _) = CreateService(agent);
    var args = Args(("path", argPath));

    var result = await svc.CheckAsync("code", tool, args);

    await Assert.That(result.Value.Action).IsEqualTo(expected);
}
```

### 2.2 `[ClassDataSource]` — Expensive shared fixtures

Use for **objects that are costly to build** (real `JsonlSessionStore` with temp dir, `AgentLoop` with full wiring, SQLite DB).

```csharp
public class JsonlSessionStoreTests
{
    [ClassDataSource]
    public static IEnumerable<JsonlSessionStore> Stores =>
        Enumerable.Range(0, 3).Select(_ =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        });

    [Test]
    public async Task CreateAsync_ReturnsValidSession([ClassDataSource] JsonlSessionStore store)
    {
        var result = await store.CreateAsync("/test/dir", "code", "anthropic", "claude-opus-4");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Directory).IsEqualTo("/test/dir");
    }
}
```

> **Note**: `[ClassDataSource]` instances are shared across tests in the class. If you need per-test isolation, use `[ClassDataSource<Disposable>]` with `IDisposable` or a custom cleanup method. For temp files, prefer `using` + factory method unless the fixture is truly expensive.

---

## 3. Test Templates

### 3.1 Tool Execution Tests

**Scenario**: Validate args, execute, check result/output.  
**Reference**: `tests/Harbor.Tools.Builtin.Tests/ToolTests.cs`

#### Before (current pattern — 30 lines per test case)

```csharp
[Test]
public async Task ExecuteAsync_CreatesNewFile()
{
    string tempFile = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}.txt");
    try
    {
        var tool = new WriteTool(NullLogger<WriteTool>.Instance);
        var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"content\": \"test content\"}}").RootElement;
        var ctx = CreateContext();

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(File.Exists(tempFile)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(tempFile)).IsEqualTo("test content");
    }
    finally
    {
        if (File.Exists(tempFile)) File.Delete(tempFile);
    }
}

private static ToolContext CreateContext() => new(
    "test-session", "test-message", "test-call", "code",
    CancellationToken.None, Array.Empty<AgentMessage>(),
    (_, _) => Task.CompletedTask,
    (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
    null!);
```

#### After (template — 15 lines per case, reusable context)

```csharp
public class WriteToolTests
{
    private static ToolContext CreateContext() => TestToolContext.Create();

    [Test]
    public async Task ExecuteAsync_CreatesNewFile()
    {
        await using var file = new TempFile(".txt");
        var tool = new WriteTool(NullLogger<WriteTool>.Instance);
        var args = ToolArgs.Create(("path", file.Path), ("content", "test content"));
        var ctx = CreateContext();

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(file.Path)).IsEqualTo("test content");
    }
}
```

**Shared helpers to add in `Harbor.TestKit`**:

```csharp
// Harbor.TestKit/TestToolContext.cs
public static class TestToolContext
{
    public static ToolContext Create(CancellationToken ct = default) => new(
        "test-session", "test-message", "test-call", "code",
        ct, Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);
}

// Harbor.TestKit/ToolArgs.cs
public static class ToolArgs
{
    public static JsonElement Create(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach ((string k, string v) in pairs) dict[k] = v;
        return JsonDocument.Parse(JsonSerializer.Serialize(dict)).RootElement.Clone();
    }
}

// Harbor.TestKit/TempFile.cs
public sealed class TempFile : IAsyncDisposable
{
    public string Path { get; }
    public TempFile(string extension = "")
    {
        Path = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}{extension}");
    }
    public async ValueTask DisposeAsync()
    {
        if (File.Exists(Path))
        {
            await Task.Run(() => File.Delete(Path));
        }
    }
}

// Harbor.TestKit/TempDirectory.cs
public sealed class TempDirectory : IAsyncDisposable
{
    public string Path { get; }
    public TempDirectory()
    {
        Path = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
        return ValueTask.CompletedTask;
    }
}
```

---

### 3.2 Agent Loop Tests

**Scenario**: Script LLM events, run loop, assert on session messages + event bus.  
**Reference**: `tests/Harbor.Core.Tests/AgentLoopTests.cs`, `tests/Harbor.Application.Tests/AgentLoopLifecycleTests.cs`

#### Template

```csharp
public class MyAgentLoopTests
{
    private static AgentDefinition AllowAllAgent() => new(
        AgentName.Create("code"), "Code", "Test agent", "test-model", "test",
        new PermissionRuleset(new[] { new PermissionRule("*", "*", PermissionAction.Allow) }));

    private static AgentLoop CreateLoop(
        ScriptedLlmClient client,
        IToolRegistry? tools = null,
        ICompactionService? compaction = null,
        IEventBus? bus = null)
    {
        tools ??= new FakeToolRegistry();
        compaction ??= new FakeCompactionService();
        bus ??= new FakeEventBus();

        return new AgentLoop(
            new FakeProviderRegistry(client),
            tools,
            new FakeAgentRegistry(AllowAllAgent()),
            new StubSystemPromptBuilder(),
            compaction,
            new FakeTokenTracker(),
            new RetryPolicy(),
            bus,
            new PermissionService(new FakeAgentRegistry(AllowAllAgent()), NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    private static TestSessionContext NewSession(params AgentMessage[] seed) => new(
        Session.Create("/tmp", "code", "test", "test-model"), seed);

    [Test]
    public async Task RunAsync_TextDeltaOnly_CompletesAfterFirstTurn()
    {
        var client = new ScriptedLlmClient(
        [
            [new TextDeltaEvent("0", "Hello, "), new TextDeltaEvent("0", "World!"),
             new StepFinishEvent(0, "stop", new Usage(10, 5))]
        ]);

        var loop = CreateLoop(client);
        var session = NewSession();
        var received = new List<AgentEvent>();
        loop.EventBus.Subscribe(async (evt, ct) => received.Add(evt));

        var result = await loop.RunAsync(session, AllowAllAgent());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(received.Any(e => e is AgentStartEvent)).IsTrue();
        await Assert.That(received.Any(e => e is AgentEndEvent)).IsTrue();
        var assistant = session.Messages.OfType<AssistantMessage>().Single();
        string text = string.Concat(assistant.Parts.OfType<TextPart>().Select(t => t.Text));
        await Assert.That(text).IsEqualTo("Hello, World!");
    }
}
```

#### Before/After: Event capture

```csharp
// Before — repeated in every test
var (loop, _, _, _, bus) = CreateLoop(client);
var received = new List<AgentEvent>();
bus.Subscribe(async (evt, ct) => received.Add(evt));

// After — extract to loop helper or use FakeEventBus.Events directly
var loop = CreateLoop(client, bus: out var events);
// ... later:
await Assert.That(events.Events.Any(e => e is TurnStartEvent)).IsTrue();
```

---

### 3.3 Permission Tests

**Scenario**: allow/deny/ask rules, path traversal, bash injection.  
**Reference**: `tests/Harbor.Core.Tests/PermissionServiceTests.cs`, `tests/Harbor.Application.Tests/PermissionBypassTests.cs`

#### Template — Data-driven ruleset evaluation

```csharp
public class PermissionRulesetDataDrivenTests
{
    private static PermissionService CreateService(PermissionRuleset ruleset) =>
        new(new FakeAgentRegistry(AgentWithRuleset(ruleset)), NullLogger<PermissionService>.Instance);

    private static AgentDefinition AgentWithRuleset(params PermissionRule[] rules) => new(
        AgentName.Create("code"), "Code", "Test", "test-model", "test",
        new PermissionRuleset(rules));

    private static JsonElement Args(params (string key, string value)[] pairs) =>
        ToolArgs.Create(pairs);

    [Test]
    [Arguments("read", "src/file.cs", PermissionAction.Allow, "relative path is allowed")]
    [Arguments("read", "/etc/passwd", PermissionAction.Ask, "absolute path falls through")]
    [Arguments("edit", ".env", PermissionAction.Deny, "env files are hard-denied")]
    [Arguments("bash", "rm -rf /", PermissionAction.Deny, "destructive bash is denied")]
    [Arguments("write", "src/feature/x.ts", PermissionAction.Allow, "normal src write allowed")]
    public async Task CheckAsync_Evaluate_MatchesExpected(
        string tool, string path, PermissionAction expected, string because)
    {
        var ruleset = PermissionRuleset.Default;
        var svc = CreateService(ruleset);
        var args = Args(("path", path), ("command", path));

        var result = await svc.CheckAsync("code", tool, args);

        await Assert.That(result.Value.Action).IsEqualTo(expected, because);
    }
}
```

#### Template — Red-team bypass

```csharp
public class PermissionBypassTests
{
    private static PermissionService CreateService() =>
        new(new FakeAgentRegistry(AgentWithRuleset(PermissionRuleset.Default)),
            NullLogger<PermissionService>.Instance);

    private static AgentDefinition AgentWithRuleset(PermissionRuleset ruleset) => new(
        AgentName.Create("code"), "Code", "Red-team", "test-model", "test", ruleset);

    [Test]
    [Arguments("cat setup.sh; rm -rf ~", "chained destructive tail")]
    [Arguments("git diff | sh", "piped shell execution")]
    [Arguments("cat `whoami`.log", "backtick substitution")]
    [Arguments("cat README.md\nrm -rf ~/notes", "multiline command")]
    public async Task CheckAsync_BashAllowRule_BypassAttempts_AreNotAllow(string command, string _)
    {
        var svc = CreateService();
        var args = Args(("command", command));

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.Value.Action).IsNotEqualTo(PermissionAction.Allow);
    }
}
```

---

### 3.4 Session Store Tests

**Scenario**: CRUD, message ordering, cascading delete.  
**Reference**: `tests/Harbor.Storage.Tests/MemorySessionStoreTests.cs`, `tests/Harbor.Storage.Jsonl.Tests/JsonlSessionStoreTests.cs`, `tests/Harbor.Storage.Tests/SqliteSessionStoreTests.cs`

#### Template — Shared contract across backends

```csharp
public interface ISessionStoreContract
{
    Task<Result<Session>> CreateAsync(string directory, string agent, string provider, string model);
    Task<Result<Session>> GetAsync(string sessionId);
    Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null);
    Task<Result> AppendMessageAsync(string sessionId, AgentMessage message);
    Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId);
    Task<Result> DeleteAsync(string sessionId);
}

public abstract class SessionStoreContractTests
{
    [Test]
    public async Task CreateAsync_ReturnsSessionWithValidId()
    {
        var store = await CreateStoreAsync();
        var result = await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(string.IsNullOrEmpty(result.Value.Id)).IsFalse();
    }

    [Test]
    public async Task GetAsync_UnknownId_ReturnsFailure()
    {
        var store = await CreateStoreAsync();
        var result = await store.GetAsync("nonexistent");
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task AppendMessageAsync_PersistsMessage()
    {
        var store = await CreateStoreAsync();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        var msg = NewUserMessage(session.Id, "hello");

        var append = await store.AppendMessageAsync(session.Id, msg);
        await Assert.That(append.IsSuccess).IsTrue();

        var messages = await store.GetMessagesAsync(session.Id);
        await Assert.That(messages.Value.Count).IsEqualTo(1);
        await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("hello");
    }

    protected abstract Task<ISessionStoreContract> CreateStoreAsync();

    private static UserMessage NewUserMessage(string sessionId, string content, string idSuffix = "") => new(
        $"umsg-{idSuffix}{Guid.NewGuid():N}", sessionId, DateTimeOffset.UtcNow,
        content, "code", "claude-opus-4");
}
```

#### Concrete implementations

```csharp
// MemorySessionStoreContractTests.cs
public class MemorySessionStoreContractTests : SessionStoreContractTests
{
    protected override Task<ISessionStoreContract> CreateStoreAsync()
        => Task.FromResult<ISessionStoreContract>(new MemorySessionStore());
}

// SqliteSessionStoreContractTests.cs
public class SqliteSessionStoreContractTests : SessionStoreContractTests
{
    private string? _dbPath;
    protected override async Task<ISessionStoreContract> CreateStoreAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"harbor-sqlite-{Guid.NewGuid():N}.db");
        return new SqliteSessionStore(_dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!string.IsNullOrEmpty(_dbPath))
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        await base.DisposeAsync();
    }
}

// JsonlSessionStoreContractTests.cs
public class JsonlSessionStoreContractTests : SessionStoreContractTests
{
    private string? _tempDir;
    protected override async Task<ISessionStoreContract> CreateStoreAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        return new JsonlSessionStore(_tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
        await base.DisposeAsync();
    }
}
```

---

### 3.5 Event Bus Tests

**Scenario**: publish, subscribe, typed filter, unsubscribe, scrollback, middleware.  
**Reference**: `tests/Harbor.Core.Tests/EventBusTests.cs`, `tests/Harbor.Core.Tests/EventBusMiddlewareTests.cs`

#### Template

```csharp
public class MyEventBusTests
{
    [Test]
    public async Task PublishAsync_DeliversTo_AllSubscribers()
    {
        var bus = new InMemoryEventBus();
        var received1 = new List<AgentEvent>();
        var received2 = new List<AgentEvent>();

        bus.Subscribe(async (evt, ct) => received1.Add(evt));
        bus.Subscribe(async (evt, ct) => received2.Add(evt));

        var testEvent = new TurnStartEvent(1);
        await bus.PublishAsync(testEvent);

        await Assert.That(received1.Count).IsEqualTo(1);
        await Assert.That(received2[0]).IsEqualTo(testEvent);
    }

    [Test]
    public async Task Subscribe_TypedFilter_OnlyReceivesMatchingEvents()
    {
        var bus = new InMemoryEventBus();
        var turnEvents = new List<TurnStartEvent>();
        var messageEvents = new List<MessageStartEvent>();

        bus.Subscribe<TurnStartEvent>(async (evt, ct) => turnEvents.Add(evt));
        bus.Subscribe<MessageStartEvent>(async (evt, ct) => messageEvents.Add(evt));

        await bus.PublishAsync(new TurnStartEvent(1));
        await bus.PublishAsync(new MessageStartEvent(AssistantMessage.Empty("s", "m")));

        await Assert.That(turnEvents.Count).IsEqualTo(1);
        await Assert.That(messageEvents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Unsubscribe_StopsReceivingEvents()
    {
        var bus = new InMemoryEventBus();
        var received = new List<AgentEvent>();
        var sub = bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(new TurnStartEvent(1));
        sub.Dispose();
        await bus.PublishAsync(new TurnStartEvent(2));

        await Assert.That(received.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetScrollback_ReturnsRecentEvents_WithoutDraining()
    {
        var bus = new InMemoryEventBus(maxScrollback: 5);
        for (int i = 0; i < 10; i++)
            await bus.PublishAsync(new TurnStartEvent(i));

        var first = bus.GetScrollback(3);
        var second = bus.GetScrollback(3);

        await Assert.That(first.Count).IsEqualTo(3);
        await Assert.That(second.Count).IsEqualTo(3);
        await Assert.That(((TurnStartEvent)second[0]).TurnIndex).IsEqualTo(7);
    }
}
```

---

### 3.6 UI Renderer Tests

**Scenario**: render view with/without ViewModel, assert output contains expected strings.  
**Reference**: `tests/Harbor.Tui.Tests/BuiltinViewTests.cs`

#### Template

```csharp
public class MyViewTests
{
    [Test]
    public async Task Render_NoViewModel_WritesNothing()
    {
        var view = new MyView();
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Render_WithViewModel_WritesExpectedContent()
    {
        var vm = new MyViewModel { Title = "Hello", Count = 42 };
        var view = new MyView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("Hello");
        await Assert.That(ctx.Output).Contains("42");
    }

    [Test]
    public async Task Id_And_Placement_AreCorrect()
    {
        var view = new MyView();
        await Assert.That(view.Id).IsEqualTo("my-view");
        await Assert.That(view.Placement).IsEqualTo(TuiViewPlacement.ChatHistory);
    }
}
```

#### Event-driven ViewModel tests

```csharp
public class MyViewModelTests
{
    [Test]
    public async Task UpdateFromEventAsync_TextDelta_AppendsToBuffer()
    {
        var vm = new MyViewModel();
        await vm.UpdateFromEventAsync(new MessageStartEvent(AssistantMessage.Empty("s", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(
            new TextDeltaEvent("0", "hello"), AssistantMessage.Empty("s", "m")));

        await Assert.That(vm.StreamingBuffer).IsEqualTo("hello");
    }
}
```

---

## 4. Test Recipes (Copy-Paste)

### Recipe: How to test a new tool

1. Create `tests/Harbor.Tools.Builtin.Tests/MyToolTests.cs`
2. Use `TestToolContext.Create()` and `ToolArgs.Create(...)` from `Harbor.TestKit`
3. Use `TempFile` / `TempDirectory` for filesystem tools

```csharp
namespace Harbor.Tools.Builtin.Tests;

public class MyToolTests
{
    private static ToolContext Ctx => TestToolContext.Create();

    [Test]
    public async Task Name_IsMyTool()
    {
        var tool = new MyTool(NullLogger<MyTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("my_tool");
    }

    [Test]
    public async Task ValidateArguments_MissingRequired_ReturnsFailure()
    {
        var tool = new MyTool(NullLogger<MyTool>.Instance);
        var result = tool.ValidateArguments(ToolArgs.Create());
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    [Arguments("input1", "expected output 1")]
    [Arguments("input2", "expected output 2")]
    public async Task ExecuteAsync_ValidInput_ReturnsExpected(string input, string expected)
    {
        var tool = new MyTool(NullLogger<MyTool>.Instance);
        var args = ToolArgs.Create(("input", input));
        var result = await tool.ExecuteAsync(args, Ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).IsEqualTo(expected);
    }
}
```

### Recipe: How to test an agent behavior

1. Use `ScriptedLlmClient` from `Harbor.TestKit` (or project-local `Fakes`)
2. Wire `AgentLoop` with fakes via `CreateLoop(...)` helper
3. Assert on `session.Messages` and `eventBus.Events`

```csharp
[Test]
public async Task RunAsync_ToolCall_IsExecuted_AndResultAppended()
{
    var counter = new CountingTool();
    var client = new ScriptedLlmClient(
    [
        [new ToolCallStartEvent("c1", "counter"), new ToolCallDeltaEvent("c1", """{"n":5}"""),
         new StepFinishEvent(0, "tool_use", new Usage(4, 2))],
        [new TextDeltaEvent("0", "done"), new StepFinishEvent(1, "stop", new Usage(1, 1))]
    ]);

    var loop = CreateLoop(client, tools: new FakeToolRegistry(counter));
    var session = NewSession();

    var result = await loop.RunAsync(session, AllowAllAgent());

    await Assert.That(result.IsSuccess).IsTrue();
    await Assert.That(counter.Executions).IsEqualTo(1);
    await Assert.That(session.Messages.OfType<ToolResultMessage>().Count()).IsEqualTo(1);
}
```

### Recipe: How to test permissions

1. Build a `PermissionRuleset` with specific rules
2. Call `PermissionService.CheckAsync(agentName, toolName, args)`
3. Use `[Arguments]` for the allow/deny/ask matrix

```csharp
[Test]
[Arguments("read", "src/file.cs", PermissionAction.Allow)]
[Arguments("edit", ".env", PermissionAction.Deny)]
[Arguments("bash", "sudo rm -rf /", PermissionAction.Deny)]
public async Task CheckAsync_SecurityHardening(string tool, string path, PermissionAction expected)
{
    var svc = CreateService(PermissionRuleset.Default);
    var args = ToolArgs.Create(("path", path), ("command", path));

    var result = await svc.CheckAsync("code", tool, args);

    await Assert.That(result.Value.Action).IsEqualTo(expected);
}
```

### Recipe: How to test storage

1. Use the `SessionStoreContractTests` base class
2. Implement `CreateStoreAsync()` for your backend
3. All CRUD + message ordering tests are inherited

```csharp
public class MyNewStoreTests : SessionStoreContractTests
{
    protected override Task<ISessionStoreContract> CreateStoreAsync()
    {
        // create and return your store instance
    }
}
```

For backend-specific tests (concurrency, WAL, cancellation), add them in the concrete test class.

---

## 5. Before/After: Boilerplate Reduction

### Example 1 — Tool args + temp file (WriteTool)

```diff
- string tempFile = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}.txt");
- try
- {
-     var tool = new WriteTool(NullLogger<WriteTool>.Instance);
-     var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"content\": \"test content\"}}").RootElement;
-     var ctx = CreateContext();
-     var result = await tool.ExecuteAsync(args, ctx);
-     await Assert.That(result.IsError).IsFalse();
-     await Assert.That(File.Exists(tempFile)).IsTrue();
-     await Assert.That(await File.ReadAllTextAsync(tempFile)).IsEqualTo("test content");
- }
- finally
- {
-     if (File.Exists(tempFile)) File.Delete(tempFile);
- }
+ await using var file = new TempFile(".txt");
+ var tool = new WriteTool(NullLogger<WriteTool>.Instance);
+ var args = ToolArgs.Create(("path", file.Path), ("content", "test content"));
+ var result = await tool.ExecuteAsync(args, TestToolContext.Create());
+ await Assert.That(result.IsError).IsFalse();
+ await Assert.That(await File.ReadAllTextAsync(file.Path)).IsEqualTo("test content");
```

**Lines**: 15 → 5 (67% reduction)  
**Benefit**: No manual escaping, automatic cleanup, no `try/finally` noise.

### Example 2 — Permission rules evaluation

```diff
- [Test]
- public async Task CheckAsync_AllowRule_ReturnsAllow()
- {
-     var agent = AgentWithRuleset(new PermissionRule("read", "*", PermissionAction.Allow));
-     var (svc, _) = CreateService(agent);
-     var args = Args(("path", "some/file.txt"));
-     var result = await svc.CheckAsync("code", "read", args);
-     await Assert.That(result.IsSuccess).IsTrue();
-     await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
- }
-
- [Test]
- public async Task CheckAsync_DenyRule_ReturnsDeny()
- {
-     var agent = AgentWithRuleset(new PermissionRule("edit", "*.env", PermissionAction.Deny));
-     var (svc, _) = CreateService(agent);
-     var args = Args(("path", "/repo/secrets.env"));
-     var result = await svc.CheckAsync("code", "edit", args);
-     await Assert.That(result.IsSuccess).IsTrue();
-     await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
- }
+ [Test]
+ [Arguments("read", "some/file.txt", PermissionAction.Allow)]
+ [Arguments("edit", "/repo/secrets.env", PermissionAction.Deny)]
+ public async Task CheckAsync_RuleEvaluatesCorrectly(string tool, string path, PermissionAction expected)
+ {
+     var ruleset = new PermissionRuleset(new[] { new PermissionRule(tool, "*", expected) });
+     var svc = CreateService(ruleset);
+     var args = ToolArgs.Create(("path", path));
+     var result = await svc.CheckAsync("code", tool, args);
+     await Assert.That(result.Value.Action).IsEqualTo(expected);
+ }
```

**Lines**: 24 → 14 (42% reduction)  
**Benefit**: New cases are one-line additions; setup is shared.

### Example 3 — Session store contract

```diff
- // Each backend repeats: CreateAsync, GetAsync, ListAsync, AppendMessageAsync,
- // GetMessagesAsync, DeleteAsync tests (50+ lines each × 3 backends)
+ // One base class: 6 tests, ~80 lines total, inherited by all backends
```

**Lines**: 150+ × 3 → 80 + 15 per backend (90% reduction)  
**Benefit**: Contract is enforced uniformly; backend-specific tests live alongside.

---

## 6. Naming Conventions

| Element | Convention | Example |
|---------|-----------|---------|
| Test class | `{ProductionClass}Tests` | `ReadToolTests`, `PermissionServiceTests` |
| Test method | `MethodName_State_ExpectedResult` | `ExecuteAsync_NonExistentFile_ReturnsError` |
| Data-driven method | Same, but rows describe the variation | `CheckAsync_RuleEvaluatesCorrectly` |
| Fixture class | `{Type}Fixture` or `{Purpose}Fakes` | `AgentLoopFakes`, `DefaultAgentFakes` |
| Helper | `Create*()` or `New*()` | `CreateContext()`, `NewSession()` |
| Temp resource | `using var x = new TempFile(...)` | `await using var dir = new TempDirectory()` |

---

## 7. Assertion Patterns

### Result<T>

```csharp
// Success path
await Assert.That(result.IsSuccess).IsTrue();
await Assert.That(result.Value).IsEqualTo(expected);

// Failure path
await Assert.That(result.IsFailure).IsTrue();
await Assert.That(result.Error).Contains("expected substring");

// Shortcut — if you only care about the value
await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
```

### Collections

```csharp
await Assert.That(list.Count).IsEqualTo(3);
await Assert.That(list).Contains("item");
await Assert.That(list).IsEquivalentTo(new[] { "a", "b", "c" }); // order-independent
await Assert.That(list).HasCount(3); // fluent
```

### Strings

```csharp
await Assert.That(text).Contains("substring");
await Assert.That(text).StartsWith("prefix");
await Assert.That(text).IsEqualTo("exact");
```

### Exceptions

```csharp
await Assert.That(async () => await store.GetAsync("missing"))
    .Throws<OperationCanceledException>();
```

---

## 8. Anti-Patterns to Avoid

1. **Don't** use `dotnet test` — run tests as executables.
2. **Don't** copy-paste `CreateContext()` — put it in `Harbor.TestKit`.
3. **Don't** manually escape paths for JSON — use `ToolArgs.Create`.
4. **Don't** forget `try/finally` for temp files — use `TempFile` / `TempDirectory`.
5. **Don't** write 5 single-case tests when `[Arguments]` covers them in one method.
6. **Don't** create per-test `new FakeAgentRegistry()` when `Harbor.TestKit.FakeAgentRegistry` exists.
7. **Don't** block on async code (`.Result`, `.Wait()`) — always `await`.

---

## 9. Checklist Before Submitting a Test

- [ ] `dotnet build` passes with 0 warnings.
- [ ] Test runs via `dotnet run --project tests/<Project> -c Release --no-build -- --minimum-expected-tests 1`.
- [ ] Uses `Harbor.TestKit` helpers where applicable.
- [ ] Temp files/dirs use `TempFile` / `TempDirectory` or `try/finally`.
- [ ] 3+ similar cases use `[Arguments]`.
- [ ] Expensive fixture uses `[ClassDataSource]`.
- [ ] Test method name follows `Method_State_ExpectedResult`.
- [ ] No `TODO(principles)` markers introduced.
