# Harbor.TestKit Expansion Report

> **Branch:** chore/tests-max-perf  
> **Date:** 2026-09-05  
> **Scope:** Eliminate duplicate boilerplate across `tests/` projects

---

## 1. Current Harbor.TestKit Contents Summary

The project lives at `tests/Harbor.TestKit/` and currently contains **3 source files** (~220 lines total):

| File | Types | Lines |
|------|-------|-------|
| `Fakes.cs` | `FakeAgentRegistry`, `FakeToolRegistry`, `CountingTool` | ~80 |
| `ScriptedLlmClient.cs` | `ScriptedLlmClient`, `ThrowingLlmClient` | ~60 |
| `TestMessages.cs` | `TestMessages` (User/Assistant/ToolResult builders), `TestSessionContext` | ~80 |

**Dependencies:** `Harbor.Abstractions`, `Harbor.Diagnostics.Abstractions`, `CSharpFunctionalExtensions`

**Current consumers:** Only `Harbor.Core.Tests` and `Harbor.Application.Tests` (the latter via its own local copy in `tests/Harbor.Application.Tests/Fakes/`).

---

## 2. Duplicated Patterns with File:Line Examples

### 2.1 AgentDefinition + PermissionRuleset (Allow-All) — ~25 occurrences

Every test that exercises the agent loop or permission service manually constructs an `AgentDefinition` with a wildcard `Allow` ruleset:

```csharp
// tests/Harbor.Application.Tests/AgentLoopLifecycleTests.cs:12
private static AgentDefinition AllowAllAgent() => new(
    AgentName.Create("code"), "Code", "...", "test-model", "test",
    new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

// tests/Harbor.Application.Tests/ToolDispatcherFailClosedTests.cs:20
private static AgentDefinition CodeAgent() => new(
    AgentName.Create("code"), "Code", "fail-closed harness", "test-model", "test",
    new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

// tests/Harbor.Application.Tests/AgentLoopCacheStrategyTests.cs:13
private static AgentDefinition AllowAllAgent() => new(...same pattern...);

// tests/Harbor.Core.Tests/PermissionServiceTests.cs:12
private static AgentDefinition AgentWithRuleset(params PermissionRule[] rules) => new(
    AgentName.Create("code"), "Code", "Default coding agent.", "test-model", "test",
    new PermissionRuleset(rules));

// tests/Harbor.Application.Tests/BashDenyEndToEndTests.cs:14
private static AgentDefinition AgentWith(params PermissionRule[] extra) { ... }

// tests/Harbor.Application.Tests/ToolTimeoutTests.cs:25
private static AgentDefinition Agent(int timeoutSeconds) => new(...);
```

**Impact:** 12+ files, ~30 lines each = ~360 lines of duplicated agent-setup logic.

### 2.2 ScriptedLlmClient — 3 independent implementations

```csharp
// tests/Harbor.TestKit/ScriptedLlmClient.cs — canonical (1 impl)
public sealed class ScriptedLlmClient(params LlmEvent[][] scripts) : ILlmClient { ... }

// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs:10 — EXACT COPY
public sealed class ScriptedLlmClient(params LlmEvent[][] scripts) : ILlmClient { ... }

// tests/Harbor.Core.Tests/AgentLoopTests.cs:178 — DIFFERENT impl (no Requests list, has RequestSizes)
private sealed class ScriptedLlmClient(params LlmEvent[][] calls) : ILlmClient { ... }
```

**Impact:** 2 duplicate copies (~120 lines), plus the Core.Tests variant diverges in API surface.

### 2.3 MockLlmClient (single-script replay) — Core.Tests only

```csharp
// tests/Harbor.Core.Tests/AgentLoopTests.cs:153
private sealed class MockLlmClient(params LlmEvent[] events) : ILlmClient { ... }
```

No equivalent exists in TestKit. Every test in Core.Tests that needs a simple one-shot mock defines its own or uses this local class.

### 2.4 FakeAgentRegistry — 2 independent implementations

```csharp
// tests/Harbor.TestKit/Fakes.cs — canonical
public sealed class FakeAgentRegistry(params AgentDefinition[] agents) : IAgentRegistry { ... }

// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs — EXACT COPY
public sealed class FakeAgentRegistry(params AgentDefinition[] agents) : IAgentRegistry { ... }
```

**Impact:** 2 identical copies (~40 lines).

### 2.5 FakeToolRegistry — 2 independent implementations

```csharp
// tests/Harbor.TestKit/Fakes.cs — canonical
public sealed class FakeToolRegistry(params ITool[] tools) : IToolRegistry { ... }

// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs — near-identical copy
// (has same comment about ZLinq drop-in workaround)
public sealed class FakeToolRegistry(params ITool[] tools) : IToolRegistry { ... }
```

**Impact:** 2 copies (~45 lines).

### 2.6 CountingTool — 2 independent implementations

```csharp
// tests/Harbor.TestKit/Fakes.cs
public sealed class CountingTool : ITool { ... }

// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs — EXACT COPY
public sealed class CountingTool : ITool { ... }
```

**Impact:** 2 copies (~50 lines).

### 2.7 FakeEventBus — 2 independent implementations

```csharp
// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs:122
public sealed class FakeEventBus : IEventBus { ... }

// (No equivalent in Harbor.TestKit — Core.Tests uses real InMemoryEventBus)
```

**Impact:** 1 copy (~25 lines), not in TestKit at all.

### 2.8 StubSystemPromptBuilder — 2 independent implementations

```csharp
// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs
public sealed class StubSystemPromptBuilder : ISystemPromptBuilder { ... }

// (No equivalent in Harbor.TestKit)
```

**Impact:** 1 copy (~10 lines).

### 2.9 FakeTokenTracker — 1 copy outside TestKit

```csharp
// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs
public sealed class FakeTokenTracker(bool shouldCompact = false) : ITokenTracker { ... }
```

**Impact:** 1 copy (~25 lines), not in TestKit.

### 2.10 FakeCompactionService — 1 copy outside TestKit

```csharp
// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs
public sealed class FakeCompactionService : ICompactionService { ... }
```

**Impact:** 1 copy (~20 lines), not in TestKit.

### 2.11 TestSessionContext — 3 independent implementations

```csharp
// tests/Harbor.TestKit/TestMessages.cs — canonical
public sealed class TestSessionContext(Session session, IReadOnlyList<AgentMessage>? seedMessages = null) : ISessionContext { ... }

// tests/Harbor.Application.Tests/Fakes/AgentLoopFakes.cs:145 — EXACT COPY
public sealed class TestSessionContext(Session session, IReadOnlyList<AgentMessage>? seedMessages = null) : ISessionContext { ... }

// tests/Harbor.Core.Tests/AgentLoopTests.cs:200 — LOCAL COPY (no EnqueueSteering)
private sealed class TestSessionContext : ISessionContext { ... }
```

**Impact:** 3 copies (~90 lines).

### 2.12 CapturingStatsSession — 1 copy outside TestKit

```csharp
// tests/Harbor.Core.Tests/AgentLoopTests.cs:232
private sealed class CapturingStatsSession : ISessionContext { ... }
```

**Impact:** 1 copy (~25 lines).

### 2.13 FakeSessionStore — 1 copy outside TestKit

```csharp
// tests/Harbor.Application.Tests/Fakes/DefaultAgentFakes.cs
public sealed class FakeSessionStore(Session session) : ISessionStore { ... }
```

**Impact:** 1 copy (~100 lines).

### 2.14 MultiSessionStore — 1 copy outside TestKit

```csharp
// tests/Harbor.Application.Tests/SteeringCrossSessionTests.cs
public sealed class MultiSessionStore(params Session[] sessions) : ISessionStore { ... }
```

**Impact:** 1 copy (~70 lines).

### 2.15 FailingAppendStore — 1 copy outside TestKit

```csharp
// tests/Harbor.Application.Tests/StorePersistFailureTests.cs
private sealed class FailingAppendStore(Session session) : ISessionStore { ... }
```

**Impact:** 1 copy (~70 lines).

### 2.16 JsonElement Args() helper — 3 independent signatures

```csharp
// tests/Harbor.Application.Tests/PermissionBypassTests.cs:13
private static JsonElement Args(object payload) => ...;

// tests/Harbor.Core.Tests/PermissionServiceTests.cs:14
private static JsonElement Args(params (string key, string value)[] pairs) => ...;

// tests/Harbor.Application.Tests/BashDenyEndToEndTests.cs:37
private static JsonElement Args(string command) => ...;
```

**Impact:** 3 copies (~30 lines), 3 different signatures for the same purpose.

### 2.17 AgentLoop creation boilerplate — 11-parameter wiring repeated 8+ times

Every test that creates an `AgentLoop` manually wires 11 constructor arguments:

```csharp
// tests/Harbor.Application.Tests/AgentLoopLifecycleTests.cs:20
private static AgentLoop CreateLoop(ScriptedLlmClient client, FakeToolRegistry tools, ...) {
    return new AgentLoop(
        new FakeProviderRegistry(client),
        tools,
        new FakeAgentRegistry(agent),
        new StubSystemPromptBuilder(),
        new FakeCompactionService(),
        new FakeTokenTracker(),
        new RetryPolicy(),
        new FakeEventBus(),
        new PermissionService(agents, NullLogger<PermissionService>.Instance),
        new MessageConverter(),
        NullLogger<AgentLoop>.Instance);
}

// tests/Harbor.Application.Tests/ToolDispatcherFailClosedTests.cs:28 — same pattern
// tests/Harbor.Application.Tests/AgentLoopCacheStrategyTests.cs:20 — same pattern
// tests/Harbor.Application.Tests/CachingSystemPromptBuilderTests.cs:86 — same pattern
// tests/Harbor.Application.Tests/ToolTimeoutTests.cs:74 — same pattern
// tests/Harbor.Core.Tests/AgentLoopTests.cs:20 — same pattern (different fakes)
// tests/Harbor.Application.Tests/SteeringCrossSessionTests.cs — repeated 3 times
```

**Impact:** 8+ occurrences, ~15 lines each = ~120 lines of repetitive constructor wiring.

### 2.18 Session.Create() path boilerplate

```csharp
// tests/Harbor.Application.Tests/AgentLoopLifecycleTests.cs:45
private static TestSessionContext NewSession() => new(
    Session.Create("/tmp/harbor-agentloop-lifecycle-tests", "code", "test", "test-model"));

// tests/Harbor.Application.Tests/ToolTimeoutTests.cs:88
Session.Create("/tmp/harbor-tool-timeout-tests", "code", "test", "test-model")

// tests/Harbor.Application.Tests/AgentLoopCacheStrategyTests.cs:44
Session.Create("/tmp/harbor-cache-strategy-tests", "code", "test", "test-model")

// tests/Harbor.Application.Tests/SubAgentRunnerTests.cs:12
Session.Create("/tmp/harbor-subtest", "code", "test", "test-model")
```

**Impact:** ~15+ files use hardcoded `"/tmp/harbor-..."` paths with identical `"code", "test", "test-model"` parameters.

### 2.19 Specialized LLM clients defined locally

```csharp
// tests/Harbor.Core.Tests/CompactionServiceTests.cs:157
private sealed class SummaryLlmClient : ILlmClient { ... }

// tests/Harbor.Core.Tests/CompactionErrorPathTests.cs:86
private sealed class SilentLlmClient : ILlmClient { ... }
private sealed class HangingLlmClient : ILlmClient { ... }
```

**Impact:** 3 specialized clients (~80 lines) not available in TestKit.

### 2.20 Provider test stubs

```csharp
// tests/Harbor.Providers.Tests/Stubs.cs
internal sealed class StubAuthResolver : IAuthResolver { ... }
internal sealed class StubModelCatalog : IModelCatalog { ... }
internal sealed class StubHttpHandler : HttpMessageHandler { ... }
```

**Impact:** 1 copy (~60 lines), not in TestKit.

---

## 3. Proposed New APIs/Helpers

All additions should go into `Harbor.TestKit` namespace. The project's `csproj` already references `Harbor.Abstractions` + `CSharpFunctionalExtensions`, which is sufficient for all proposed types.

### 3.1 `TestAgentBuilder` — fluent AgentDefinition factory

```csharp
namespace Harbor.TestKit;

public sealed class TestAgentBuilder
{
    public static TestAgentBuilder Create(string name = "code") => new(name);

    public TestAgentBuilder WithName(string name);
    public TestAgentBuilder WithModel(string modelId);
    public TestAgentBuilder WithProvider(string providerId);
    public TestAgentBuilder WithPermission(PermissionRuleset ruleset);
    public TestAgentBuilder AllowAll();  // wildcard Allow on all tools
    public TestAgentBuilder DenyAll();   // wildcard Deny on all tools
    public TestAgentBuilder AddRule(string tool, string argPattern, PermissionAction action);
    public TestAgentBuilder AsSubAgent(int maxSteps = 20);
    public TestAgentBuilder WithToolTimeout(int? seconds);
    public AgentDefinition Build();
}
```

**Replaces:** `AllowAllAgent()`, `CodeAgent()`, `AgentWithRuleset()`, `AgentWith()`, `Agent()` patterns in 12+ files.

### 3.2 `TestSessionFactory` — eliminate hardcoded paths

```csharp
namespace Harbor.TestKit;

public static class TestSessionFactory
{
    public static Session Create(string? testName = null);
    public static TestSessionContext CreateContext(string? testName = null, params AgentMessage[] seedMessages);
    public static Session CreateWithId(string sessionId, string? testName = null);
}
```

Uses `Path.GetTempPath()` + test class name for unique, OS-safe paths. Replaces all `"/tmp/harbor-..."` hardcoded paths.

### 3.3 Enhanced `ScriptedLlmClient` — consolidate 3 variants

Move the current TestKit `ScriptedLlmClient` to a shared location and add:

```csharp
namespace Harbor.TestKit;

// Existing: multi-script replay with Requests capture
public sealed class ScriptedLlmClient(params LlmEvent[][] scripts) : ILlmClient { ... }

// NEW: single-script convenience (replaces Core.Tests MockLlmClient)
public sealed class MockLlmClient(params LlmEvent[] events) : ILlmClient
{
    public List<LlmRequest> Requests { get; } = [];
    // Replays same script on every call
}

// NEW: specialized clients
public sealed class SilentLlmClient : ILlmClient { ... }    // streams nothing
public sealed class HangingLlmClient : ILlmClient { ... }    // delays forever
public sealed class SummaryLlmClient(string summary) : ILlmClient { ... }  // returns summary text
```

**Replaces:** `MockLlmClient`, `SilentLlmClient`, `HangingLlmClient`, `SummaryLlmClient` local definitions.

### 3.4 `FakeProviderRegistry` improvements

```csharp
namespace Harbor.TestKit;

// Already exists in Fakes.cs but unreferenced by Application.Tests
public sealed class FakeProviderRegistry(ILlmClient client) : IProviderRegistry { ... }
```

Make it `public` (it already is) and add a static `FromClient(ILlmClient client)` factory.

### 3.5 Consolidated fake registries (already in TestKit, promote usage)

The following already exist in `Harbor.TestKit/Fakes.cs` but are **duplicated** in `Harbor.Application.Tests/Fakes/`:

| Type | TestKit | Application.Tests |
|------|---------|-------------------|
| `FakeAgentRegistry` | ✅ | ❌ Duplicate |
| `FakeToolRegistry` | ✅ | ❌ Duplicate |
| `CountingTool` | ✅ | ❌ Duplicate |
| `ScriptedLlmClient` | ✅ | ❌ Duplicate |

**Action:** Remove duplicates from `Harbor.Application.Tests/Fakes/AgentLoopFakes.cs` and reference TestKit versions directly.

### 3.6 New fake infrastructure for TestKit

```csharp
namespace Harbor.TestKit;

// Event bus that records all events for assertions
public sealed class FakeEventBus : IEventBus
{
    public List<AgentEvent> Events { get; } = [];
    public List<T> EventsOfType<T>() where T : AgentEvent => Events.OfType<T>().ToList();
    // ... (move from Application.Tests.Fakes)
}

// Token tracker with configurable behavior
public sealed class FakeTokenTracker(bool shouldCompact = false) : ITokenTracker { ... }

// Compaction service with configurable outcome
public sealed class FakeCompactionService : ICompactionService { ... }

// System prompt builder returning a constant
public sealed class StubSystemPromptBuilder : ISystemPromptBuilder { ... }
```

**Replaces:** Local definitions in `Harbor.Application.Tests/Fakes/AgentLoopFakes.cs`.

### 3.7 Session store fakes

```csharp
namespace Harbor.TestKit;

// In-memory store with gating for concurrency tests
public sealed class FakeSessionStore(Session session) : ISessionStore { ... }

// Multi-session store for cross-session tests
public sealed class MultiSessionStore(params Session[] sessions) : ISessionStore { ... }

// Store that always fails on append
public sealed class FailingAppendStore(Session session) : ISessionStore { ... }
```

**Replaces:** `FakeSessionStore` in `DefaultAgentFakes.cs`, `MultiSessionStore` in `SteeringCrossSessionTests.cs`, `FailingAppendStore` in `StorePersistFailureTests.cs`.

### 3.8 `Args()` helper consolidation

```csharp
namespace Harbor.TestKit;

public static class TestArgs
{
    // Universal: accepts any serializable object
    public static JsonElement From(object payload);
    
    // Convenience: single key-value pair
    public static JsonElement Pair(string key, string value);
    
    // Convenience: command string for bash tests
    public static JsonElement Command(string command);
}
```

**Replaces:** 3 different `Args()` overloads across 3 files.

### 3.9 `AgentLoopFactory` — eliminate 11-parameter wiring

```csharp
namespace Harbor.TestKit;

public sealed class AgentLoopFactory
{
    public AgentLoopFactory WithAgent(AgentDefinition agent);
    public AgentLoopFactory WithClient(ILlmClient client);
    public AgentLoopFactory WithTools(params ITool[] tools);
    public AgentLoopFactory WithTool(ITool tool);
    public AgentLoopFactory WithEventBus(IEventBus? bus = null);
    public AgentLoopFactory WithCompaction(ICompactionService? compaction = null);
    public AgentLoopFactory WithTokenTracker(ITokenTracker? tracker = null);
    public AgentLoopFactory WithPromptBuilder(ISystemPromptBuilder? builder = null);
    public AgentLoopFactory AllowAll();  // shortcut
    public AgentLoop Build();
}
```

**Replaces:** `CreateLoop()` helpers in 6+ files, each manually wiring 11 constructor args.

### 3.10 Common assertion helpers

```csharp
namespace Harbor.TestKit;

public static class EventAssertions
{
    public static async Task ShouldHaveEvent<T>(this IReadOnlyList<AgentEvent> events) where T : AgentEvent;
    public static async Task ShouldHaveCount<T>(this IReadOnlyList<AgentEvent> events, int count) where T : AgentEvent;
    public static async Task ShouldNotHaveEvent<T>(this IReadOnlyList<AgentEvent> events) where T : AgentEvent;
}

public static class AgentLoopAssertions
{
    public static async Task ShouldSucceed(this Result result);
    public static async Task ShouldFail(this Result result);
    public static async Task ShouldHaveMessagesOfType<T>(this ISessionContext ctx, int count) where T : AgentMessage;
}
```

**Replaces:** Inline `Assert.That(receivedEvents.Any(e => e is XxxEvent)).IsTrue()` patterns in 10+ files.

---

## 4. Estimated Impact

| Pattern | Files Affected | Lines Eliminated | Priority |
|---------|---------------|------------------|----------|
| AgentDefinition + PermissionRuleset boilerplate | 12+ | ~360 | **HIGH** |
| AgentLoopFactory (11-param wiring) | 8+ | ~120 | **HIGH** |
| Duplicate ScriptedLlmClient | 2 | ~120 | **HIGH** |
| Duplicate FakeAgentRegistry | 2 | ~40 | MEDIUM |
| Duplicate FakeToolRegistry | 2 | ~45 | MEDIUM |
| Duplicate CountingTool | 2 | ~50 | MEDIUM |
| Duplicate TestSessionContext | 3 | ~90 | MEDIUM |
| JsonElement Args() helpers | 3 | ~30 | LOW |
| Session path hardcoding | 15+ | ~40 | LOW |
| Specialized LLM clients (Silent/Hanging/Summary) | 3 | ~80 | MEDIUM |
| FakeEventBus, StubSystemPromptBuilder, FakeTokenTracker, FakeCompactionService | 6+ | ~80 | MEDIUM |
| Session store fakes (FakeSessionStore, MultiSessionStore, FailingAppendStore) | 4 | ~240 | MEDIUM |
| Provider stubs (StubAuthResolver, StubModelCatalog, StubHttpHandler) | 3+ | ~60 | LOW |
| Common assertion helpers | 10+ | ~60 | LOW |

### Projects that would benefit:

1. **Harbor.Application.Tests** (~30 files) — biggest beneficiary: AgentLoop, Permission, Session, SubAgent, Steering tests
2. **Harbor.Core.Tests** (~15 files) — AgentLoop, Permission, Compaction, EventBus tests
3. **Harbor.Providers.Tests** (~5 files) — StubAuthResolver, StubHttpHandler, StubModelCatalog
4. **Harbor.Ipc.Tests** (~5 files) — TestHost, StubAgent
5. **Harbor.Tools.Builtin.Tests** (~15 files) — ToolContext creation, Args helpers
6. **Harbor.App.Cli.Tests** (~3 files) — FakeToolRegistry, FakeAgentRegistry
7. **Harbor.Benchmarks** (1 file) — AgentDefinition, AgentLoop creation

### Total estimated impact:

- **~35 files** could eliminate local fake definitions
- **~1,100–1,400 lines** of duplicated boilerplate eliminated
- **Reduction in per-test file imports:** from ~10 `using Harbor.Application.Tests.Fakes` to 1 `using Harbor.TestKit`
- **Faster onboarding:** new test contributors only need to learn TestKit API, not hunt through 3 different Fakes directories

---

## 5. Recommended Implementation Order

1. **Phase 1 (highest ROI):** Consolidate duplicates already in TestKit → Application.Tests
   - Remove `AgentLoopFakes.cs` duplicate types, reference TestKit
   - Add `TestAgentBuilder`, `TestSessionFactory`, `AgentLoopFactory`
   - Add `MockLlmClient`, specialized LLM clients

2. **Phase 2:** Add session store fakes + assertion helpers
   - `FakeSessionStore`, `MultiSessionStore`, `FailingAppendStore`
   - `EventAssertions`, `AgentLoopAssertions`

3. **Phase 3:** Cross-project promotion
   - Move provider stubs to TestKit
   - Migrate Core.Tests local `MockLlmClient`, `TestSessionContext`, `CapturingStatsSession`

---

## 6. Notes on Constraints

- **No new dependencies:** TestKit currently depends only on `Harbor.Abstractions` + `CSharpFunctionalExtensions`. All proposed additions use only types from those assemblies plus BCL (`System.Text.Json`, `System.Threading.Channels`).
- **AOT compatibility:** No reflection emit, no `Activator.CreateInstance`. All types use primary constructors and pattern matching.
- **TUnit 1.61.0 compatible:** All assertion helpers return `Task` for async TUnit assertions.
- **NativeAOT-friendly:** No dynamic code generation, no `Assembly.Load`.
