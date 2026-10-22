using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using TestSessionContext = Harbor.TestKit.TestSessionContext;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

public class AgentLoopLifecycleTests
{
    private static TestSessionContext NewSession(params AgentMessage[] seedMessages) => new(
        Session.Create("/tmp/harbor-agentloop-lifecycle-tests", "code", "test", "test-model"),
        seedMessages);

    private static IReadOnlyList<AgentMessage> SeedHistory(string sessionId, int userAssistantPairs)
    {
        var messages = new List<AgentMessage>(userAssistantPairs * 2);
        for (int i = 0; i < userAssistantPairs; i++)
        {
            messages.Add(TestMessages.User($"question-{i}", sessionId: sessionId, agent: "user"));
            messages.Add(TestMessages.Assistant($"answer-{i}", sessionId: sessionId));
        }

        return messages;
    }

    [Test]
    public async Task RunAsync_CancelledBeforeFirstTurn_ReturnsFailure()
    {
        var client = new ScriptedLlmClient(
            [[TestEvents.TextDelta(delta: "never reached"), TestEvents.StepFinish(usage: new Usage(1, 1))]]);
        var loop = TestLoops.Create(client, new FakeToolRegistry(), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        using var cts = TestCancellation.Cancelled();

        var result = await loop.RunAsync(session, TestAgents.AllowAll(), cts.Token);

        await Assert.That(result).IsFailed();
    }

    [Test]
    public async Task RunAsync_StopReasonStopWithPendingToolCalls_ExecutesPendingToolCalls()
    {
        var counter = new TestKit.CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.ToolCallStart(),
                TestEvents.ToolCallDelta(argsDelta: """{"n":7}"""),
                TestEvents.StepFinish(usage: new Usage(4, 2))
            },
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "finished"),
                TestEvents.StepFinish(1, usage: new Usage(1, 1))
            }
        ]);
        var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result).IsSuccessful();
        await Assert.That(counter.Executions).IsEqualTo(1);
        await Assert.That(System.Linq.Enumerable.Count(session.Messages.OfType<ToolResultMessage>())).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_MalformedToolCallArgs_ReturnsErrorResultWithoutExecutingTool()
    {
        var counter = new TestKit.CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.ToolCallStart(),
                TestEvents.ToolCallDelta(argsDelta: "{ this is not json"),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
            },
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "recovered"),
                TestEvents.StepFinish(1, usage: new Usage(1, 1))
            }
        ]);
        var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result).IsSuccessful();
        await Assert.That(counter.Executions).IsEqualTo(0);
        var toolResults = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(toolResults.Count).IsEqualTo(1);
        await Assert.That(toolResults[0].Results.Single()).HasFailed();
    }

    [Test]
    public async Task RunAsync_SingleMalformedCall_PlaceholderArgsAreEmptyObject()
    {
        var counter = new TestKit.CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.ToolCallStart(),
                TestEvents.ToolCallDelta(argsDelta: "{ this is not json"),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
            },
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "recovered"),
                TestEvents.StepFinish(1, usage: new Usage(1, 1))
            }
        ]);
        var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result).IsSuccessful();
        await Assert.That(counter.Executions).IsEqualTo(0);
        var toolCalls = session.Messages.OfType<AssistantMessage>().SelectMany(m => m.Parts.OfType<ToolCallPart>()).ToList();
        await Assert.That(toolCalls.Count).IsEqualTo(1);
        await Assert.That(toolCalls[0].Id).IsEqualTo("call-1");
        await Assert.That(toolCalls[0].Args.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(toolCalls[0].Args.GetRawText()).IsEqualTo("{}");
        var toolResults = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(toolResults.Count).IsEqualTo(1);
        await Assert.That(toolResults[0].Results.Single()).HasFailed();
        await Assert.That(toolResults[0].Results.Single().ToolCallId).IsEqualTo("call-1");
    }

    [Test]
    public async Task RunAsync_TwoMalformedCalls_BothPlaceholdersAreEmptyObjects()
    {
        var counter = new TestKit.CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.ToolCallStart(),
                TestEvents.ToolCallDelta(argsDelta: "{ broken one"),
                TestEvents.ToolCallStart("call-2"),
                TestEvents.ToolCallDelta("call-2", "{ broken two"),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
            },
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "recovered"),
                TestEvents.StepFinish(1, usage: new Usage(1, 1))
            }
        ]);
        var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result).IsSuccessful();
        await Assert.That(counter.Executions).IsEqualTo(0);
        var toolCalls = session.Messages.OfType<AssistantMessage>().SelectMany(m => m.Parts.OfType<ToolCallPart>()).ToList();
        await Assert.That(toolCalls.Count).IsEqualTo(2);
        for (int i = 0; i < toolCalls.Count; i++)
        {
            await Assert.That(toolCalls[i].Args.ValueKind).IsEqualTo(JsonValueKind.Object);
            await Assert.That(toolCalls[i].Args.GetRawText()).IsEqualTo("{}");
        }
        var toolResults = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(toolResults.Count).IsEqualTo(1);
        await Assert.That(toolResults[0].Results.Count).IsEqualTo(2);
        for (int i = 0; i < toolResults[0].Results.Count; i++)
        {
            await Assert.That(toolResults[0].Results[i]).HasFailed();
        }
    }

    [Test]
    public async Task RunAsync_MixedValidAndMalformedCalls_ValidExecutesWithRealArgsMalformedGetsEmptyPlaceholder()
    {
        var counter = new TestKit.CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.ToolCallStart(),
                TestEvents.ToolCallDelta(argsDelta: """{"n":7}"""),
                TestEvents.ToolCallStart("call-2"),
                TestEvents.ToolCallDelta("call-2", "{ broken"),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
            },
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "recovered"),
                TestEvents.StepFinish(1, usage: new Usage(1, 1))
            }
        ]);
        var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result).IsSuccessful();
        await Assert.That(counter.Executions).IsEqualTo(1);
        await Assert.That(counter.ExecutedArgs.Single()).IsEqualTo("""{"n":7}""");
        var toolCalls = session.Messages.OfType<AssistantMessage>().SelectMany(m => m.Parts.OfType<ToolCallPart>()).ToList();
        await Assert.That(toolCalls.Count).IsEqualTo(2);
        var valid = toolCalls.Single(c => c.Id == "call-1");
        await Assert.That(valid.Args.GetRawText()).IsEqualTo("""{"n":7}""");
        var malformed = toolCalls.Single(c => c.Id == "call-2");
        await Assert.That(malformed.Args.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(malformed.Args.GetRawText()).IsEqualTo("{}");
        var toolResults = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(toolResults.Count).IsEqualTo(1);
        await Assert.That(toolResults[0].Results.Count).IsEqualTo(2);
        await Assert.That(toolResults[0].Results.Single(r => r.ToolCallId == "call-2")).HasFailed();
    }

    [Test]
    public async Task RunAsync_MalformedLargeArgs_ErrorTailIsFixedWindowIndependentOfPayloadSize()
    {
        // #1102 (one slice): the malformed-args diagnostic tail is a fixed
        // 200-char window of the accumulated args, independent of payload
        // size — the failure path copies the window, never the whole
        // builder. Relative gate: a 1 KB and an 8 KB payload both surface
        // exactly the last 200 chars, so the error output stays bounded
        // while the payload grows 8x.
        const int tailLength = 200;
        string[] payloads =
        [
            "{ broken " + new string('x', 1024),
            "{ broken " + new string('y', 8192),
        ];

        foreach (string payload in payloads)
        {
            var counter = new CountingTool();
            var client = new ScriptedLlmClient(
            [
                new LlmEvent[]
                {
                    TestEvents.ToolCallStart(),
                    TestEvents.ToolCallDelta(argsDelta: payload),
                    TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
                },
                new LlmEvent[]
                {
                    TestEvents.TextDelta(delta: "recovered"),
                    TestEvents.StepFinish(1, usage: new Usage(1, 1))
                }
            ]);
            var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
            var session = NewSession();

            var result = await loop.RunAsync(session, TestAgents.AllowAll());

            await Assert.That(result).IsSuccessful();
            await Assert.That(counter.Executions).IsEqualTo(0);
            var toolResults = session.Messages.OfType<ToolResultMessage>().ToList();
            await Assert.That(toolResults.Count).IsEqualTo(1);
            var entry = toolResults[0].Results.Single(r => r.ToolCallId == "call-1");
            await Assert.That(entry).HasFailed();
            string expectedTail = payload.Substring(payload.Length - tailLength);
            await Assert.That(entry.Output.EndsWith(expectedTail)).IsTrue();
            await Assert.That(entry.Output.Length).IsLessThan(payload.Length);

            Console.WriteLine($"coalescer-rawtail: payload={payload.Length} tail={tailLength} error-output={entry.Output.Length}");
        }
    }

    [Test]
    public async Task RunAsync_CompactionFails_FallsBackAndContinuesWithTruncatedContext()
    {
        var session = NewSession();
        var seed = SeedHistory(session.Session.Id, userAssistantPairs: 6);
        var context = new TestSessionContext(session.Session, seed);
        var client = new ScriptedLlmClient(
            [[TestEvents.TextDelta(delta: "ok"), TestEvents.StepFinish(usage: new Usage(1, 1))]]);
        var compaction = new FakeCompactionService();
        var loop = TestLoops.Create(
            client,
            new FakeToolRegistry(),
            new FakeTokenTracker(shouldCompact: true),
            compaction,
            new FakeEventBus());

        var result = await loop.RunAsync(context, TestAgents.AllowAll());

        await Assert.That(result).IsSuccessful();
        await Assert.That(compaction.Calls).IsEqualTo(1);
        await Assert.That(client.Requests.Count).IsEqualTo(1);
        await Assert.That(client.Requests[0].Messages.Count).IsLessThan(seed.Count);
    }

    [Test]
    public async Task RunAsync_SteeringMessagesQueued_AllDrainedAtTurnBoundary()
    {
        var counter = new TestKit.CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.ToolCallStart(),
                TestEvents.ToolCallDelta(argsDelta: """{"n":1}"""),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
            },
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "done"),
                TestEvents.StepFinish(1, usage: new Usage(1, 1))
            }
        ]);
        var loop = TestLoops.Create(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();
        session.EnqueueSteering([.. Enumerable.Range(0, 5).Select(i => (AgentMessage)TestMessages.User($"steer-{i}", sessionId: session.Session.Id, agent: "user"))]);

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result).IsSuccessful();
        var steeredContents = session.Messages.OfType<UserMessage>().Select(m => m.Content).ToList();
        for (int i = 0; i < 5; i++)
        {
            await Assert.That(steeredContents).Contains($"steer-{i}");
        }
    }
}

public class DefaultAgentConcurrencyTests
{
    [Test]
    public async Task PromptAsync_SecondCallWhileFirstIsInFlight_ReturnsFailure()
    {
        var session = Session.Create("/tmp/harbor-agentloop-lifecycle-tests", "code", "test", "test-model");
        var store = new Harbor.TestKit.FakeSessionStore(session);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GateNextAppend(gate);
        var fakeLoop = new Harbor.Application.Tests.Fakes.FakeAgentLoop();
        var agentDefinition = new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "Red-team concurrency harness agent",
            "test-model",
            "test",
            new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

        DefaultAgent? agent = null;
        try
        {
            agent = new DefaultAgent(store, fakeLoop, new FakeEventBus(), NullLogger<DefaultAgent>.Instance);
            agent.Initialize(session, agentDefinition);

            var first = agent.PromptAsync(TestMessages.User("first", sessionId: session.Id, agent: "user"));
            await Task.Yield();

            var second = await agent.PromptAsync(TestMessages.User("second", sessionId: session.Id, agent: "user"));

            await Assert.That(second).IsFailed();
            string error = second.Error;
            await Assert.That(
                    error.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("busy", StringComparison.OrdinalIgnoreCase))
                .IsTrue();

            gate.TrySetResult();
            var firstResult = await first;

            await Assert.That(firstResult).IsSuccessful();
            await Assert.That(fakeLoop.Runs).IsEqualTo(1);
        }
        finally
        {
            gate.TrySetResult();
            agent?.Dispose();
        }
    }
}
