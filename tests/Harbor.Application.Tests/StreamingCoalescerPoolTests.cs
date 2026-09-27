using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     Regression test (via the public <c>AgentLoop</c> seam) for the
///     pooled-buffer ownership fix audited in #53: a repeated tool-call start
///     for the same id must return the previously rented args builder to the
///     pool (no leak) and keep last-start-wins semantics.
///     (Internal <c>StreamingCoalescer</c> is exercised through
///     <c>ScriptedLlmClient</c>; no <c>InternalsVisibleTo</c> — exposing
///     internals would also leak the ZLinq drop-in extensions into this
///     project and break overload resolution in unrelated test files.)
/// </summary>
public class StreamingCoalescerPoolTests
{
    [Test]
    public async Task DuplicateToolCallStart_SameId_LastStartWins_ExecutesOnce()
    {
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":7}"""),
                // Repeated start for the same id: the old args builder goes
                // back to the pool, the latest args win.
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":8}"""),
                new StepFinishEvent(0, "stop", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(counter.Executions).IsEqualTo(1);
        await Assert.That(counter.ExecutedArgs.Count).IsEqualTo(1);
        await Assert.That(counter.ExecutedArgs[0]).Contains("\"n\":8");
    }

    [Test]
    public async Task LargeArgsAcrossManyDeltas_MultiChunkBuilder_MaterializesIdenticalArgs()
    {
        // #173: args larger than any single StringBuilder chunk force the
        // multi-chunk ToString() fallback in TryParseArgs. The materialized
        // args must be byte-identical to the concatenated deltas regardless
        // of which parse path was taken (chunk-direct vs fallback).
        string pad = new('x', 20_000);
        string argsJson = """{"n":7,"pad":"""" + pad + "\"}";
        var deltas = new List<LlmEvent> { new ToolCallStartEvent("call-1", "counter") };
        for (int i = 0; i < argsJson.Length; i += 100)
        {
            int len = Math.Min(100, argsJson.Length - i);
            deltas.Add(new ToolCallDeltaEvent("call-1", argsJson.Substring(i, len)));
        }

        deltas.Add(new StepFinishEvent(0, "stop", new Usage(4, 2)));
        var client = new ScriptedLlmClient(
        [
            deltas.ToArray(),
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var counter = new CountingTool();
        var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(counter.Executions).IsEqualTo(1);
        await Assert.That(counter.ExecutedArgs.Count).IsEqualTo(1);
        await Assert.That(counter.ExecutedArgs[0]).IsEqualTo(argsJson);
    }

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-coalescer-pool-tests", "code", "test", "test-model"),
        []);
}
