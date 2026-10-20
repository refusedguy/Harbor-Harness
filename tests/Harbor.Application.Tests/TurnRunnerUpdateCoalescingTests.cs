using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #1527: the turn runner published ONE <c>MessageUpdateEvent</c> per
///     streamed delta — one allocation plus one bus dispatch per token, on a
///     path whose consumers redraw at frame rate. The slice coalesces those
///     publishes into batched updates under an explicit
///     <see cref="UpdateFlushPolicy" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>THE INVARIANT THESE TESTS PIN.</b> A buffered delta is never
///         dropped, only merged: the concatenation of the published deltas is
///         byte-for-byte the concatenation of the received ones, in the same
///         order, and the last delta of a turn is published BEFORE the turn's
///         end event. Every consumer in the tree accumulates deltas, so a
///         merged delta renders exactly like the deltas it replaces.
///     </para>
///     <para>
///         The count assertions are the second half of the claim: the number
///         of published updates is a function of the POLICY, not of the number
///         of deltas. A regression that quietly published every delta again
///         would keep the text intact and still break the slice — the
///         <see cref="UpdateFlushPolicy.PerDelta" /> control below is that
///         regression, run on purpose.
///     </para>
///     <para>
///         The clock is injected (<see cref="TimeProvider" />) so the interval
///         bound is driven deterministically: a wall-clock assertion here would
///         be a flake waiting for a loaded runner.
///     </para>
/// </remarks>
[NotInParallel]
public class TurnRunnerUpdateCoalescingTests
{
    /// <summary>Deltas per streamed turn in the behaviour tests.</summary>
    private const int DeltaCount = 1000;

    /// <summary>
    ///     The policy the behaviour tests pin: a delta cap and no time bound,
    ///     so the published count is a pure function of the delta cap.
    /// </summary>
    private static readonly UpdateFlushPolicy BoundedPolicy = new(
        maxDeltas: 32,
        maxChars: 4096,
        maxInterval: null);

    /// <summary>
    ///     The scripted streams are built ONCE and replayed: the events are
    ///     immutable, and the provider's own allocation is not what the
    ///     publish path is measured on.
    /// </summary>
    private static readonly LlmEvent[] Script1000 = BuildTextScript(1000);

    private static readonly LlmEvent[] Script2000 = BuildTextScript(2000);

    // ── the invariant ────────────────────────────────────────────────────────

    [Test]
    public async Task RunAsync_TextDeltas_PublishesOneUpdatePerPolicyBatch()
    {
        var bus = new FakeEventBus();
        var loop = NewLoop(new ScriptedStreamClient(Script1000), bus, BoundedPolicy);

        Result result = await loop.RunAsync(NewSession(), Agent());

        await Assert.That(result.IsSuccess).IsTrue();

        List<MessageUpdateEvent> textUpdates = TextUpdates(bus);
        int expected = 1 + CeilDiv(DeltaCount - 1, BoundedPolicy.MaxDeltas);
        await Assert.That(textUpdates.Count).IsEqualTo(expected)
            .Because(
                $"the published count must be a function of the POLICY, not of the delta count: "
                + $"{DeltaCount} deltas at {BoundedPolicy.MaxDeltas} deltas per batch is "
                + "1 (the run's first delta, published alone so it never waits) + "
                + $"{CeilDiv(DeltaCount - 1, BoundedPolicy.MaxDeltas)} batches = {expected}.");

        await Assert.That(textUpdates.Count).IsLessThan(DeltaCount)
            .Because("the whole point of the slice: the bus sees batches, not tokens.");
    }

    [Test]
    public async Task RunAsync_TextDeltas_PublishesEveryDeltaInOrder()
    {
        var bus = new FakeEventBus();
        var loop = NewLoop(new ScriptedStreamClient(Script1000), bus, BoundedPolicy);

        Result result = await loop.RunAsync(NewSession(), Agent());

        await Assert.That(result.IsSuccess).IsTrue();

        string published = string.Concat(TextUpdates(bus).Select(DeltaText));
        await Assert.That(published).IsEqualTo(ExpectedText(DeltaCount))
            .Because(
                "the merge is lossless: the published deltas, concatenated in publication order, "
                + "are exactly the deltas that were streamed. A dropped tail or a reordered batch "
                + "shows up here as a string difference.");
    }

    [Test]
    public async Task RunAsync_LastDeltaIsPublishedBeforeTheTurnEnds()
    {
        var bus = new FakeEventBus();
        var loop = NewLoop(new ScriptedStreamClient(Script1000), bus, BoundedPolicy);

        Result result = await loop.RunAsync(NewSession(), Agent());

        await Assert.That(result.IsSuccess).IsTrue();

        List<AgentEvent> events = bus.Events;
        int lastUpdate = events.FindLastIndex(e => e is MessageUpdateEvent);
        int messageEnd = events.FindIndex(e => e is MessageEndEvent);

        await Assert.That(messageEnd).IsGreaterThanOrEqualTo(0)
            .Because("the turn must publish its end event — otherwise the ordering below is vacuous.");
        await Assert.That(lastUpdate).IsGreaterThanOrEqualTo(0)
            .Because("the turn must publish at least one update.");
        await Assert.That(lastUpdate).IsLessThan(messageEnd)
            .Because(
                "THE TAIL GUARANTEE: the last delta of a turn is published BEFORE the turn's end "
                + "event. An end event that overtook the text would close a message the user never "
                + "saw the end of.");
    }

    [Test]
    public async Task RunAsync_LastDeltaOfTheTurn_IsTheLastPublishedText()
    {
        var bus = new FakeEventBus();
        var loop = NewLoop(new ScriptedStreamClient(Script1000), bus, BoundedPolicy);

        Result result = await loop.RunAsync(NewSession(), Agent());

        await Assert.That(result.IsSuccess).IsTrue();

        List<MessageUpdateEvent> textUpdates = TextUpdates(bus);
        string lastBatch = DeltaText(textUpdates[^1]);
        await Assert.That(lastBatch).IsEqualTo(ExpectedText(DeltaCount)[^lastBatch.Length..])
            .Because(
                "the final published batch carries the final deltas: the tail is in the last "
                + "update, not stranded in a buffer that was never flushed.");
    }

    // ── the policy is honoured ──────────────────────────────────────────────

    [Test]
    public async Task RunAsync_PerDeltaPolicy_PublishesEveryDeltaOnItsOwn()
    {
        var bus = new FakeEventBus();
        var loop = NewLoop(new ScriptedStreamClient(Script1000), bus, UpdateFlushPolicy.PerDelta);

        Result result = await loop.RunAsync(NewSession(), Agent());

        await Assert.That(result.IsSuccess).IsTrue();

        await Assert.That(TextUpdates(bus).Count).IsEqualTo(DeltaCount)
            .Because(
                "the pre-#1527 behaviour, reachable on purpose: the policy is what decides how "
                + "many deltas share one update, so the escape hatch has to actually escape.");
    }

    [Test]
    public async Task RunAsync_SlowStream_PublishesEveryDeltaWithoutWaitingForABatch()
    {
        // The clock advances past the policy's interval between deltas — a
        // stream slow enough that a batch would never fill. The interval bound
        // is what keeps such a stream visible token by token.
        var clock = new ManualTimeProvider();
        var policy = new UpdateFlushPolicy(
            maxDeltas: 32,
            maxChars: 4096,
            maxInterval: TimeSpan.FromMilliseconds(100));
        var bus = new FakeEventBus();
        var loop = NewLoop(
            new ScriptedStreamClient(Script1000, clock, TimeSpan.FromMilliseconds(200)),
            bus,
            policy,
            clock);

        Result result = await loop.RunAsync(NewSession(), Agent());

        await Assert.That(result.IsSuccess).IsTrue();

        await Assert.That(TextUpdates(bus).Count).IsEqualTo(DeltaCount)
            .Because(
                "a delta that waited for a batch to fill would be invisible for as long as the "
                + "stream is slow; the interval bound publishes it as soon as the next delta "
                + "arrives.");
    }

    // ── ordering across a boundary ──────────────────────────────────────────

    [Test]
    public async Task RunAsync_KindChange_KeepsThePublishedOrder()
    {
        // text → thinking → text: each change of kind ends the run, so each
        // run's batch must go out before the next run starts.
        LlmEvent[] script =
        [
            new TextDeltaEvent("0", "first "),
            new ThinkingDeltaEvent("0", "hmm"),
            new TextDeltaEvent("0", "second"),
            new StepFinishEvent(0, "stop", new Usage(1, 1)),
        ];

        var bus = new FakeEventBus();
        var loop = NewLoop(new ScriptedStreamClient(script), bus, BoundedPolicy);

        Result result = await loop.RunAsync(NewSession(), Agent());

        await Assert.That(result.IsSuccess).IsTrue();

        List<AgentEvent> events = bus.Events;
        int thinking = events.FindIndex(e => e is MessageUpdateEvent { LlmEvent: ThinkingDeltaEvent });
        int firstText = events.FindIndex(e => e is MessageUpdateEvent { LlmEvent: TextDeltaEvent });
        int lastText = events.FindLastIndex(e => e is MessageUpdateEvent { LlmEvent: TextDeltaEvent });

        await Assert.That(thinking).IsGreaterThanOrEqualTo(0)
            .Because("the scripted turn must publish the thinking delta it streamed.");
        await Assert.That(firstText).IsGreaterThanOrEqualTo(0)
            .Because("the scripted turn must publish the text it streamed.");
        await Assert.That(firstText).IsLessThan(thinking)
            .Because("the text run's batch goes out before the thinking run starts.");
        await Assert.That(thinking).IsLessThan(lastText)
            .Because("the thinking run's batch goes out before the next text run starts.");

        await Assert.That(string.Concat(TextUpdates(bus).Select(DeltaText))).IsEqualTo("first second")
            .Because("the text on both sides of the kind change is published, in order, with nothing lost.");
    }

    // ── the measurement (CI) ────────────────────────────────────────────────

    /// <summary>
    ///     The relative gate: the publish path's cost must stay LINEAR in the
    ///     number of deltas. A merge that re-copied the accumulated text per
    ///     delta (the obvious way to get this wrong) is O(N²) and lands at 4x
    ///     the bound; the shipped merge is O(N).
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task PublishCost_DoubledDeltas_StaysWithinGrowthLimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // GC accounting varies across OS runtimes.
        }

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => MeasureLegAsync(BoundedPolicy, Script1000, units),
            measureLargeLeg: units => MeasureLegAsync(BoundedPolicy, Script2000, units),
            initialUnits: 2,
            options: new TestScalingGateOptions
            {
                Label = "turnrunner-update-coalescing-scaling",
                GrowthLimit = 3.0,
            });

        await result.AssertWithinLimitsAsync(
            topic: "publish(2N)/publish(N)",
            linearWhy: "Linear is 2.00, so this bound separates a linear merge from a quadratic one without depending on how fast this runner is.");
    }

    /// <summary>
    ///     The before/after measurement the slice is about: the same deltas
    ///     published per delta (the pre-#1527 path) and published in batches.
    ///     The allocation half is the runner-independent claim and is asserted;
    ///     the time half is printed for the benchmark table.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task PublishCost_BatchedPublishes_AllocateLessThanPerDeltaPublishes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // GC accounting varies across OS runtimes.
        }

        const int Units = 20;

        (double PerDeltaMs, long PerDeltaBytes) = await MeasureAsync(UpdateFlushPolicy.PerDelta, Script2000, Units);
        (double BatchedMs, long BatchedBytes) = await MeasureAsync(BoundedPolicy, Script2000, Units);

        Console.WriteLine(
            $"[coalescing] {Script2000.Length - 1} deltas x {Units} units — per-delta: {PerDeltaMs:F3} ms / {PerDeltaBytes} B; "
            + $"batched: {BatchedMs:F3} ms / {BatchedBytes} B; "
            + $"alloc x{(double)PerDeltaBytes / Math.Max(1, BatchedBytes):F2}");

        await Assert.That(BatchedBytes).IsLessThan(PerDeltaBytes)
            .Because(
                $"batching the deltas must allocate less than publishing them one by one "
                + $"({BatchedBytes} B vs {PerDeltaBytes} B): the batched path replaces one event "
                + "allocation plus one bus dispatch per delta with one per batch.");
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> turns of the scripted
    ///     stream, timed and allocated as one window (#741: the body awaits, so
    ///     the process-wide counter is the honest one).
    /// </summary>
    private static async Task<TestScalingGateLeg> MeasureLegAsync(UpdateFlushPolicy policy, LlmEvent[] script, int units)
    {
        var sw = Stopwatch.StartNew();
        long allocated = await AllocationProbe.MeasureProcessAsync(() => RunTurnsAsync(policy, script, units)).ConfigureAwait(false);
        sw.Stop();
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>The turns of one measured leg, one after another.</summary>
    private static async Task RunTurnsAsync(UpdateFlushPolicy policy, LlmEvent[] script, int units)
    {
        for (int i = 0; i < units; i++)
        {
            await RunTurnAsync(policy, script).ConfigureAwait(false);
        }
    }

    /// <summary>The before/after measurement helper (time + bytes, one window).</summary>
    private static async Task<(double Ms, long Bytes)> MeasureAsync(UpdateFlushPolicy policy, LlmEvent[] script, int units)
    {
        var sw = Stopwatch.StartNew();
        long allocated = await AllocationProbe.MeasureProcessAsync(async () =>
        {
            for (int i = 0; i < units; i++)
            {
                await RunTurnAsync(policy, script).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>One text-only turn of the scripted stream.</summary>
    private static async Task RunTurnAsync(UpdateFlushPolicy policy, LlmEvent[] script)
    {
        var bus = new FakeEventBus();
        var loop = NewLoop(new ScriptedStreamClient(script), bus, policy);
        Result result = await loop.RunAsync(NewSession(), Agent()).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"the measured run failed: {result.Error}");
        }
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    private static AgentDefinition Agent() => TestAgents.AllowAll();

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-update-coalescing-tests", "code", "test", "test-model"));

    private static AgentLoop NewLoop(
        ILlmClient client,
        FakeEventBus bus,
        UpdateFlushPolicy policy,
        TimeProvider? clock = null)
    {
        var agents = new FakeAgentRegistry(Agent());
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
            NullLogger<AgentLoop>.Instance,
            timeProvider: clock,
            updateFlushPolicy: policy);
    }

    /// <summary>The scripted text stream: <paramref name="deltas" /> deltas and a step finish.</summary>
    private static LlmEvent[] BuildTextScript(int deltas)
    {
        var script = new LlmEvent[deltas + 1];
        for (int i = 0; i < deltas; i++)
        {
            script[i] = new TextDeltaEvent("0", $"t{i:D6}");
        }

        script[deltas] = new StepFinishEvent(0, "stop", new Usage(1, 1));
        return script;
    }

    private static string ExpectedText(int deltas)
    {
        var sb = new StringBuilder(deltas * 7);
        for (int i = 0; i < deltas; i++)
        {
            sb.Append($"t{i:D6}");
        }

        return sb.ToString();
    }

    private static List<MessageUpdateEvent> TextUpdates(FakeEventBus bus) =>
        [.. bus.Events.OfType<MessageUpdateEvent>().Where(u => u.LlmEvent is TextDeltaEvent)];

    private static string DeltaText(MessageUpdateEvent update) => ((TextDeltaEvent)update.LlmEvent).Delta;

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    /// <summary>
    ///     A client that replays a fixed script. The events are immutable, so
    ///     the same instances are replayed for every turn — the provider's
    ///     allocation stays outside the measured window.
    /// </summary>
    private sealed class ScriptedStreamClient(
        IReadOnlyList<LlmEvent> script,
        ManualTimeProvider? clock = null,
        TimeSpan? step = null) : ILlmClient
    {
        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (LlmEvent evt in script)
            {
                if (clock is not null && step is { } delta)
                {
                    clock.Advance(delta);
                }

                yield return evt;

                // A completed task, not Task.Yield: the turn's cost is the
                // publish path, not a thread hop per delta.
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([ScriptedLlmClient.TestModel]));
    }

    /// <summary>
    ///     A clock the test drives by hand — the only way to make the
    ///     interval bound deterministic instead of wall-clock dependent.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan amount) => _ticks += amount.Ticks;
    }
}
