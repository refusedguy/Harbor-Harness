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
using Harbor.TestKit;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
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

    private static ToolDescriptor Tool(string name, string schemaJson, params string[] guidelines) => new(
        ToolName.Create(name),
        name,
        $"{name} description",
        JsonDocument.Parse(schemaJson),
        ExecutionMode.Parallel,
        null,
        guidelines);

    /// <summary>
    ///     ONE working directory for every context this class builds. The key
    ///     covers <c>WorkingDirectory</c>, so a helper that minted a fresh temp
    ///     dir per call made each two-call test miss on that field alone — the
    ///     field the test names never had to move the key for the test to pass.
    ///     Five tests here were measuring the temp directory.
    /// </summary>
    private static readonly string WorkDir = TestTempDirs.NewDirectory("harbor-prompt-cache-tests");

    private static SystemPromptContext Context(params ToolDescriptor[] tools) => new(
        TestAgents.AllowAll(),
        TestModel,
        tools,
        Array.Empty<ContextFile>(),
        Array.Empty<SkillDescriptor>(),
        null,
        WorkDir);

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
        var otherAgent = TestAgents.AllowAll() with { Model = "other-model" };
        var otherContext = new SystemPromptContext(
            otherAgent,
            TestModel with { Id = "other-model" },
            Array.Empty<ToolDescriptor>(),
            Array.Empty<ContextFile>(),
            Array.Empty<SkillDescriptor>(),
            null,
            WorkDir);
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
        var agent = TestAgents.AllowAll();
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
        var session = new TestSessionContext(
            Session.Create(TestTempDirs.NewDirectory("harbor-prompt-cache-loop"), "code", "test", "test-model"));

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
        // The KEY is order-sensitive. This used to be justified by a comment
        // claiming the rendered prompt lists tools in context order — the
        // builder sorts them by name (SystemPromptBuilder.cs:139), so the
        // check-doc-cites: record-drift SystemPromptBuilder.cs:139 now="builder.AppendLine();" [#947: written over `// Header is "## Available Tools" per sp`; repair deferred to the owner's symbol-rename decision] -->
        // prompt is byte-identical across this reorder and the entry is an
        // over-invalidation, not a required miss. Harmless, and kept: the key
        // walking the list as given is the cheaper property to keep than the
        // sort that would make it order-blind. The reason is now the real one.
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

    // ── #792: a guideline is prompt content, so it belongs in the key ──

    [Test]
    public async Task BuildAsync_OnlyGuidelinesChanged_ServesRebuiltPrompt()
    {
        // The REAL builder, not the counting fake. The defect #792 reports is a
        // stale STRING — a guideline edit serving the previous turn's prompt —
        // and a fake that returns a constant "built" cannot show staleness at
        // all: the counter would have read 2 and the user would still have got
        // the old text. So this asserts on the strings the model would read.
        //
        // Everything else is held equal by construction, including the working
        // directory (see WorkDir) — otherwise this passes for the wrong reason.
        var caching = new CachingSystemPromptBuilder(new SystemPromptBuilder());

        string first = await caching.BuildAsync(
            Context(Tool("alpha", """{"type":"object"}""", "OLD guidance.")));
        string second = await caching.BuildAsync(
            Context(Tool("alpha", """{"type":"object"}""", "NEW guidance.")));

        await Assert.That(first).Contains("OLD guidance.");
        await Assert.That(second).Contains("NEW guidance.");
        await Assert.That(second).DoesNotContain("OLD guidance.");
    }

    [Test]
    public async Task BuildAsync_GuidelineCountChanged_RebuildsPrompt()
    {
        // Separate from the content case above: appending the guidelines as a
        // joined string would satisfy that one while keying every count the
        // same, and a single added guideline then changes the rendered prompt
        // without changing the key. The count has to ride in the key too.
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);

        _ = await caching.BuildAsync(Context(Tool("alpha", """{"type":"object"}""", "only one.")));
        _ = await caching.BuildAsync(
            Context(Tool("alpha", """{"type":"object"}""", "only one.", "and a second.")));

        await Assert.That(inner.BuildCalls).IsEqualTo(2);
    }

    // ── #815: the key holds the AGENT's provider; the prompt renders the MODEL's ──

    /// <summary>
    ///     The stale-string shape of #815, on the real builder.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both contexts are spelled out rather than routed through
    ///         <c>Context()</c> so the "everything else is the same object" claim is
    ///         visible rather than asserted: same agent — and therefore the same
    ///         <c>Agent.ProviderId</c>, the field the key <i>was</i> holding — same
    ///         <c>Model.Id</c>, same working directory, no tools. Only
    ///         <c>Model.ProviderId</c> moves.
    ///     </para>
    ///     <para>
    ///         <c>PromptCacheKeyCoverageRules</c> proves the same gap by reading the
    ///         two source files. This proves the consequence, which no source scan can
    ///         assert: that a colliding key really does hand the model the previous
    ///         prompt.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task BuildAsync_ModelProviderChanged_ServesRebuiltPrompt()
    {
        var caching = new CachingSystemPromptBuilder(new SystemPromptBuilder());
        var agent = TestAgents.AllowAll();

        string first = await caching.BuildAsync(new SystemPromptContext(
            agent,
            TestModel with { ProviderId = "first" },
            Array.Empty<ToolDescriptor>(),
            Array.Empty<ContextFile>(),
            Array.Empty<SkillDescriptor>(),
            null,
            WorkDir));

        string second = await caching.BuildAsync(new SystemPromptContext(
            agent,
            TestModel with { ProviderId = "second" },
            Array.Empty<ToolDescriptor>(),
            Array.Empty<ContextFile>(),
            Array.Empty<SkillDescriptor>(),
            null,
            WorkDir));

        await Assert.That(first).Contains("- Model: first/test-model")
            .Because("the environment section renders the MODEL's provider; this is the line #815 "
                   + "left uncovered");
        await Assert.That(second).Contains("- Model: second/test-model")
            .Because(
                "the two contexts differ only in Model.ProviderId, so a key that holds "
                + "Agent.ProviderId collides and the model reads the PREVIOUS prompt — a hit count, "
                + "no miss, no log line");
        await Assert.That(second).DoesNotContain("- Model: first/test-model")
            .Because("the previous environment line surviving into the next prompt IS the defect; "
                   + "asserting only that the new one is present would pass a prompt carrying both");
    }
}
