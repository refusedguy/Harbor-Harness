using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
using FakeSessionStore = Harbor.Application.Tests.Fakes.FakeSessionStore;

using FakeToolBase = Harbor.TestKit.Generated.FakeTool; // #1769: generated double

using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #1103: cancellation misclassified as error/timeout. One test per listed
///     path: a cancel at each point must produce no <see cref="AgentErrorEvent" />,
///     and a timeout must read as Timeout, never Cancelled.
/// </summary>
public class CancelMisclassification1103Tests
{
    /// <summary>
    ///     Bus that honors its token like the production bus: a publish with an
    ///     already-cancelled token throws instead of recording. The default
    ///     <see cref="FakeEventBus" /> ignores the token, which hides exactly
    ///     the defect these tests pin.
    /// </summary>
    private sealed class CancelHonoringBus : IEventBus
    {
        public List<AgentEvent> Events { get; } = [];

        public Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(@event);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) =>
            NullDisposable.Instance;

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
            where TEvent : AgentEvent =>
            NullDisposable.Instance;

        public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => [];

        internal sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-1103-cancel", "code", "test", "test-model"));

    private static AgentLoop LoopWithBus(ScriptedLlmClient client, IEventBus bus, AgentDefinition agent)
    {
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    [Test]
    public async Task RunAsync_TerminalProviderError_PublishesAgentEnd()
    {
        // #1103 AgentLoop:277 — the run failed without TurnEnd/AgentEnd, so the
        // turn card hung and the IPC projection never learned the run ended.
        var client = new ScriptedLlmClient([
            new ErrorEvent("bad key", Kind: ProviderErrorKind.Auth, StatusCode: 401)]);
        var bus = new FakeEventBus();
        var loop = TestLoops.Create(client, new FakeToolRegistry(), new FakeTokenTracker(), new FakeCompactionService(), bus);

        var result = await loop.RunAsync(NewSession(), TestAgents.AllowAll());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<AgentEndEvent>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_CancelDuringPublish_NoErrorEvent_CancelledEnd()
    {
        // #1103 AgentLoop catch — a cancel landing inside a turn (here: the
        // AgentStart publish against an already-cancelled token) surfaced as
        // AgentErrorEvent instead of a clean cancel.
        var client = new ScriptedLlmClient([
            new TextDeltaEvent("t", "never reached"), new StepFinishEvent(0, "stop", new Usage(1, 1))]);
        var bus = new CancelHonoringBus();
        var agent = TestAgents.AllowAll();
        var loop = LoopWithBus(client, bus, agent);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await loop.RunAsync(NewSession(), agent, cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("cancelled");
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Cancelled).IsTrue();
    }

    /// <summary>
    ///     Context that presses Stop as the first persist lands, then honors
    ///     the passed token like a real store. Replicates a cancel between
    ///     stream and persist.
    /// </summary>
    private sealed class CancelOnFirstAppendContext : ISessionContext
    {
        private readonly TestSessionContext _inner;
        private readonly CancellationTokenSource _cts;
        private int _appends;

        public CancelOnFirstAppendContext(Session session, CancellationTokenSource cts)
        {
            _inner = new TestSessionContext(session);
            _cts = cts;
        }

        public Session Session => _inner.Session;

        public IReadOnlyList<AgentMessage> Messages => _inner.Messages;

        public Channel<AgentMessage> SteeringQueue => _inner.SteeringQueue;

        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _appends) == 1)
                _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return _inner.AppendMessageAsync(message, ct);
        }

        public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return _inner.UpdateStatsAsync(usage, pricing, ct);
        }
    }

    [Test]
    public async Task RunAsync_CancelBetweenStreamAndPersist_PartialKept_NoErrorEvent()
    {
        // #1103 TurnRunner:242,252 — the post-stream persist rode the run token,
        // so a cancel between stream and persist threw OCE into the run and read
        // as a provider error (and the streamed output was lost). The persist
        // now rides None; the cancel still ends the run without tool execution.
        var client = new ScriptedLlmClient([
            new TextDeltaEvent("t", "hello"), new StepFinishEvent(0, "stop", new Usage(1, 1))]);
        var bus = new FakeEventBus();
        var agent = TestAgents.AllowAll();
        var loop = LoopWithBus(client, bus, agent);

        using var cts = new CancellationTokenSource();
        var session = new CancelOnFirstAppendContext(
            Session.Create("/tmp/harbor-1103-cancel", "code", "test", "test-model"), cts);

        var result = await loop.RunAsync(session, agent, cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
        // The already-streamed output survived the cancel.
        await Assert.That(session.Messages.OfType<AssistantMessage>().Count()).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<TurnEndEvent>().Count()).IsEqualTo(1);
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Cancelled).IsTrue();
    }

    [Test]
    public async Task RunAsync_CancelAtTurnStart_BoundaryRecorded_NoErrorEvent()
    {
        // #1103 TurnRunner:120 — the TurnStart publish rode the run token, so a
        // cancel landing on the boundary threw OCE into the run and the turn
        // card never recorded. The boundary is a fact and rides None; the
        // cancel is still honored at the next await, with no Error event.
        var client = new ScriptedLlmClient([
            new TextDeltaEvent("t", "hello after stop"), new StepFinishEvent(0, "stop", new Usage(1, 1))]);
        using var cts = new CancellationTokenSource();
        var bus = new CancelOnTurnStartBus(cts);
        var agent = TestAgents.AllowAll();
        var loop = LoopWithBus(client, bus, agent);

        var result = await loop.RunAsync(NewSession(), agent, cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(bus.Events.OfType<TurnStartEvent>().Count()).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Cancelled).IsTrue();
    }

    /// <summary>
    ///     Bus that presses Stop as the turn boundary lands, then honors the
    ///     passed token like the production bus. Replicates a cancel racing
    ///     the TurnStart publish.
    /// </summary>
    private sealed class CancelOnTurnStartBus(CancellationTokenSource cts) : IEventBus
    {
        public List<AgentEvent> Events { get; } = [];

        public Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
        {
            if (@event is TurnStartEvent)
                cts.Cancel();
            ct.ThrowIfCancellationRequested();
            Events.Add(@event);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) =>
            CancelHonoringBus.NullDisposable.Instance;

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
            where TEvent : AgentEvent =>
            CancelHonoringBus.NullDisposable.Instance;

        public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => [];
    }

    /// <summary>Probe tool: records whether the gate ever let it execute.</summary>
    private sealed class GateProbeTool : FakeToolBase
    {
        public bool Executed;

        public GateProbeTool()
        {
            Name = ToolName.Create("gateprobe");
            DisplayName = "GateProbe";
            Description = "Probe.";
        }

        public override Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
        {
            Executed = true;
            return Task.FromResult(ToolResult.Success("probe"));
        }
    }

    [Test]
    public async Task ExecuteAsync_TimeoutDuringPermissionGate_ReadsAsTimeout()
    {
        // #1103 ToolDispatcher: CheckPermissionAsync read the run token, not
        // the effective (deadline-linked) one: when the per-call deadline
        // fired while an asker that resolves instead of throwing was still
        // waiting, the Deny it returned read as a policy deny. A deadline hit
        // is a timeout — the loop keeps going — and never a cancel.
        var agent = new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "gate-timeout harness",
            "test-model",
            "test",
            new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Ask) }),
            MaxSteps: 10);
        var agents = new FakeAgentRegistry(agent);
        Task<PermissionResponse> HangingAsker(PermissionRequest request, CancellationToken ct) =>
            HangingDenyAsync(ct);

        var permissions = new PermissionService(
            agents, NullLogger<PermissionService>.Instance, HangingAsker);
        var tool = new GateProbeTool();
        var dispatcher = new ToolDispatcher(
            new FakeToolRegistry(tool),
            permissions,
            new FakeEventBus(),
            NullLogger<ToolDispatcher>.Instance);
        using var args = JsonDocument.Parse("""{}""");

        ToolResultMessage message = await dispatcher
            .ExecuteAsync(
                [new ToolCallPart("tc1", "gateprobe", args.RootElement)],
                NewSession(),
                AssistantMessage.Empty("s", "m"),
                agent,
                CancellationToken.None,
                toolExecutionTimeout: TimeSpan.FromMilliseconds(200))
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].Output).Contains("timed out");
        await Assert.That(message.Results[0].Output).DoesNotContain("Permission denied");
        await Assert.That(message.Results[0].Output).DoesNotContain("cancelled");
        await Assert.That(tool.Executed).IsFalse();
    }

    /// <summary>
    ///     Asker that waits forever but resolves (Deny) instead of throwing
    ///     when its token fires — the shape that misreported a deadline hit
    ///     as a policy deny.
    /// </summary>
    private static async Task<PermissionResponse> HangingDenyAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return new PermissionResponse(PermissionAction.Deny, false);
    }

    /// <summary>
    ///     Store that presses Stop as the sub-run's setup persist lands, then
    ///     honors the passed token like a real store. Replicates a cancel
    ///     racing the spawn.
    /// </summary>
    private sealed class CancelOnSubSpawnStore : ISessionStore
    {
        private readonly FakeSessionStore _inner;
        private readonly CancellationTokenSource _cts;
        private int _appends;

        public CancelOnSubSpawnStore(Session session, CancellationTokenSource cts)
        {
            _inner = new FakeSessionStore(session);
            _cts = cts;
        }

        public IReadOnlyList<Session> UpdatedSessions => _inner.UpdatedSessions;

        public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            _inner.CreateAsync(directory, agentName, providerId, modelId, ct);

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            _inner.GetAsync(sessionId, ct);

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) =>
            _inner.ListAsync(projectId, ct);

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _appends) == 1)
                _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return _inner.AppendMessageAsync(sessionId, message, ct);
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            _inner.UpdateMessageAsync(sessionId, message, ct);

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            _inner.GetMessagesAsync(sessionId, ct);

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            _inner.DeleteAsync(sessionId, ct);

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) =>
            _inner.UpdateAsync(session, ct);

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            _inner.GetStatsAsync(sessionId, ct);

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            _inner.UpdateStatsAsync(sessionId, metadata, ct);

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            _inner.DeleteMessagesAfterAsync(sessionId, messageId, ct);
    }

    [Test]
    public async Task RunAsync_CancelAtSubSpawnSetup_SessionAborted_NoOrphan()
    {
        // #1103 SubAgentRunner:165 — the setup persist rode the run token, so
        // a cancel racing the spawn threw OCE past MarkStatusAsync and left
        // the session Working forever. The setup now rides None; the cancel
        // still ends the run as Aborted with the resumable trailer.
        using var cts = new CancellationTokenSource();
        var store = new CancelOnSubSpawnStore(
            Session.Create("/tmp/harbor-1103-cancel", "explore", "test", "test-model"), cts);
        var runner = new SubAgentRunner(
            store,
            new FakeAgentLoop(Result.Failure("Sub-agent run was cancelled.")),
            NullLogger<SubAgentRunner>.Instance);
        var agent = new AgentDefinition(
            AgentName.Create("explore"),
            "explore",
            "explore",
            "test-model",
            "test",
            PermissionRuleset.Empty,
            20,
            IsSubAgent: true);

        var result = await runner.RunAsync(
            agent, new SubAgentRunRequest("find the todos", ParentSessionId: "parent-1"), cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("cancelled");
        await Assert.That(store.UpdatedSessions.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(store.UpdatedSessions[^1].Status).IsEqualTo(SessionStatus.Aborted);
    }
}
