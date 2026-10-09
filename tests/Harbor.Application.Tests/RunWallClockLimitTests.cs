using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #1024: a run that hits its WALL-CLOCK budget must end as a limit stop
///     (<see cref="RunStopReason.LimitExceeded" /> with
///     <see cref="RunLimitKind.Timeout" />), not as a user cancel.
/// </summary>
/// <remarks>
///     <para>
///         <b>What was wrong.</b> The only ready-made way to put a deadline on
///         the loop was the per-tool-call shape — a linked CTS plus
///         <c>CancelAfter</c> (<c>ToolDispatcher.cs:302-303</c>, driven by
///         <c>AgentDefinition.ToolTimeoutSeconds</c>). That shape FIRES the run
///         token, so <c>AgentLoop</c>'s <c>ct.IsCancellationRequested</c> branch
///         reports the run as <c>Result.Failure("Agent run was cancelled.")</c>
///         with <c>AgentEndEvent(Cancelled: true)</c> — a ceiling the user set,
///         reported as a stop the user pressed. The step budget (#1011) does not
///         have this problem: it never touches the token and rides
///         <c>TurnStepResult.Limit</c> → <c>AgentEndEvent.Limit</c> instead.
///         The wall-clock budget follows that shape, not the tool-timeout one.
///     </para>
///     <para>
///         <b>Determinism.</b> The clock is a <see cref="TimeProvider" /> seam
///         and the test advances it by whole deltas (one hour per tool call),
///         never by wall-clock sleeps — the verdict counts advances, not
///         milliseconds.
///     </para>
///     <para>
///         <b>Non-vacuity.</b>
///         <see cref="RunAsync_StepCapWithIdleClock_StillReportsMaxSteps" /> pins
///         the field to <c>MaxSteps</c> when the clock never moves, and
///         <see cref="RunAsync_CancelledRunWithTimeoutConfigured_LimitIsNull" />
///         pins it to null on the cancel path — so the timeout stamp is produced
///         by the budget and not by the fact of ending (#591).
///     </para>
/// </remarks>
public class RunWallClockLimitTests
{
    /// <summary>Controllable clock: the run's elapsed time counts advances, not wall-clock.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            NoopTimer.Instance;

        private sealed class NoopTimer : ITimer
        {
            public static readonly NoopTimer Instance = new();
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    /// <summary>Tool that burns one clock delta per execution, so the budget trip is exact.</summary>
    private sealed class ClockAdvancingTool(ManualTimeProvider clock, TimeSpan step) : ITool
    {
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public ToolName Name => ToolName.Create("clock");

        /// <inheritdoc />
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Clock";

        public string Description => "Advances the manual clock.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executions);
            clock.Advance(step);
            return Task.FromResult(ToolResult.Success("advanced"));
        }
    }

    private static AgentDefinition Agent(int maxSteps) => new(
        AgentName.Create("code"),
        "Code",
        "wall-clock limit harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        MaxSteps: maxSteps);

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-wall-clock-limit-tests", "code", "test", "test-model"));

    /// <summary>A provider that never stops asking for tools, so only a budget can end the run.</summary>
    private static ScriptedLlmClient AlwaysCallsATool() => new(
    [
        new LlmEvent[]
        {
            new ToolCallStartEvent("call-1", "clock"),
            new ToolCallDeltaEvent("call-1", """{"n":1}"""),
            new StepFinishEvent(0, "tool_use", new Usage(2, 1))
        }
    ]);

    private static AgentLoop Loop(
        ILlmClient client,
        ITool tool,
        FakeEventBus bus,
        AgentDefinition agent,
        ManualTimeProvider clock,
        TimeSpan? runTimeout)
    {
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(tool),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance,
            timeProvider: clock,
            runTimeout: runTimeout);
    }

    [Test]
    public async Task RunAsync_WallClockExpired_EndsAsTimeoutLimitNotCancel()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tool = new ClockAdvancingTool(clock, TimeSpan.FromHours(1));
        var client = AlwaysCallsATool();
        var bus = new FakeEventBus();
        var agent = Agent(maxSteps: 50);
        var loop = Loop(client, tool, bus, agent, clock, TimeSpan.FromMinutes(10));

        var result = await loop.RunAsync(NewSession(), agent);

        // `Result` here means "the loop reached a terminal event without a
        // provider/stream failure", NOT "the work was finished" — the same
        // deliberate split as the step budget (#1011): a limit reported as a
        // FAILURE would turn into `SessionStatus.Error` plus a "failed" note
        // in `SubAgentRunner`, i.e. a ceiling reported as a malfunction.
        await Assert.That(result.IsSuccess).IsTrue();

        // One clock delta burned on turn 1, the boundary check trips before
        // turn 2 — exact on any machine, no wall-clock tolerance to tune.
        await Assert.That(client.StreamCalls).IsEqualTo(1);
        await Assert.That(tool.Executions).IsEqualTo(1);

        // The terminal event names the limit and does NOT claim a cancel.
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsEqualTo(RunLimitKind.Timeout);
        await Assert.That(end.Cancelled).IsFalse();

        // No failure site inherited either: a limit is not a malfunction.
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_StepCapWithIdleClock_StillReportsMaxSteps()
    {
        // NON-VACUITY. The clock never moves, so the step cap — not the
        // timeout — must end the run, with its own kind. The two limits stay
        // distinguishable: only one of them tells the user to raise MaxSteps.
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":1}"""),
                new StepFinishEvent(0, "tool_use", new Usage(2, 1))
            }
        ]);
        var bus = new FakeEventBus();
        var agent = Agent(maxSteps: 3);
        var loop = Loop(client, counter, bus, agent, clock, TimeSpan.FromMinutes(10));

        var result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result.IsSuccess).IsTrue();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsEqualTo(RunLimitKind.MaxSteps);
        await Assert.That(end.Limit).IsNotEqualTo(RunLimitKind.Timeout);
        await Assert.That(end.Cancelled).IsFalse();
    }

    [Test]
    public async Task RunAsync_CancelledRunWithTimeoutConfigured_LimitIsNull()
    {
        // A user cancellation is not a limit even when a budget is configured.
        // The two are different facts and the terminal event must not merge
        // them — `cancelled` and `limit` stay mutually exclusive by
        // construction, exactly as for the step budget.
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var counter = new CountingTool();
        var client = AlwaysCallsATool();
        var bus = new FakeEventBus();
        var agent = Agent(maxSteps: 50);
        var loop = Loop(client, counter, bus, agent, clock, TimeSpan.FromMinutes(10));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await loop.RunAsync(NewSession(), agent, cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Cancelled).IsTrue();
        await Assert.That(end.Limit).IsNull();
    }
}
