using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
/// Allocation-budget coverage for the agent-turn hot path (#186).
/// The internal <c>StreamingCoalescer</c> (incl. <c>TryParseArgs</c>) is
/// exercised through the public <c>AgentLoop</c> seam with a
/// <c>ScriptedLlmClient</c> — no <c>InternalsVisibleTo</c> (same approach as
/// <c>StreamingCoalescerPoolTests</c>). Each measured run uses a pre-built
/// loop + session so per-turn cost is isolated (no cross-run history growth
/// inside measurement). Bounds are generous, CI-safe tripwires in the
/// <c>SpanParserTests</c> tradition: per-turn averages are reported for
/// BENCHMARKS.md, hard failures only on pathological growth.
/// </summary>
public class AgentLoopAllocationTests
{
    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-alloc-loop-tests", "code", "test", "test-model"),
        []);

    private static AgentLoop NewTextOnlyLoop() => TestLoops.Create(
        new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "hi"),
                new StepFinishEvent(0, "stop", new Usage(1, 1)),
            },
        ]),
        new FakeToolRegistry());

    private static AgentLoop NewToolCallLoop() => TestLoops.Create(
        new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":7}"""),
                new StepFinishEvent(0, "stop", new Usage(4, 2)),
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(1, 1)),
            },
        ]),
        new FakeToolRegistry(new CountingTool()));

    [Test]
    public async Task TextOnly_Turn_StaysBounded()
    {
        var warm = await NewTextOnlyLoop().RunAsync(NewSession(), TestAgents.AllowAll());
        await Assert.That(warm.IsSuccess).IsTrue();

        const int turns = 10;
        var loops = new AgentLoop[turns];
        var sessions = new TestSessionContext[turns];
        for (int i = 0; i < turns; i++)
        {
            loops[i] = NewTextOnlyLoop();
            sessions[i] = NewSession();
        }

        var agent = TestAgents.AllowAll();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < turns; i++)
        {
            _ = await loops[i].RunAsync(sessions[i], agent);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"agentloop-alloc: text-only turn avg = {(double)allocated / turns:F0} B over {turns} turns");
        await Assert.That(allocated).IsLessThanOrEqualTo(turns * 64L * 1_024L);
    }

    [Test]
    public async Task ToolCall_Turn_StaysBounded()
    {
        // Exercises StreamingCoalescer start/delta/materialize (TryParseArgs)
        // plus permission check, tool dispatch and result append.
        var warm = await NewToolCallLoop().RunAsync(NewSession(), TestAgents.AllowAll());
        await Assert.That(warm.IsSuccess).IsTrue();

        const int runs = 5;
        var loops = new AgentLoop[runs];
        var sessions = new TestSessionContext[runs];
        for (int i = 0; i < runs; i++)
        {
            loops[i] = NewToolCallLoop();
            sessions[i] = NewSession();
        }

        var agent = TestAgents.AllowAll();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < runs; i++)
        {
            _ = await loops[i].RunAsync(sessions[i], agent);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"agentloop-alloc: tool-call turn avg = {(double)allocated / runs:F0} B over {runs} runs");
        await Assert.That(allocated).IsLessThanOrEqualTo(runs * 256L * 1_024L);
    }
}
