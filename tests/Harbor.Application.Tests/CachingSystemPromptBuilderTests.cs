using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Tests.Fakes;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     A2: <see cref="CachingSystemPromptBuilder" /> memoizes prompt builds
///     by a hash of every context component; the loop therefore calls the
///     inner builder once per distinct context instead of once per turn.
/// </summary>
public class CachingSystemPromptBuilderTests
{
    private static readonly ModelInfo TestModel =
        new("test-model", "test", "Test Model", 200_000, 4096, false, false, true, Pricing.Unknown, "openai");

    private static AgentDefinition AllowAllAgent() => new(
        AgentName.Create("code"),
        "Code",
        "Prompt-cache harness agent",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static ToolDescriptor Tool(string name, string schemaJson) => new(
        ToolName.Create(name),
        name,
        $"{name} description",
        JsonDocument.Parse(schemaJson),
        ExecutionMode.Parallel,
        null,
        Array.Empty<string>());

    private static SystemPromptContext Context(params ToolDescriptor[] tools) => new(
        AllowAllAgent(),
        TestModel,
        tools,
        Array.Empty<ContextFile>(),
        Array.Empty<SkillDescriptor>(),
        null,
        "/tmp/harbor-prompt-cache-tests");

    [Test]
    public async Task BuildAsync_SameContextTwice_InnerBuilderInvokedOnce()
    {
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);
        var context = Context();

        string first = await caching.BuildAsync(context);
        string second = await caching.BuildAsync(context);

        await Assert.That(first).IsEqualTo("built");
        await Assert.That(second).IsEqualTo("built");
        await Assert.That(inner.BuildCalls).IsEqualTo(1);
        await Assert.That(caching.CacheHits).IsEqualTo(1);
        await Assert.That(caching.Misses).IsEqualTo(1);
    }

    [Test]
    public async Task BuildAsync_ChangedToolSchema_RebuildsPrompt()
    {
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);

        _ = await caching.BuildAsync(Context(Tool("alpha", """{"type":"object"}""")));
        // Same tool NAME but different schema → the rendered tool list differs,
        // so the cache key must change and the inner builder must run again.
        _ = await caching.BuildAsync(Context(Tool("alpha", """{"type":"object","properties":{}}""")));

        await Assert.That(inner.BuildCalls).IsEqualTo(2);
    }

    [Test]
    public async Task BuildAsync_DifferentAgent_RebuildsPrompt()
    {
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);

        _ = await caching.BuildAsync(Context());
        var otherAgent = AllowAllAgent() with { Model = "other-model" };
        var otherContext = new SystemPromptContext(
            otherAgent,
            TestModel with { Id = "other-model" },
            Array.Empty<ToolDescriptor>(),
            Array.Empty<ContextFile>(),
            Array.Empty<SkillDescriptor>(),
            null,
            "/tmp/harbor-prompt-cache-tests");
        _ = await caching.BuildAsync(otherContext);

        await Assert.That(inner.BuildCalls).IsEqualTo(2);
    }

    [Test]
    public async Task RunAsync_TwoTurnRunWithSameTools_PromptBuiltOnce()
    {
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", "{}"),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var inner = new CountingPromptBuilder();
        var agent = AllowAllAgent();
        var agents = new FakeAgentRegistry(agent);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            inner,
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
        var session = new Fakes.TestSessionContext(
            Session.Create("/tmp/harbor-prompt-cache-loop-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, agent);

        await Assert.That(result.IsSuccess).IsTrue();
        // Two turns resolved an identical tool set → exactly ONE inner build.
        await Assert.That(inner.BuildCalls).IsEqualTo(1);
    }

    /// <summary>Inner builder that counts invocations and returns a constant.</summary>
    private sealed class CountingPromptBuilder : ISystemPromptBuilder
    {
        public int BuildCalls => Volatile.Read(ref _buildCalls);

        private int _buildCalls;

        public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _buildCalls);
            return Task.FromResult("built");
        }
    }
    // ── A10 (sprint 5): tool-list mutation invalidation ──

    [Test]
    public async Task BuildAsync_ToolAddedBetweenCalls_RebuildsPrompt()
    {
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);

        _ = await caching.BuildAsync(Context(Tool("alpha", """{"type":"object"}""")));
        _ = await caching.BuildAsync(Context(
            Tool("alpha", """{"type":"object"}"""),
            Tool("beta", """{"type":"object"}""")));

        // New tool → new rendered prompt → cache must miss.
        await Assert.That(inner.BuildCalls).IsEqualTo(2);
    }

    [Test]
    public async Task BuildAsync_ToolRemovedBetweenCalls_RebuildsPrompt()
    {
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);

        _ = await caching.BuildAsync(Context(
            Tool("alpha", """{"type":"object"}"""),
            Tool("beta", """{"type":"object"}""")));
        _ = await caching.BuildAsync(Context(Tool("alpha", """{"type":"object"}""")));

        await Assert.That(inner.BuildCalls).IsEqualTo(2);
    }

    [Test]
    public async Task BuildAsync_SameToolsDifferentOrder_TreatedAsChange()
    {
        // The key preserves order because the rendered system prompt lists
        // tools in order — a reorder changes the prompt text.
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);

        _ = await caching.BuildAsync(Context(
            Tool("alpha", """{"type":"object"}"""),
            Tool("beta", """{"type":"object"}""")));
        _ = await caching.BuildAsync(Context(
            Tool("beta", """{"type":"object"}"""),
            Tool("alpha", """{"type":"object"}""")));

        await Assert.That(inner.BuildCalls).IsEqualTo(2);
    }

}
