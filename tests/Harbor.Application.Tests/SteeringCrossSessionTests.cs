using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Providers;
using Harbor.TestKit;
using TestSessionContext = Harbor.TestKit.TestSessionContext;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     G2 (deep2-core): one steering channel serves an agent across session
///     rebinds. A message authored for session A must never be drained into
///     session B's history (nor persisted under B's id).
/// </summary>
public class SteeringCrossSessionTests
{
    private static AgentDefinition AllowAllAgent() => new(
        AgentName.Create("code"),
        "Code",
        "cross-session steering harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static UserMessage SteerFor(string sessionId, string content) => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        content,
        "user",
        "test-model");

    [Test]
    public async Task Drain_ForeignSessionSteer_IsDroppedNotAppended()
    {
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":1}"""),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "done"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var agentDef = AllowAllAgent();
        var agents = new FakeAgentRegistry(agentDef);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(counter),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-steering-cross-session", "code", "test", "test-model"));
        session.EnqueueSteering(SteerFor("some-other-session", "foreign injection"));

        var result = await loop.RunAsync(session, agentDef);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(session.Messages.OfType<UserMessage>().Any(m => m.Content == "foreign injection")).IsFalse();
    }

    [Test]
    public async Task Drain_OwnSessionSteer_StillDelivered()
    {
        // Steering drains only at tool-result boundaries / turn ends reached
        // through the execution path — script one tool call to get there.
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":1}"""),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "done"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var agentDef = AllowAllAgent();
        var agents = new FakeAgentRegistry(agentDef);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(counter),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-steering-cross-session", "code", "test", "test-model"));

        // Authored for THIS session — must still flow through.
        session.EnqueueSteering(SteerFor(session.Session.Id, "own steer"));
        // Plus a foreign one that must be dropped.
        session.EnqueueSteering(SteerFor("another-session-again", "foreign"));

        var result = await loop.RunAsync(session, agentDef);

        await Assert.That(result.IsSuccess).IsTrue();
        var steered = session.Messages.OfType<UserMessage>().Select(m => m.Content).ToList();
        await Assert.That(steered).Contains("own steer");
        await Assert.That(steered).DoesNotContain("foreign");
    }

    [Test]
    public async Task Initialize_RebindCycle_StaleSteeringNeverSurfacesInHistory()
    {
        var sessionA = Session.Create("/tmp/harbor-steering-rebind-a", "code", "test", "test-model");
        var sessionB = Session.Create("/tmp/harbor-steering-rebind-b", "code", "test", "test-model");
        var store = new MultiSessionStore(sessionA, sessionB);
        var client = new ScriptedLlmClient(
            [[new TextDeltaEvent("t", "done"), new StepFinishEvent(0, "stop", new Usage(1, 1))]]);
        var agents = new FakeAgentRegistry(AllowAllAgent());
        var realLoop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
        var agent = new DefaultAgent(store, realLoop, new FakeEventBus(), NullLogger<DefaultAgent>.Instance);
        try
        {
            agent.Initialize(sessionA, AllowAllAgent());
            // Authored for A while A is NOT running → stranded in the channel
            // (F1's check-then-act window; Steer never throws).
            agent.Steer(SteerFor(sessionA.Id, "stale for A"));

            // Switch away (drops foreign/stale entries) and back to A.
            agent.Initialize(sessionB, AllowAllAgent());
            agent.Initialize(sessionA, AllowAllAgent());

            var run = await agent.PromptAsync("go");

            await Assert.That(run.IsSuccess).IsTrue();
            var contents = store.MessagesOf(sessionA.Id).OfType<UserMessage>().Select(m => m.Content).ToList();
            await Assert.That(contents).Contains("go");
            await Assert.That(contents).DoesNotContain("stale for A");
        }
        finally
        {
            agent.Dispose();
        }
    }
}

/// <summary>In-memory ISessionStore holding several sessions independently.</summary>
public sealed class MultiSessionStore(params Session[] sessions) : ISessionStore
{
    private readonly Dictionary<string, Session> _sessions = sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, List<AgentMessage>> _messages = [];

    public IReadOnlyList<AgentMessage> MessagesOf(string sessionId) =>
        _messages.TryGetValue(sessionId, out List<AgentMessage>? list) ? [.. list] : [];

    public Task<Result<Session>> CreateAsync(
        string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<Session>("CreateAsync is not part of this fake."));

    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(_sessions.TryGetValue(sessionId, out Session? session)
            ? Result.Success(session)
            : Result.Failure<Session>($"Session '{sessionId}' was not found."));

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Session>>([.. _sessions.Values]));

    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        if (!_messages.TryGetValue(sessionId, out List<AgentMessage>? list))
        {
            list = [];
            _messages[sessionId] = list;
        }

        list.Add(message);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>(MessagesOf(sessionId)));

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<int>("DeleteMessagesAfter is not supported by this test fake."));

    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(SessionMetadata.Empty));

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
        => Task.FromResult(Result.Success());
}
