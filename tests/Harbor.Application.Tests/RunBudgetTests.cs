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
///     B2.3 (#404): one budget read-model per run with four SEPARATED sources
///     (provider-reported / local-estimate / tariff / unknown) plus hard caps
///     that are honest about overshoot.
/// </summary>
/// <remarks>
///     <para>
///         Today attribution is local-estimate-only
///         (<c>CompactionService</c>/<c>HeuristicTokenEstimator</c> guess tokens
///         from text): cost is not attributable to a run at all, and nothing
///         enforces a spend cap. A single blended <c>cost</c> field would bake
///         the lie into the type — usage arrives AFTER the request that spent
///         it, parallel requests are in flight when the cap trips, cancellation
///         lags — so the sources stay four distinct members and the recorded
///         actual may exceed the cap, with the overshoot reason carried on the
///         outcome.
///     </para>
///     <para>
///         Cap hits reuse the #403/#1011 terminal shape (<c>RunStopReason</c>.
///         <c>LimitExceeded</c> + <c>RunLimitKind</c>): no new axis, new members
///         on the existing limit kind.
///     </para>
///     <para>
///         <b>Non-vacuity.</b>
///         <see cref="RunAsync_CappedRunThatFinished_LimitIsNull" /> pins the
///         terminal event to null on the normal path, so the cap assertions
///         cannot hold on a tree that stamps every ending.
///     </para>
/// </remarks>
public class RunBudgetTests
{
    private static AgentDefinition BudgetAgent(RunBudgetCaps budget, int maxSteps = 10) => new(
        AgentName.Create("code"),
        "Code",
        "budget accounting harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        MaxSteps: maxSteps,
        Budget: budget);

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-run-budget-tests", "code", "test", "test-model"));

    /// <summary>A provider that always emits a tool call with the given usage, so only a cap can end the run.</summary>
    private static ScriptedLlmClient ToolCallingClient(Usage usage) => new(
    [
        new LlmEvent[]
        {
            new ToolCallStartEvent("call-1", "counter"),
            new ToolCallDeltaEvent("call-1", """{"n":1}"""),
            new StepFinishEvent(0, "tool_use", usage)
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
    public async Task BudgetSource_HasFourMembersAndNoFallback()
    {
        string[] names = Enum.GetNames<BudgetSource>();
        await Assert.That(names.Length).IsEqualTo(4);
        await Assert.That(names.Contains(nameof(BudgetSource.ProviderReported))).IsTrue();
        await Assert.That(names.Contains(nameof(BudgetSource.LocalEstimate))).IsTrue();
        await Assert.That(names.Contains(nameof(BudgetSource.TariffCost))).IsTrue();
        await Assert.That(names.Contains(nameof(BudgetSource.Unknown))).IsTrue();
        string[] banned = ["Default", "Fallback", "Zero"];
        foreach (string bad in banned)
            await Assert.That(names.Contains(bad)).IsFalse();
    }

    [Test]
    public async Task AbsentUsage_SnapshotReportsUnknownRatherThanZero()
    {
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 1000));
        RunBudget budget = tracker.Snapshot();
        await Assert.That(budget.ReportedUsage).IsNull();
        await Assert.That(budget.TariffCostUsd).IsNull();
        await Assert.That(budget.TokenSource).IsEqualTo(BudgetSource.Unknown);
        await Assert.That(budget.CostSource).IsEqualTo(BudgetSource.Unknown);
    }

    [Test]
    public async Task KnownUsage_ExactTokensAndExactTariffCost()
    {
        var pricing = new Pricing(2m, 10m);
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxCostUsd: 0.005m));
        tracker.CompleteRequest(new Usage(1000, 500), pricing, 0);
        RunBudget budget = tracker.Snapshot();
        await Assert.That(budget.ReportedUsage!.InputTokens).IsEqualTo(1000);
        await Assert.That(budget.ReportedUsage!.OutputTokens).IsEqualTo(500);
        // 1000/1e6*2 + 500/1e6*10 = 0.002 + 0.005. Exact decimal, not approximate.
        await Assert.That(budget.TariffCostUsd).IsEqualTo((decimal?)0.007m);
        await Assert.That(budget.CostSource).IsEqualTo(BudgetSource.TariffCost);
        await Assert.That(tracker.CheckCap()).IsEqualTo(RunLimitKind.MaxCost);
    }

    [Test]
    public async Task TariffAndEstimate_AreSeparatelyNamedAndSeparatelyLabeled()
    {
        var pricing = new Pricing(2m, 10m);
        var tracker = new RunBudgetTracker(new RunBudgetCaps());
        tracker.CompleteRequest(new Usage(1000, 500), pricing, 0);
        tracker.CompleteRequest(null, pricing, 400);
        RunBudget budget = tracker.Snapshot();
        await Assert.That(budget.TariffCostUsd).IsEqualTo((decimal?)0.007m);
        // 400 estimate tokens priced at the output rate: 400/1e6*10. Never the billed figure.
        await Assert.That(budget.EstimatedCostUsd).IsEqualTo((decimal?)0.004m);
        await Assert.That(ReferenceEquals(budget.TariffCostUsd, budget.EstimatedCostUsd)).IsFalse();
        await Assert.That(typeof(RunBudget).GetProperty("TariffCostUsd") != typeof(RunBudget).GetProperty("EstimatedCostUsd")).IsTrue();
        await Assert.That(RunBudgetLabels.BilledCost).IsNotEqualTo(RunBudgetLabels.EstimatedCost);
        await Assert.That(RunBudgetLabels.BilledCost.ToLowerInvariant().Contains("estimat")).IsFalse();
    }

    [Test]
    public async Task InFlightBatch_RecordsSpendBeyondCapWithOvershootReason()
    {
        var pricing = new Pricing(2m, 10m);
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 2500));
        for (int i = 0; i < 4; i++)
            tracker.BeginRequest();
        var usage = new Usage(1000, 0);
        await Task.WhenAll(
            Task.Run(() => tracker.CompleteRequest(usage, pricing, 0)),
            Task.Run(() => tracker.CompleteRequest(usage, pricing, 0)),
            Task.Run(() => tracker.CompleteRequest(usage, pricing, 0)),
            Task.Run(() => tracker.CompleteRequest(usage, pricing, 0)));
        RunBudget budget = tracker.Snapshot();
        // The third completion crosses 2500 while one request is still in
        // flight, so the recorded 4000 EXCEEDS the cap — and says why.
        await Assert.That(budget.ReportedUsage!.InputTokens).IsEqualTo(4000);
        await Assert.That(budget.ReportedUsage!.InputTokens > 2500).IsTrue();
        await Assert.That(tracker.Overshoot).IsEqualTo(BudgetOvershootReason.InFlightRequests);
        await Assert.That(tracker.CheckCap()).IsEqualTo(RunLimitKind.MaxTokens);
    }

    [Test]
    public async Task StragglerAfterTrip_ReadsCancelLag()
    {
        var pricing = new Pricing(2m, 10m);
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 1500));
        tracker.CompleteRequest(new Usage(1000, 0), pricing, 0);
        tracker.CompleteRequest(new Usage(1000, 0), pricing, 0);
        // Sequential flow: usage arrives after the request that spent it.
        await Assert.That(tracker.Overshoot).IsEqualTo(BudgetOvershootReason.LateUsage);
        // Spend recorded after the trip is cancellation lag, not a second trip.
        tracker.CompleteRequest(new Usage(500, 0), pricing, 0);
        await Assert.That(tracker.Overshoot).IsEqualTo(BudgetOvershootReason.CancelLag);
    }

    [Test]
    public async Task RunAsync_TokenCapHit_TerminalEventNamesTheTokenLimit()
    {
        var counter = new CountingTool();
        // 1200 tokens/turn against a 1500 cap: the second turn trips it.
        var client = ToolCallingClient(new Usage(1000, 200));
        var bus = new FakeEventBus();
        var agent = BudgetAgent(new RunBudgetCaps(MaxTokens: 1500));
        var loop = Loop(client, counter, bus, agent);

        var result = await loop.RunAsync(NewSession(), agent);

        // A limit is terminal, not a failure (#403 doctrine: the WHY rides the event).
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.StreamCalls).IsEqualTo(2);
        // The tripping turn's tools already ran — the modeled overshoot at the
        // boundary, same placement as the MaxSteps check.
        await Assert.That(counter.Executions).IsEqualTo(2);
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsEqualTo(RunLimitKind.MaxTokens);
        await Assert.That(end.Cancelled).IsFalse();
        // Reuses the #1011 outcome: no new axis, the existing limit member.
        var outcome = RunOutcome.Reconstruct(RunId.New(), "s", Array.Empty<AgentMessage>(), limit: end.Limit);
        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.LimitExceeded);
        await Assert.That(outcome.Limit).IsEqualTo(RunLimitKind.MaxTokens);
    }

    [Test]
    public async Task RunAsync_OutputCapHit_StopsStreamingWithinOneDeltaOfSlack()
    {
        const int Deltas = 10;
        const int DeltaChars = 1000;
        const int CapBytes = 3000;
        var script = new LlmEvent[Deltas + 1];
        for (int i = 0; i < Deltas; i++)
            script[i] = new TextDeltaEvent($"t-{i}", new string('x', DeltaChars));
        script[Deltas] = new StepFinishEvent(0, "stop", null);
        var client = new ScriptedLlmClient([script]);
        var bus = new FakeEventBus();
        var agent = BudgetAgent(new RunBudgetCaps(MaxOutputBytes: CapBytes));
        var loop = Loop(client, new CountingTool(), bus, agent);

        var result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result.IsSuccess).IsTrue();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsEqualTo(RunLimitKind.MaxOutputBytes);
        // The cap is enforced on the delta path: the run stops streaming
        // instead of materializing the whole 10 000-char output first.
        var finished = bus.Events.OfType<MessageEndEvent>().Single();
        int totalChars = finished.Message.Parts.OfType<TextPart>().Sum(p => p.Text.Length);
        await Assert.That(totalChars <= CapBytes + DeltaChars).IsTrue();
        await Assert.That(totalChars < Deltas * DeltaChars).IsTrue();
    }

    [Test]
    public async Task RunAsync_CappedRunThatFinished_LimitIsNull()
    {
        // NON-VACUITY. Caps are set but never hit: the stamp below is produced
        // by the cap, not by the fact of ending.
        var counter = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":1}"""),
                new StepFinishEvent(0, "tool_use", new Usage(10, 10))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(10, 10))
            }
        ]);
        var bus = new FakeEventBus();
        var agent = BudgetAgent(new RunBudgetCaps(MaxTokens: 1_000_000, MaxCostUsd: 1000m, MaxOutputBytes: 1_000_000));
        var loop = Loop(client, counter, bus, agent);

        var result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result.IsSuccess).IsTrue();
        var end = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(end.Limit).IsNull();
        await Assert.That(end.Cancelled).IsFalse();
    }
}
