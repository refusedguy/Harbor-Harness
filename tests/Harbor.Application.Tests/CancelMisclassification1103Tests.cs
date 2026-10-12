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

        private sealed class NullDisposable : IDisposable
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
        // #1103 TurnRunner:234,244 — the post-stream persist rode the run token,
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
        // #1103 TurnRunner:116 — the TurnStart publish rode the run token, so a
        // cancel during publish threw OCE into the run and read as a provider
        // error. The boundary is a fact and rides None; the cancel is still
        // honored at the next await.
        var client = new ScriptedLlmClient([
            new TextDeltaEvent("t", "never reached"), new StepFinishEvent(0, "stop", new Usage(1, 1))]);
        var bus = new CancelHonoringBus();
        var agent = TestAgents.AllowAll();
        var loop = LoopWithBus(client, bus, agent);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await loop.RunAsync(NewSession(), agent, cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(bus.Events.OfType<TurnStartEvent>().Count()).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Cancelled).IsTrue();
    }

    /// <summary>Probe tool: records whether the gate ever let it execute.</summary>
    private sealed class GateProbeTool : ITool
    {
        public bool Executed;

        public ToolName Name => ToolName.Create("gateprobe");

        /// <inheritdoc />
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "GateProbe";
        public string Description => "Probe.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");
        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
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
}
