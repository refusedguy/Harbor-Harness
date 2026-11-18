using Harbor.Abstractions.Events;
using Harbor.TestKit;

namespace Harbor.Ipc.Tests;

/// <summary>
///     #2704 H05: two independent event contexts can share one deterministic
///     scheduler. A seeded interleaving decides which context publishes each
///     event first and how far the shared clock moves, yet both subscribers
///     observe the same logical event order. Replaying the seed reproduces the
///     interleaving and the clock exactly, so a collaboration-style test does
///     not depend on the machine's wall clock or on TUnit's scheduling order.
/// </summary>
public class DeterministicMultiContextTests
{
    private const int EventCount = 12;

    /// <summary>One isolated context: its own bus, its own recorded view of the stream.</summary>
    private sealed class Context
    {
        public FakeEventBus Bus { get; } = new();

        public List<string> Observed { get; } = [];

        public Context()
        {
            // Kept alive by the bus for the context's lifetime; no teardown is
            // needed because both the bus and the list die with this instance.
            _ = Bus.Subscribe((e, _) =>
            {
                Observed.Add(e.GetType().Name);
                return ValueTask.CompletedTask;
            });
        }

        public Task DeliverAsync(AgentEvent e) => Bus.PublishAsync(e);
    }

    private sealed record RunResult(
        int Seed,
        string FirstPublishers,
        string ClockAdvances,
        long AdvanceSum,
        long ElapsedMs,
        string Canonical,
        string ContextA,
        string ContextB);

    /// <summary>A well-formed agent run opened by AgentStart and closed by AgentEnd.</summary>
    private static AgentEvent At(int i) => i switch
    {
        0 => TestEvents.AgentStart(),
        _ when i == EventCount - 1 => TestEvents.AgentEnd(),
        _ when i % 2 == 0 => TestEvents.TurnStart(i / 2),
        _ => TestEvents.TurnEnd(),
    };

    private static async Task<RunResult> RunAsync(int seed)
    {
        var clock = new ManualTimeProvider();
        var a = new Context();
        var b = new Context();
        var rng = new Random(seed);

        var firstPublishers = new char[EventCount];
        var advances = new long[EventCount];
        var canonical = new List<string>(EventCount);
        long advanceSum = 0;

        for (int i = 0; i < EventCount; i++)
        {
            AgentEvent e = At(i);
            canonical.Add(e.GetType().Name);

            bool aFirst = rng.Next(2) == 0;
            long ms = rng.Next(1, 5);
            firstPublishers[i] = aFirst ? 'A' : 'B';
            advances[i] = ms;
            advanceSum += ms;

            Context primary = aFirst ? a : b;
            Context secondary = aFirst ? b : a;
            await primary.DeliverAsync(e);
            clock.AdvanceMs(ms);
            await secondary.DeliverAsync(e);
        }

        await a.Bus.WaitForDrainAsync();
        await b.Bus.WaitForDrainAsync();

        return new RunResult(
            seed,
            new string(firstPublishers),
            string.Join(",", advances),
            advanceSum,
            clock.ElapsedMilliseconds,
            string.Join(",", canonical),
            string.Join(",", a.Observed),
            string.Join(",", b.Observed));
    }

    [Test]
    public async Task TwoContexts_SharedScheduler_ObserveIdenticalEventOrder()
    {
        int seed = TestSeeds.Values(20261119)[0];
        RunResult r = await RunAsync(seed);

        await Assert.That(r.ContextA)
            .IsEqualTo(r.Canonical)
            .Because($"context A diverged from the published stream (seed {r.Seed})");
        await Assert.That(r.ContextB)
            .IsEqualTo(r.Canonical)
            .Because($"context B diverged from the published stream (seed {r.Seed})");
        await Assert.That(r.ContextB)
            .IsEqualTo(r.ContextA)
            .Because($"the two contexts saw different orders (seed {r.Seed})");
    }

    [Test]
    public async Task SameSeed_ReplaysInterleavingAndSharedClock()
    {
        int seed = TestSeeds.Values(424242)[0];
        RunResult first = await RunAsync(seed);
        RunResult replay = await RunAsync(seed);

        await Assert.That(replay.FirstPublishers)
            .IsEqualTo(first.FirstPublishers)
            .Because($"interleaving is not reproducible (seed {seed})");
        await Assert.That(replay.ClockAdvances)
            .IsEqualTo(first.ClockAdvances)
            .Because($"clock advances are not reproducible (seed {seed})");
        await Assert.That(first.ElapsedMs)
            .IsEqualTo(first.AdvanceSum)
            .Because("the shared clock must reflect only the scheduled advances");
        await Assert.That(replay.ElapsedMs)
            .IsEqualTo(first.ElapsedMs)
            .Because($"the shared clock is not reproducible (seed {seed})");
    }
}
