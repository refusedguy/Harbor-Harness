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
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     B2.2 (#403): a run that hits its step budget must SAY so on its terminal
///     event, and the run must not be reported as a finish.
/// </summary>
/// <remarks>
///     <para>
///         The step cap is the half of #403 that is deterministic, so it is the
///         half that can be checked exactly. A provider that always emits a tool
///         call produces an unbounded number of turns; the loop stops at
///         <c>MaxStepsBehavior.IsExhausted</c>, which is a comparison, not a
///         deadline, so the run ends at exactly the configured number of turns on
///         any machine.
///     </para>
///     <para>
///         <b>What was wrong.</b> <c>TurnStepResult.EndRun</c> is a
///         <see cref="bool" />: the turn that ran out of budget returned
///         <c>EndRun: true</c>, identically to the turn that finished the agent's
///         work, and the loop published a plain
///         <c>AgentEndEvent(Cancelled: false)</c> for both. Downstream that is
///         indistinguishable from a completed run — <c>ChatAppReducer.CoreEndedRun</c>
///         maps it to <c>SessionStatus.Done</c> — so a run cut off mid-work was
///         reported as a success. This is the #993 shape on the step axis: a
///         plausible value standing in for a fact nobody recorded.
///     </para>
///     <para>
///         <b>Non-vacuity.</b> The last two tests are the ones that keep the
///         first honest. A guard of the form "the terminal event carries a limit"
///         passes on any tree where <c>Limit</c> is a constant, so
///         <see cref="RunAsync_RunThatFinished_LimitIsNull" /> pins the field to
///         null on the normal path and
///         <see cref="RunAsync_CancelledRun_LimitIsNullAndNotStampedAsALimit" />
///         pins it to null on the cancel path. The stamp is therefore produced by
///         the cap and not by the fact of ending.
///     </para>
/// </remarks>
public class RunLimitStampingTests
{
    private const int MaxSteps = 3;

    private static AgentDefinition Agent(int maxSteps) => new(
        AgentName.Create("code"),
        "Code",
        "run-limit stamping harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        MaxSteps: maxSteps);

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-run-limit-stamping-tests", "code", "test", "test-model"));

    /// <summary>A provider that never stops asking for tools, so only the budget can end the run.</summary>
    private static ScriptedLlmClient AlwaysCallsATool() => new(
    [
        new LlmEvent[]
        {
            new ToolCallStartEvent("call-1", "counter"),
            new ToolCallDeltaEvent("call-1", """{"n":1}"""),
            new StepFinishEvent(0, "tool_use", new Usage(2, 1))
        }
    ]);

    private static AgentLoop Loop(ILlmClient client, ITool tool, FakeEventBus bus, AgentDefinition agent)
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
            NullLogger<AgentLoop>.Instance);
    }

    [Test]
    public async Task RunAsync_StepBudgetExhausted_TerminalEventNamesTheStepLimit()
    {
        var counter = new CountingTool();
        var client = AlwaysCallsATool();
        var bus = new FakeEventBus();
        var agent = Agent(MaxSteps);
        var loop = Loop(client, counter, bus, agent);

        var result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result.IsSuccess).IsTrue();

        // The budget is a comparison, so the run ends at exactly MaxSteps turns
        // — on any machine, with no tolerance to tune.
        await Assert.That(client.StreamCalls).IsEqualTo(MaxSteps);
        await Assert.That(counter.Executions).IsEqualTo(MaxSteps);

        // The terminal event says WHY. Before the fix `Limit` did not exist and
        // this run was indistinguishable from a finished one.
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsEqualTo(RunLimitKind.MaxSteps);
        await Assert.That(end.Cancelled).IsFalse();
    }

    [Test]
    public async Task RunAsync_RunThatFinished_LimitIsNull()
    {
        // NON-VACUITY. A run that did its work must not be stamped with a limit,
        // or the assertion above would hold on any tree that ends a run.
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
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var bus = new FakeEventBus();
        var agent = Agent(MaxSteps);
        var loop = Loop(client, counter, bus, agent);

        var result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result.IsSuccess).IsTrue();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsNull();
        await Assert.That(end.Cancelled).IsFalse();
    }

    [Test]
    public async Task RunAsync_CancelledRun_LimitIsNullAndNotStampedAsALimit()
    {
        // A user cancellation is not a limit. The two are different facts and
        // the terminal event must not merge them (#403, fourth requirement).
        var counter = new CountingTool();
        var client = AlwaysCallsATool();
        var bus = new FakeEventBus();
        var agent = Agent(MaxSteps);
        var loop = Loop(client, counter, bus, agent);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await loop.RunAsync(NewSession(), agent, cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Cancelled).IsTrue();
        await Assert.That(end.Limit).IsNull();
    }
}
