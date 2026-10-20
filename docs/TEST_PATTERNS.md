# Test Patterns — Copy-Paste Ready

> **Framework:** TUnit 1.61 (`[Test]` attribute, `await Assert.That(...)` assertions).
> **Run command:** `dotnet run --project tests/<Project> -c Release --no-build -- --minimum-expected-tests 1`
> **Never** use `dotnet test` — every CI job runs each test project as a plain
> executable, and no job runs `dotnet test`, so its behaviour here is unverified.
> (An earlier version of this line said it "discovers zero tests"; that was
> wrong — see [CONTRIBUTING.md §Why not `dotnet test`](../CONTRIBUTING.md#why-not-dotnet-test).)
> **Shared helpers** live in `tests/Harbor.TestKit/` and are referenced by test projects
> via `<ProjectReference Include="../Harbor.TestKit/Harbor.TestKit.csproj" />`.

---

## Quick Reference

| What you're testing | Where the file lives | Key helpers |
|---|---|---|
| A builtin `ITool` | `tests/Harbor.Tools.Builtin.Tests/ToolTests.cs` | `TestToolContext.Create()`, `Args()` |
| The agent loop | `tests/Harbor.Core.Tests/AgentLoopTests.cs` | `CreateLoop()`, `MockLlmClient`, `ScriptedLlmClient`, `TestSessionContext` |
| Permissions | `tests/Harbor.Application.Tests/PermissionBypassTests.cs` | `FakeAgentRegistry`, `PermissionService` |
| Storage (JSONL/SQLite/Memory) | `tests/Harbor.Storage.Jsonl.Tests/JsonlSessionStoreTests.cs` | `CreateStore()`, temp-dir cleanup |

---

## How to test a new builtin tool

**Goal:** Verify `ValidateArguments` + `ExecuteAsync` for one tool class.
**Template class name:** `<ToolName>Tests` (e.g. `ReadToolTests`).

### Minimal template

```csharp
using System.Text.Json;
using Harbor.Abstractions.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

public class MyToolTests
{
    [Test]
    public async Task Name_IsMyTool()
    {
        var tool = new MyTool(NullLogger<MyTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("my_tool");
    }

    [Test]
    public async Task ValidateArguments_MissingRequiredArg_ReturnsFailure()
    {
        var tool = new MyTool(NullLogger<MyTool>.Instance);
        var args = JsonDocument.Parse("{}").RootElement;

        var result = tool.ValidateArguments(args);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("my required arg");
    }

    [Test]
    public async Task ExecuteAsync_HappyPath_ReturnsSuccess()
    {
        var tool = new MyTool(NullLogger<MyTool>.Instance);
        var args = JsonDocument.Parse("""{"input": "hello"}""").RootElement;

        var result = await tool.ExecuteAsync(args, TestToolContext.Create());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("hello");
    }

    [Test]
    public async Task ExecuteAsync_InvalidInput_ReturnsError()
    {
        var tool = new MyTool(NullLogger<MyTool>.Instance);
        var args = JsonDocument.Parse("""{"input": "/nonexistent/path"}""").RootElement;

        var result = await tool.ExecuteAsync(args, TestToolContext.Create());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not found");
    }
}
```

### Shared helpers

`ToolContext` has ONE factory: `Harbor.TestKit.TestToolContext.Create()`. Do
not copy the 8-argument literal into a test file — a copy is a hand-written
contract that drifts from the record the moment the record changes (#1775).

```csharp
using Harbor.TestKit;

var result = await tool.ExecuteAsync(args, TestToolContext.Create());
```

The defaults are the canonical ones: session `"test-session"`, message
`"test-message"`, call `"test-call"`, agent `"code"`, no abort token, no
messages, a progress callback that drops updates, and a permission callback
that **allows without persisting**. Every value that ever differed between the
copies is a parameter, so nothing is lost by using the factory:

```csharp
// a different session / message / call id
TestToolContext.Create(sessionId: "s1", messageId: "m1", callId: "c1");

// a conversation snapshot
TestToolContext.Create(messages: [message]);

// a tool that resolves relative paths against a directory
TestToolContext.Create(workingDirectory: worktree);

// a permission answer other than "allow, do not persist"
TestToolContext.Create(ask: (_, _) =>
    Task.FromResult(new PermissionResponse(PermissionAction.Deny, false)));
```

**Before (the literal, repeated in every tool class):**
```csharp
private static ToolContext CreateContext() => new(
    "test-session", "test-message", "test-call", "code",
    CancellationToken.None, Array.Empty<AgentMessage>(),
    (_, _) => Task.CompletedTask,
    (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
    null!);
```

**After (one shared factory, 1 line at call sites):**
```csharp
var result = await tool.ExecuteAsync(args, TestToolContext.Create());
```

### Building args

For simple JSON use a raw string literal:

```csharp
var args = JsonDocument.Parse("""{"path": "/tmp/hello.txt"}""").RootElement;
```

For programmatic construction, copy the `Args` helper from
`NotebookToolTests.cs`:

```csharp
private static JsonElement Args(params (string key, string value)[] pairs)
{
    var dict = new Dictionary<string, object?>();
    foreach ((string k, string v) in pairs) dict[k] = v;
    return JsonDocument.Parse(JsonSerializer.Serialize(dict)).RootElement.Clone();
}

// usage
await tool.ExecuteAsync(Args(("action", "set"), ("key", "todo"), ("content", "buy milk")),
    TestToolContext.Create());
```

### Temp-file / temp-dir teardown pattern

Every tool test that touches the filesystem must clean up its temp directory.
Follow the existing try/finally idiom:

```csharp
string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(tempDir);
    // ... test body ...
}
finally
{
    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
}
```

For single-file tools (`WriteTool`, `EditTool`), use `Path.GetTempFileName()`
and `File.Delete` in `finally`.

### File layout

- All builtin tool tests live in `tests/Harbor.Tools.Builtin.Tests/`.
- Related tool classes share one file (`ToolTests.cs` holds `ReadToolTests`,
  `WriteToolTests`, `EditToolTests`, `GlobToolTests`, `GrepToolTests`,
  `BashToolTests`). Create a new `.cs` per tool only if the test surface is
  large enough to warrant it (e.g. `NotebookToolTests.cs`, `PatchToolTests.cs`).
- Each tool gets its own test class; add a shared private `NewTool` helper at
  the bottom of the class, and take the `ToolContext` from
  `Harbor.TestKit.TestToolContext.Create()`.

---

## How to test agent loop behavior

**Goal:** Verify the event sequence, message materialization, compaction, or
error handling of `AgentLoop.RunAsync`.
**File:** `tests/Harbor.Core.Tests/AgentLoopTests.cs`.

### Minimal template

```csharp
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

public class AgentLoopTests
{
    private static (AgentLoop loop, InMemoryEventBus bus) CreateLoop(ILlmClient client)
    {
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client);

        var tools = new ToolRegistry();
        tools.Freeze();

        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("test-model", "test"));

        var bus = new InMemoryEventBus();
        var promptBuilder = new SystemPromptBuilder();
        var compaction = new CompactionService(new TokenTracker(), providers,
            NullLogger<CompactionService>.Instance);
        var permissions = new PermissionService(agents,
            NullLogger<PermissionService>.Instance);
        var converter = new MessageConverter();
        var tokenTracker = new TokenTracker();
        var retryPolicy = new RetryPolicy();

        var loop = new AgentLoop(
            providers, tools, agents, promptBuilder, compaction,
            tokenTracker, retryPolicy, bus, permissions, converter,
            NullLogger<AgentLoop>.Instance);

        return (loop, bus);
    }

    [Test]
    public async Task RunAsync_TextDeltaOnly_CompletesAfterFirstTurn()
    {
        // Script: text stream + step finish, no tool calls → loop exits after turn 1.
        var client = new MockLlmClient(
            new TextDeltaEvent("0", "Hello, "),
            new TextDeltaEvent("0", "World!"),
            new StepFinishEvent(0, "stop", new Usage(10, 5)));

        var (loop, bus) = CreateLoop(client);
        var session = new TestSessionContext(
            Session.Create("/tmp", "code", "test", "test-model"));

        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        var result = await loop.RunAsync(session,
            AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(received.Any(e => e is AgentStartEvent)).IsTrue();
        await Assert.That(received.Any(e => e is TurnStartEvent)).IsTrue();
        await Assert.That(received.Any(e => e is MessageStartEvent)).IsTrue();
        await Assert.That(received.Any(e => e is MessageEndEvent)).IsTrue();
        await Assert.That(received.Any(e => e is AgentEndEvent)).IsTrue();
    }
}
```

### Event sequence checklist

The canonical happy path publishes this sequence (per the E2E spec in AGENTS.md):

```
AgentStartEvent → TurnStartEvent → MessageStartEvent → [MessageUpdateEvent*] →
MessageEndEvent → TurnEndEvent → AgentEndEvent
```

Assert it like:

```csharp
await Assert.That(receivedEvents.Any(e => e is AgentStartEvent)).IsTrue();
await Assert.That(receivedEvents.Any(e => e is TurnStartEvent)).IsTrue();
await Assert.That(receivedEvents.Any(e => e is MessageStartEvent)).IsTrue();
await Assert.That(receivedEvents.Any(e => e is MessageEndEvent)).IsTrue();
await Assert.That(receivedEvents.Any(e => e is AgentEndEvent)).IsTrue();
```

### Mock LLM clients

Two patterns are used. Pick based on need:

**`MockLlmClient`** — single scripted sequence per test (defined locally in the test file):

```csharp
private sealed class MockLlmClient(params LlmEvent[] events) : ILlmClient
{
    public ProviderId ProviderId => ProviderId.Create("test");

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in events) { yield return e; await Task.Yield(); }
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([TestModel]));
}
```

**`ScriptedLlmClient`** (from `Harbor.TestKit`) — different sequences per turn
(e.g. for compaction tests). Records every `LlmRequest` so you can assert how
many messages were sent:

```csharp
var client = new ScriptedLlmClient(
    new LlmEvent[] { new TextDeltaEvent("s", "summary"), new StepFinishEvent(0, "stop", new Usage(0, 10)) },
    new LlmEvent[] { new TextDeltaEvent("t", "done"),    new StepFinishEvent(0, "stop", new Usage(5, 5)) });

// after RunAsync:
await Assert.That(client.Requests.Count).IsEqualTo(2);
await Assert.That(client.Requests[0].Messages.Count).IsEqualTo(1); // summary turn
```

### Common LlmEvent recipes

```csharp
// Simple text stream
new TextDeltaEvent("0", "Hello, "),
new TextDeltaEvent("0", "World!"),
new StepFinishEvent(0, "stop", new Usage(10, 5))

// Tool-calling turn
new ToolCallStartEvent("call-1", "read"),
new ToolCallDeltaEvent("call-1", "{\"path\":\"README.md\"}"),
new ToolCallEndEvent("call-1", "read", JsonDocument.Parse("{\"path\":\"README.md\"}").RootElement),
new StepFinishEvent(0, "tool_use", new Usage(10, 5))

// Thinking + text
new ThinkingDeltaEvent("0", "Let me reason..."),
new TextDeltaEvent("0", "Answer"),
new StepFinishEvent(0, "stop", new Usage(1, 1))

// Error path
new ErrorEvent("upstream blew up"),
new StepFinishEvent(0, "stop", null)
```

### Before / after — compaction test boilerplate

**Before (inline everything — 60 lines of wiring):**

```csharp
var providers = new ProviderRegistry();
providers.Register(ProviderId.Create("test"), () => client);
var tools = new ToolRegistry(); tools.Freeze();
var agents = new AgentRegistry();
agents.Register(AgentDefinition.CodeDefault("test-model", "test"));
var bus = new InMemoryEventBus();
var compaction = new CompactionService(new TokenTracker(), providers,
    NullLogger<CompactionService>.Instance);
// ... configure compaction ...
var permissions = new PermissionService(agents, NullLogger<PermissionService>.Instance);
var converter = new MessageConverter();
var loop = new AgentLoop(providers, tools, agents, new SystemPromptBuilder(),
    compaction, new TokenTracker(), new RetryPolicy(), bus, permissions,
    converter, NullLogger<AgentLoop>.Instance);
```

**After (single helper, 3 lines at call sites):**

```csharp
private static AgentLoopHarness CreateLoop(ILlmClient client,
    Action<CompactionService>? configure = null)
    => new AgentLoopHarness(client, configure);
```

> **Tip:** If you're adding several agent-loop tests, extract a `AgentLoopHarness`
> class that returns `(loop, bus, client)` and handles all the boilerplate.
> The existing `CreateLoop` in `AgentLoopTests.cs:37` is the de-facto template —
> copy and trim it to just the fields you need.

---

## How to test permissions

**Goal:** Verify that `PermissionService.CheckAsync` returns the expected
`PermissionAction` for a given ruleset + tool args.
**File:** `tests/Harbor.Application.Tests/PermissionBypassTests.cs`.

### Minimal template

```csharp
using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Permissions;
using Harbor.Application.Tests.Fakes;   // FakeAgentRegistry
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

public class MyPermissionTests
{
    private static PermissionService CreateService(
        PermissionRuleset ruleset,
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? asker = null)
    {
        var agent = new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "Test agent",
            "test-model",
            "test",
            ruleset);

        var registry = new FakeAgentRegistry(agent);
        return new PermissionService(registry, NullLogger<PermissionService>.Instance, asker);

    private static JsonElement Args(object payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    [Test]
    public async Task CheckAsync_RuleName_ExpectedAction()
    {
        var svc = CreateService(PermissionRuleset.Default);

        var result = await svc.CheckAsync("code", "my_tool",
            Args(new { path = "/src/foo.cs" }));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_RuleName_AskDowngrade()
    {
        var rules = PermissionRuleset.Default.Merge(new PermissionRuleset(new[]
        {
            new PermissionRule("read", "*", PermissionAction.Ask)
        }));
        var svc = CreateService(rules);

        var result = await svc.CheckAsync("code", "read",
            Args(new { path = "/src/foo.cs" }));

        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Ask);
    }
}
```

### PermissionRuleset construction

Use the built-in `PermissionRuleset.Default` and `Merge` rather than building
from raw rules:

```csharp
// Start from safe defaults (read-only allow, writes ask, etc.)
var rules = PermissionRuleset.Default;

// Merge additional rules on top of defaults
var denyBash = rules.Merge(new PermissionRuleset(new[]
{
    new PermissionRule("bash", "*", PermissionAction.Deny)
}));

// Empty ruleset (all deny) for strict harness tests
var denyAll = PermissionRuleset.Empty;
```

> Check the exact rule constructors in `src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs:321`.

### Testing the "Ask" callback

When a rule resolves to `PermissionAction.Ask`, the service invokes the
user-asking callback. Test it by injecting a fake asker:

```csharp
    [Test]
    public async Task CheckAsync_AskRule_PromptsUser()
    {
        var rules = PermissionRuleset.Default.Merge(new PermissionRuleset(new[]
        {
            new PermissionRule("bash", "*", PermissionAction.Ask)
        }));
        var prompted = new List<PermissionRequest>();

        var svc = CreateService(rules, request =>
        {
            prompted.Add(request);
            return Task.FromResult(new PermissionResponse(PermissionAction.Allow, false));
        });

        var result = await svc.CheckAsync("code", "bash", Args(new { command = "ls" }));

        await Assert.That(prompted.Count).IsEqualTo(1);
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }
}
```

The `asker` parameter is the 3rd argument to the `PermissionService` constructor:

```csharp
new PermissionService(agents, logger, userAsker: (request, ct) => { ... })
```

### Before / after — Args helper

**Before (verbose inline JSON):**
```csharp
var args = JsonDocument.Parse(
    JsonSerializer.Serialize(new { command = "cat setup.sh; rm -rf ~" }))
    .RootElement.Clone();
```

**After (one-line `Args` helper, defined once per test class):**
```csharp
private static JsonElement Args(object payload) =>
    JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

var result = await svc.CheckAsync("code", "bash", Args(new { command = "cat setup.sh; rm -rf ~" }));
```

---

## How to test storage / sessions

**Goal:** Verify create/read/append/delete round-trips and cancellation behavior.
**File:** `tests/Harbor.Storage.Jsonl.Tests/JsonlSessionStoreTests.cs`.

### Minimal template

```csharp
using Harbor.Abstractions.Models;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Storage.Jsonl.Tests;

public class MyStoreTests
{
    // Capture the root path so cleanup works even without GetRootDirectory().
    private static (JsonlSessionStore store, string root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var store = new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
        return (store, root);
    }

    [Test]
    public async Task CreateAsync_ReturnsValidSession()
    {
        var (store, root) = CreateStore();
        try
        {
            var result = await store.CreateAsync("/test/dir", "code", "anthropic", "claude-opus-4");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsNotNull();
            await Assert.That(result.Value.Directory).IsEqualTo("/test/dir");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AppendMessageAsync_ThenGetMessagesAsync_ReturnsInOrder()
    {
        var (store, root) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "openai", "gpt-4o")).Value;

            await store.AppendMessageAsync(session.Id,
                new UserMessage("m1", session.Id, DateTimeOffset.UtcNow, "hello", "code", "test-model"));

            var messages = await store.GetMessagesAsync(session.Id);

            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("hello");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AppendMessageAsync_PreCancelledToken_PropagatesCancellation()
    {
        var (store, root) = CreateStore();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.That(async () =>
                await store.CreateAsync("/dir", "code", "openai", "gpt-4o", cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
```

### Temp-directory cleanup

Every storage test creates its own temp directory via `CreateStore()` and must
clean up in `finally`. `GetRootDirectory()` is a test-only extension method
defined at the bottom of `JsonlSessionStoreTests.cs` (it reads the private
`_rootDirectory` field via reflection). If that extension isn't available in
your test project, capture the temp path in a local variable:

```csharp
private static (JsonlSessionStore store, string root) CreateStoreWithRoot()
{
    string root = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return (new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance), root);
}

// usage in tests
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
```

### Test message factories

Use `Harbor.TestKit.TestMessages` to build realistic `AgentMessage` instances
without hand-rolling constructors:

```csharp
var userMsg    = TestMessages.User("Hello", sessionId: "session-1");
var assistant  = TestMessages.Assistant("Hi there!", sessionId: "session-1");
var toolResult = TestMessages.ToolResult("read", "file contents", callId: "call-1", sessionId: "session-1");
```

### Memory store alternative

For loop-level tests that don't need persistence, use the in-memory store
(`Harbor.Storage.Memory`) which requires no temp-dir cleanup:

```csharp
var store = new MemorySessionStore();
var session = (await store.CreateAsync("/test", "code", "test", "test-model")).Value;
```

---

## How to test session messages & events

**Goal:** Verify message conversion or event payloads.
**File:** `tests/Harbor.Core.Tests/MessageConverterTests.cs`.

### Minimal template

```csharp
using Harbor.Abstractions.Models;
using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

public class MyMessageTests
{
    private static readonly string SessionId = "session-1";

    [Test]
    public async Task ToLlmMessages_UserMessage_ConvertsToLlmUserMessage()
    {
        var converter = new MessageConverter();
        var user = new UserMessage("m1", SessionId, DateTimeOffset.UtcNow,
            "Hello, agent!", "code", "test-model");

        var result = converter.ToLlmMessages(new AgentMessage[] { user });

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0]).IsTypeOf<LlmUserMessage>();
        var userMsg = (LlmUserMessage)result[0];
        await Assert.That(userMsg.Role).IsEqualTo("user");
    }

    [Test]
    public async Task ToolCallEvent_HasExpectedFields()
    {
        var evt = new ToolCallStartEvent("call-1", "read");

        await Assert.That(evt.Id).IsEqualTo("call-1");
        await Assert.That(evt.ToolName).IsEqualTo("read");
    }
}
```

---

## TUnit cheat sheet

### Attributes

| Attribute | Usage |
|---|---|
| `[Test]` | Marks a method as a test (must return `Task` or `void`). |
| `[Before(HookType.TestDiscovery)]` | Global setup (use in `GlobalSetup` class). |
| `[NotInParallel]` | Runs the test **completely alone** — no other test in the process overlaps it. |
| `[NotInParallel("group")]` | Serializes against other tests carrying an **overlapping key only**. Not a global lock — see below. |
| `[RunOn(OS.Linux)]` | Run only on Linux; skipped elsewhere. Also `[ExcludeOn(OS.Windows \| OS.MacOs)]` for the inverse. |
| `[Timeout(ms)]` | Per-test wall-clock budget. The runner cancels the test's token when the budget elapses and fails the test as a timeout. |

Every row above is a TUnit type that resolves in any test project. The one
bespoke attribute this file used to list here, `[SkipWhenNotLinux]`, is
`internal` to `tests/Harbor.Tools.Builtin.Tests` and resolves nowhere else —
copying it into another project is CS0246, not a skip.

### Timeouts and cancellation

A test that needs a bound takes the runner-injected `CancellationToken` and
declares `[Timeout(ms)]`:

```csharp
[Test]
[Timeout(30_000)]
public async Task Store_Appends_And_Reads(CancellationToken cancellationToken)
{
    var store = new JsonlSessionStore(path);
    await store.AppendAsync(msg, cancellationToken);
}
```

`CancellationToken` must be the **last** parameter (TUnit0062). TUnit creates one
`CancellationTokenSource` per test attempt, links it to the `[Timeout]` budget and
cancels it when the budget elapses. An operation that honours the token aborts
at once and the test is reported as a timeout; the runner does not sit on a hung
test until the shard's own limit. A hand-rolled `new CancellationTokenSource(...)`
does the opposite: it fires its token but the runner keeps waiting for the test,
so the failure shows up as a slow shard rather than a failed test.

Keep a manual CTS only when the test drives the cancellation itself: a
pre-cancelled token, a token the test cancels mid-run, or a bounded negative
probe whose window is the assertion.

### Parallelism: what each `[NotInParallel]` form actually guarantees

Read from TUnit 1.61.0's scheduler, not from the XML docs. This distinction is
the one that has cost the most: a named key *looks* like a global lock, is not
one, and the mistake has been made twice (#703's palette flake, #704).

| Form | Serialized against | Scope |
|---|---|---|
| `[NotInParallel("a")]` | other tests whose key set **intersects** `{a}` | one test process |
| `[NotInParallel(new[] { "a", "b" })]` | other tests whose key set intersects `{a}` **or** `{b}` | one test process |
| `[NotInParallel]` | **every other test, keyed or not** | one test process |

**The array is not optional shorthand — the two-argument form does not compile.**
TUnit 1.61.0's `NotInParallelAttribute` (`src/TUnit.Core/Attributes/TestMetadata/`
`NotInParallelAttribute.cs`, tag `v1.61.0`) declares exactly three constructors —
`()`, `(string constraintKey)` and `(string[] constraintKeys)`. There is no
`params` overload, so `[NotInParallel("a", "b")]` is **CS1729** — this table
shipped that spelling for a day before CI caught it (#849). The array spelling is
also what TUnit's own XML documentation uses for its two-key example.

Two keys and one shared key are not the same thing, and the array is not a lock
tier. `new[] { "a", "b" }` means *wait for anyone holding `a`, and for anyone
holding `b`* — the union of two peer sets, never the two sets locking each other.
It collapses to a single shared key `c` **only** if every class holding `a` and
every class holding `b` is re-keyed to `c` as well; a class that keeps only `a`
stays invisible to `c`. So use one shared key when you are free to name both
groups — fewer names to keep straight, and it is the only form whose intent is
readable from the key — and use the array when `a` and `b` are existing groups
you did not get to name. That is the case #823 hit.

The array constructor is also the only one that checks its input, and it does so
at **runtime**: `[NotInParallel(new[] { "a", "a" })]` compiles and then throws
`ArgumentException("Duplicate constraint keys are not allowed.")`.

Three consequences, all of them load-bearing:

1. **A keyed test runs concurrently with every unkeyed test in the same
   assembly.** The two buckets are separate phases joined with
   `RunPhasesConcurrentlyAsync`, so `ConstraintKeyScheduler` sees only other
   *keyed* tests. A class that mutates a process-wide static and carries a key
   is protected from its named peers and from nothing else.
2. **Keys do not cross processes.** CI runs one `dotnet exec` per test project,
   so `"pty"` in `Harbor.Tui.CellForge.Tests` and `"pty"` in
   `Harbor.Tui.CellForge.PtyTests` are two unrelated keys. A key held by exactly
   one class in its assembly is a label with no effect at all.
3. **Different keys run concurrently with each other.** A key names *peers*, not
   a lock tier. `[NotInParallel("pty")]` and `[NotInParallel("ipc")]` never wait.

So: use a key when the set of competitors is knowable and enumerable (a shared
pseudo-terminal, one headless Avalonia session); use the bare form when you
mutate a process-wide static and the readers are unkeyed. Prefer not writing
process state at all — that is what #700 settled on, and it is cheaper than
either attribute.

If you change the parallelism attribute on a class, say so in the commit
message and keep the reason as a comment on the attribute. #720 put the key back
on `ThemeFileWatcherTests` without a word, and the class sat racing unkeyed
palette readers until this was noticed.

#### Which form to write: the decision procedure

The table above says what each form *does*. This says which one to *write*.

> **Bare** when the class mutates a process-wide static AND the readers of
> that static are unkeyed — i.e. when you cannot enumerate the competitors.
> **A named key** when the competitors are knowable and enumerable: the other
> process-environment writers, the other PTY holders, the classes that share
> one headless session.

A named key is a label as much as a lock — a key nobody else holds excludes
nothing at all. So before writing one, *name the peers*. If the honest answer
is "every other test in the assembly", the form is the bare one.

The bare form is not a smell and it is not the default. Each class that uses
it in this repository says why in a comment on the attribute, and the reasons
are always the same shape: a process-global palette, a process-global counter,
a process-wide allocation measurement, a wall-clock stopwatch, `Console.Out`.
Those are exactly the cases where the competitor set is the whole assembly.

#### One key on EVERY class is a sharding problem, not a locking problem

A named key that every class in a project holds serialises the whole project:
the wall time becomes the SUM of the test durations, and the parallelism
factor (sum of durations ÷ wall) falls to ≈1. That is *correct* — those
classes really do share one resource — but it is not the end of the story,
because the same classes are free to run in **parallel processes**: a process
has its own statics, its own environment, and its own application instance.

`Harbor.E2E.App.Avalonia` is the worked example. Fifteen classes, one
`[NotInParallel("e2e-framework")]` key, one headless Avalonia application
(`Application.Current` is a per-AppDomain singleton — there is no second app
to build), one `$HOME` and one Harbor home. Re-keying the classes apart would
not make the suite faster; it would make two of them drive the same window.
The lever is the process, not the attribute: the two shards are defined in
[`E2eShards.cs`](../tests/Harbor.E2E.App.Avalonia/E2eShards.cs), guarded by
`E2eShardsTests` (which fails if a class is left out of both shards), and run
by CI as two `dotnet exec` invocations of the same assembly with different
`--treenode-filter` expressions.

### Running tests

```bash
# Single test project (REQUIRED — never use dotnet test)
dotnet run --project tests/Harbor.Tools.Builtin.Tests -c Release --no-build -- --minimum-expected-tests 1

# Filtered — TUnit uses --treenode-filter, NOT --filter
dotnet run --project tests/Harbor.Tools.Builtin.Tests -c Release --no-build -- \
    --treenode-filter "/*/*/ReadToolTests/*"

# Multiple filters — pass after --
dotnet run --project tests/Harbor.Core.Tests -c Release -- --minimum-expected-tests 1 --treenode-filter "/*/*/AgentLoopTests/RunAsync_TextDeltaOnly*"
```

### Assertions

```csharp
// Booleans
await Assert.That(result.IsSuccess).IsTrue();
await Assert.That(result.IsFailure).IsTrue();

// Equality
await Assert.That(value).IsEqualTo("expected");
await Assert.That(actions).IsEqualTo(3);

// Collections
await Assert.That(list).HasCount().EqualTo(5);
await Assert.That(list).Contains("item");

// Comparators
await Assert.That(count).IsGreaterThan(0);
await Assert.That(count).IsLessThan(10);
await Assert.That(action).IsNotEqualTo(PermissionAction.Deny);

// Exceptions
await Assert.That(async () => await fragile.Method())
    .Throws<OperationCanceledException>();

await Assert.That(async () => await fragile.Method())
    .Throws<InvalidOperationException>(ex => ex.Message.Contains("expected text"));
```

### Data-driven tests

TUnit supports inline data via `[Arguments]` and `[MethodDataSource]`:

```csharp
using TUnit.Core;

// Inline arguments — TUnit auto-generates one test case per [Arguments] row
[Arguments("read", "src/Program.cs")]
[Arguments("write", "src/Program.cs")]
[Arguments("bash", "src/Program.cs")]
[Test]
public async Task CheckAsync_Paths_AreAllowedFor(string toolName, string path)
{
    // ...
}

// Method data source — pull from a static method returning IEnumerable<object[]>
[MethodDataSource(nameof(GetPermissionCases))]
[Test]
public async Task CheckAsync_BuiltinCases(string toolName, string args, PermissionAction expected)
{
    // ...
}

public static IEnumerable<object?[]> GetPermissionCases()
{
    yield return new object?[] { "bash", Args(new { command = "cat setup.sh; rm -rf ~" }), PermissionAction.Deny };
    yield return new object?[] { "read", Args(new { path = "/src/foo.cs" }),    PermissionAction.Allow };
}
```

### Hand fakes over mocks

Harbor tests use hand-written fakes (`CountingTool`, `ScriptedLlmClient`,
`ThrowingLlmClient` in `Harbor.TestKit`) instead of a mocking library: few
interfaces, zero proxy cost, AOT-safe. `TUnit.Mocks` measured zero uses and
was removed (#1087) — do not re-add it for new tests.

### Event subscription capture

```csharp
var received = new List<AgentEvent>();
bus.Subscribe(async (evt, ct) => received.Add(evt));           // untyped — catches all
bus.Subscribe<AgentErrorEvent>(async (evt, ct) => errors.Add(evt));  // typed — specific event type
bus.Subscribe<TurnStartEvent>(async (evt, ct) => turns.Add(evt));
```

### Per-test setup/teardown (hooks)

Prefer `[Before(Test)]` / `[After(Test)]` hooks over constructors +
`IDisposable` for per-test setup/teardown (#1087): hooks keep the lifecycle
visible next to the tests and compose with data sources, while a ctor buries
it. Keep `IDisposable` only for fixtures shared across tests (use
`ClassDataSource` for those instead):

```csharp
public class MyToolTests
{
    private string _tempDir = string.Empty;

    [Before(Test)]
    public void CreateTempDir()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [After(Test)]
    public void DeleteTempDir()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }
}
```

> Hooks can be `async Task` too — use `async Task` for async setup/teardown.

---

## Harbor.TestKit — available helpers

All in the `Harbor.TestKit` namespace. Add a `<ProjectReference>` to
`tests/Harbor.TestKit/Harbor.TestKit.csproj` if your test project doesn't already reference it.

| Helper | What it provides |
|---|---|
| `FakeAgentRegistry(params AgentDefinition[])` | `IAgentRegistry` with exactly the given agents. |
| `FakeToolRegistry(params ITool[])` | `IToolRegistry` over the given tools; `Freeze()` is a no-op. |
| `CountingTool` | `ITool` that counts executions and records raw args — use as a canary for timeout/permission tests. |
| `ScriptedLlmClient(params LlmEvent[][] scripts)` | `ILlmClient` replaying scripted events; exposes `Requests` list and `GetModelsAsync`. |
| `ThrowingLlmClient` | `ILlmClient` whose `StreamAsync` always throws. |
| `TestMessages.*` | Message builders: `User(content, sid)`, `Assistant(text, sid)`, `ToolResult(tool, out, callId, sid)`. |
| `TestSessionContext(Session, seedMessages?)` | `ISessionContext` with `SteeringQueue` + `EnqueueSteering`. |

### Example: using TestKit in an agent-loop test

```csharp
using Harbor.TestKit;

public class ToolCallTests
{
    [Test]
    public async Task RunAsync_ToolCall_RunsAndAppendsResult()
    {
        var client = new ScriptedLlmClient(
            new LlmEvent[] { new TextDeltaEvent("0", "hi"), new StepFinishEvent(0, "stop", new Usage(5, 5)) });

        var (loop, bus) = CreateLoop(client);
        var session = new TestSessionContext(
            Session.Create("/tmp", "code", "test", "test-model"));

        // ToolCallEndEvent triggers a tool call in the loop; the CountingTool
        // proves it was actually invoked.
        var tool = new CountingTool();
        // ... register in the ToolRegistry used by CreateLoop ...

        await loop.RunAsync(session, AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(tool.Executions).IsEqualTo(1);
    }
}
```

---

## Project reference cheat sheet

| Test project | Key source references |
|---|---|
| `Harbor.Tools.Builtin.Tests` | `Harbor.Abstractions`, `Harbor.Tools.Builtin` |
| `Harbor.Core.Tests` | `Harbor.Abstractions`, `Harbor.Core`, `Harbor.Storage.Memory` |
| `Harbor.Application.Tests` | `Harbor.Abstractions`, `Harbor.Abstractions.Contracts`, `Harbor.Application` |
| `Harbor.Storage.Jsonl.Tests` | `Harbor.Abstractions`, `Harbor.Storage.Jsonl` |
| `Harbor.Abstractions.Tests` | `Harbor.Abstractions`, `Harbor.Abstractions.Contracts` |

> When you add a new tool, add its tests to `Harbor.Tools.Builtin.Tests` and
> reference `Harbor.Tools.Builtin` if not already present.
