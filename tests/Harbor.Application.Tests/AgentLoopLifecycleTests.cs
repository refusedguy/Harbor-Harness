using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.Core.Agents;
using Harbor.Core.Permissions;
using Harbor.Core.Resilience;
using Harbor.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

public class AgentLoopLifecycleTests
{
    private static AgentDefinition AllowAllAgent() => new(
        AgentName.Create("code"),
        "Code",
        "Red-team lifecycle harness agent",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static AgentLoop CreateLoop(
        ScriptedLlmClient client,
        FakeToolRegistry tools,
        ITokenTracker tracker,
        ICompactionService compaction,
        FakeEventBus bus,
        AgentDefinition? agent = null)
    {
        agent ??= AllowAllAgent();
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            tools,
            agents,
            new StubSystemPromptBuilder(),
            compaction,
            tracker,
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    private static TestSessionContext NewSession(params AgentMessage[] seedMessages) => new(
        Session.Create("/tmp/harbor-agentloop-lifecycle-tests", "code", "test", "test-model"),
        seedMessages);

    private static IReadOnlyList<AgentMessage> SeedHistory(string sessionId, int userAssistantPairs)
    {
        var messages = new List<AgentMessage>(userAssistantPairs * 2);
        for (int i = 0; i < userAssistantPairs; i++)
        {
            messages.Add(new UserMessage(
                Guid.NewGuid().ToString("N"),
                sessionId,
                DateTimeOffset.UtcNow,
                $"question-{i}",
                "user",
                "test-model"));
            messages.Add(new AssistantMessage(
                Guid.NewGuid().ToString("N"),
                sessionId,
                DateTimeOffset.UtcNow,
                new ContentPart[] { new TextPart($"answer-{i}") },
                StopReason.Stop,
                new Usage(0, 0),
                "test-model"));
        }

        return messages;
    }

    [Test]
    public async Task RunAsync_CancelledBeforeFirstTurn_ReturnsFailure()
    {
        var client = new ScriptedLlmClient(
            [[new TextDeltaEvent("t", "never reached"), new StepFinishEvent(0, "stop", new Usage(1, 1))]]);
        var loop = CreateLoop(client, new FakeToolRegistry(), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await loop.RunAsync(session, AllowAllAgent(), cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task RunAsync_StopReasonStopWithPendingToolCalls_ExecutesPendingToolCalls()
    {
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":7}"""),
                new StepFinishEvent(0, "stop", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var loop = CreateLoop(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, AllowAllAgent());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(counter.Executions).IsEqualTo(1);
        await Assert.That(session.Messages.OfType<ToolResultMessage>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_MalformedToolCallArgs_ReturnsErrorResultWithoutExecutingTool()
    {
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", "{ this is not json"),
                new StepFinishEvent(0, "tool_use", new Usage(2, 1))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "recovered"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var loop = CreateLoop(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();

        var result = await loop.RunAsync(session, AllowAllAgent());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(counter.Executions).IsEqualTo(0);
        var toolResults = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(toolResults.Count).IsEqualTo(1);
        await Assert.That(toolResults[0].Results.Single().IsError).IsTrue();
    }

    [Test]
    public async Task RunAsync_CompactionFails_FallsBackAndContinuesWithTruncatedContext()
    {
        var session = NewSession();
        var seed = SeedHistory(session.Session.Id, userAssistantPairs: 6);
        var context = new TestSessionContext(session.Session, seed);
        var client = new ScriptedLlmClient(
            [[new TextDeltaEvent("t", "ok"), new StepFinishEvent(0, "stop", new Usage(1, 1))]]);
        var compaction = new FakeCompactionService();
        var loop = CreateLoop(
            client,
            new FakeToolRegistry(),
            new FakeTokenTracker(shouldCompact: true),
            compaction,
            new FakeEventBus());

        var result = await loop.RunAsync(context, AllowAllAgent());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(compaction.Calls).IsEqualTo(1);
        await Assert.That(client.Requests.Count).IsEqualTo(1);
        await Assert.That(client.Requests[0].Messages.Count).IsLessThan(seed.Count);
    }

    [Test]
    public async Task RunAsync_SteeringMessagesQueued_AllDrainedAtTurnBoundary()
    {
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":1}"""),
                new StepFinishEvent(0, "tool_use", new Usage(2, 1))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "done"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var loop = CreateLoop(client, new FakeToolRegistry(counter), new FakeTokenTracker(), new FakeCompactionService(), new FakeEventBus());
        var session = NewSession();
        session.EnqueueSteering([.. Enumerable.Range(0, 5).Select(i => (AgentMessage)new UserMessage(
            Guid.NewGuid().ToString("N"),
            session.Session.Id,
            DateTimeOffset.UtcNow,
            $"steer-{i}",
            "user",
            "test-model"))]);

        var result = await loop.RunAsync(session, AllowAllAgent());

        await Assert.That(result.IsSuccess).IsTrue();
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
        var store = new FakeSessionStore(session);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GateNextAppend(gate);
        var fakeLoop = new FakeAgentLoop();
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

            var first = agent.PromptAsync(new UserMessage(
                Guid.NewGuid().ToString("N"), session.Id, DateTimeOffset.UtcNow, "first", "user", "test-model"));
            await Task.Yield();

            var second = await agent.PromptAsync(new UserMessage(
                Guid.NewGuid().ToString("N"), session.Id, DateTimeOffset.UtcNow, "second", "user", "test-model"));

            await Assert.That(second.IsFailure).IsTrue();
            string error = second.Error;
            await Assert.That(
                    error.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("busy", StringComparison.OrdinalIgnoreCase))
                .IsTrue();

            gate.TrySetResult();
            var firstResult = await first;

            await Assert.That(firstResult.IsSuccess).IsTrue();
            await Assert.That(fakeLoop.Runs).IsEqualTo(1);
        }
        finally
        {
            gate.TrySetResult();
            agent?.Dispose();
        }
    }
}
