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
using FakeToolBase = Harbor.TestKit.Generated.FakeTool; // #1769: generated double
using TestSessionContext = Harbor.TestKit.TestSessionContext;
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
///         <c>CancelAfter</c> (<c>src/Harbor.AgentRuntime/Agents/ToolDispatcher.cs:655-656</c>, driven by
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
///     <para>
///         <b>Mid-stream (#403).</b> The deadline is a token linked to the
///         caller's, so an in-flight turn is interrupted rather than left to
///         run past the budget. <see cref="RunAsync_MidStreamTimeout_InterruptsToolViaTokenAndStampsLimit" />
///         proves the tool observes cancellation on that token and still gets
///         a limit; <see cref="RunAsync_MidStreamUserCancel_StaysCancelledAndNeverBecomesTimeout" />
///         proves the same interruption from the caller's token stays a
///         cancel. Both use a clock whose timers move only when the test
///         advances them.
///     </para>
/// </remarks>
public class RunWallClockLimitTests
{
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
    private static ScriptedLlmClient AlwaysCallsATool() => AlwaysCalls("clock");

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
        await Assert.That(result).IsSuccessful();

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
                TestEvents.ToolCallStart(),
                TestEvents.ToolCallDelta(argsDelta: """{"n":1}"""),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
            }
        ]);
        var bus = new FakeEventBus();
        var agent = Agent(maxSteps: 3);
        var loop = Loop(client, counter, bus, agent, clock, TimeSpan.FromMinutes(10));

        var result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result).IsSuccessful();
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

        using var cts = TestCancellation.Cancelled();

        var result = await loop.RunAsync(NewSession(), agent, cts.Token);

        await Assert.That(result).IsFailed();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Cancelled).IsTrue();
        await Assert.That(end.Limit).IsNull();
    }

    /// <summary>A provider that keeps asking for one named tool, so only a limit ends the run.</summary>
    private static ScriptedLlmClient AlwaysCalls(string toolName) => new(
    [
        new LlmEvent[]
        {
            TestEvents.ToolCallStart(toolName: toolName),
            TestEvents.ToolCallDelta(argsDelta: """{"n":1}"""),
            TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(2, 1))
        }
    ]);

    /// <summary>
    ///     A tool that blocks until the token handed to it is cancelled. The
    ///     point is the observation: a deadline that interrupted the run must
    ///     reach the in-flight work through the token, not through an exception
    ///     thrown by a timer on some other thread.
    /// </summary>
    private sealed class BlockingTool : FakeToolBase
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ObservedCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CompletedNormally { get; private set; }

        public BlockingTool()
        {
            Name = ToolName.Create("block");
            DisplayName = "Block";
            Description = "Blocks until its cancellation token fires.";
        }

        public override async Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation.TrySetResult();
                throw;
            }

            CompletedNormally = true;
            return ToolResult.Success("done");
        }
    }

    [Test]
    public async Task RunAsync_MidStreamTimeout_InterruptsToolViaTokenAndStampsLimit()
    {
        // The budget elapses while turn 1 is inside the tool. Advancing the
        // virtual clock fires the deadline on the injected TimeProvider, which
        // cancels the linked token the loop handed to the tool. No wall-clock
        // sleep and no elapsed-time tolerance: the assertion counts the
        // cancellation the tool observed.
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch, fireTimerOnAdvance: true);
        var tool = new BlockingTool();
        var client = AlwaysCalls("block");
        var bus = new FakeEventBus();
        var agent = Agent(maxSteps: 50);
        var loop = Loop(client, tool, bus, agent, clock, TimeSpan.FromMinutes(10));

        Task<Result> run = loop.RunAsync(NewSession(), agent);
        await tool.Started.Task;
        clock.Advance(TimeSpan.FromHours(1));
        // The wait is a hang guard for a broken implementation, not a timing
        // assertion: the verdict comes from the tool and the terminal event.
        Result result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await tool.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(tool.CompletedNormally).IsFalse();
        await Assert.That(result).IsSuccessful();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsEqualTo(RunLimitKind.Timeout);
        await Assert.That(end.Cancelled).IsFalse();
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
        // Cleanup: the deadline's timer is released on the limit exit.
        await Assert.That(clock.ActiveTimerCount).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_MidStreamUserCancel_StaysCancelledAndNeverBecomesTimeout()
    {
        // Same in-flight shape as the timeout test, but the caller's token is
        // the one that fires and the clock never moves. If the linked deadline
        // had merged the two causes, this run would come back as a limit.
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch, fireTimerOnAdvance: true);
        var tool = new BlockingTool();
        var client = AlwaysCalls("block");
        var bus = new FakeEventBus();
        var agent = Agent(maxSteps: 50);
        var loop = Loop(client, tool, bus, agent, clock, TimeSpan.FromMinutes(10));
        using var cts = new CancellationTokenSource();

        Task<Result> run = loop.RunAsync(NewSession(), agent, cts.Token);
        await tool.Started.Task;
        cts.Cancel();
        Result result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await tool.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result).IsFailed();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Cancelled).IsTrue();
        await Assert.That(end.Limit).IsNull();
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
        await Assert.That(clock.ActiveTimerCount).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_ClockMovesButStaysUnderBudget_StepCapStillWins()
    {
        // Both limits are armed and the clock does move. The step cap is still
        // the reason: three one-minute turns stay inside a ten-minute budget,
        // so the two first-class limits remain distinguishable when both are
        // configured and neither is idle.
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch, fireTimerOnAdvance: true);
        var tool = new ClockAdvancingTool(clock, TimeSpan.FromMinutes(1));
        var client = AlwaysCallsATool();
        var bus = new FakeEventBus();
        var agent = Agent(maxSteps: 3);
        var loop = Loop(client, tool, bus, agent, clock, TimeSpan.FromMinutes(10));

        var result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result).IsSuccessful();
        await Assert.That(client.StreamCalls).IsEqualTo(3);
        await Assert.That(tool.Executions).IsEqualTo(3);
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsEqualTo(RunLimitKind.MaxSteps);
        await Assert.That(end.Limit).IsNotEqualTo(RunLimitKind.Timeout);
        await Assert.That(end.Cancelled).IsFalse();
    }
}
