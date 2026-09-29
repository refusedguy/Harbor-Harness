using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.Registries.Events;

namespace Harbor.Core.Tests;

/// <summary>
///     #249: the 1/N-subscriber publish path must not allocate a CTS per
///     publish when no handler needs cancellation. The budget CTS is rented
///     from a single-slot pool (linkage to a cancelable outer token is
///     emulated with a registration); delivery semantics are unchanged.
///     #391 follow-up: "not allocated per publish" has to mean the pooled
///     source is really handed out again — the budget timer is armed only for
///     handlers that outlive their synchronous part, so a completed fan-out
///     returns a pristine source to the pool.
/// </summary>
public class EventBusBudgetCtsTests
{
    private static InMemoryEventBus BusWithBudget(TimeSpan budget) => new(
        NullLogger<InMemoryEventBus>.Instance, maxScrollback: 0, handlerBudget: budget);

    [Test]
    public async Task Publish_ReusesBudgetTokenAcrossPublishes()
    {
        var bus = BusWithBudget(TimeSpan.FromMilliseconds(250));
        var seen = new List<CancellationToken>();
        bus.Subscribe((evt, ct) =>
        {
            seen.Add(ct);
            return ValueTask.CompletedTask;
        });

        var evt = new TurnStartEvent(1);
        await bus.PublishAsync(evt);
        await bus.PublishAsync(evt);

        await Assert.That(seen.Count).IsEqualTo(2);
        // Same pooled source behind both tokens — a fresh CTS per publish
        // would hand out distinct tokens.
        await Assert.That(seen[1]).IsEqualTo(seen[0]);
        await Assert.That(seen[0].IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Publish_WithCancelableOuterToken_ReusesBudgetToken_AndPropagatesCancellation()
    {
        var bus = BusWithBudget(TimeSpan.FromMilliseconds(250));
        var seen = new List<CancellationToken>();
        var cancelled = new List<bool>();
        bus.Subscribe((evt, ct) =>
        {
            seen.Add(ct);
            cancelled.Add(ct.IsCancellationRequested);
            return ValueTask.CompletedTask;
        });

        using var outer = new CancellationTokenSource();
        var evt = new TurnStartEvent(1);
        await bus.PublishAsync(evt, outer.Token);
        await bus.PublishAsync(evt, outer.Token);

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[1]).IsEqualTo(seen[0]);

        // The emulated link still propagates: a canceled outer token cancels
        // the rented budget source, exactly like the per-publish linked CTS did.
        outer.Cancel();
        await bus.PublishAsync(evt, outer.Token);

        await Assert.That(cancelled.Count).IsEqualTo(3);
        await Assert.That(cancelled[2]).IsTrue();
    }

    [Test]
    public async Task Publish_BudgetedFanOut_AllocatesNoMoreThanUnbudgeted()
    {
        const int publishes = 200;
        const long maxExtraPerPublishBytes = 128;

        var budgeted = BusWithBudget(TimeSpan.FromMilliseconds(250));
        budgeted.Subscribe((evt, ct) => ValueTask.CompletedTask);
        var unbudgeted = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance, maxScrollback: 0, handlerBudget: TimeSpan.Zero);
        unbudgeted.Subscribe((evt, ct) => ValueTask.CompletedTask);

        var evt = new TurnStartEvent(1);

        // Warm up (JIT, first CancelAfter timer, pool priming).
        for (int i = 0; i < 50; i++)
        {
            await budgeted.PublishAsync(evt);
            await unbudgeted.PublishAsync(evt);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long budgetedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < publishes; i++)
        {
            await budgeted.PublishAsync(evt);
        }

        long budgetedAlloc = GC.GetAllocatedBytesForCurrentThread() - budgetedBefore;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long unbudgetedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < publishes; i++)
        {
            await unbudgeted.PublishAsync(evt);
        }

        long unbudgetedAlloc = GC.GetAllocatedBytesForCurrentThread() - unbudgetedBefore;

        // A fresh CTS (+Timer via CancelAfter) per publish costs several
        // hundred bytes each — well above the slack. The pooled path adds ~0.
        await Assert.That(budgetedAlloc - unbudgetedAlloc).IsLessThan(publishes * maxExtraPerPublishBytes);
    }

    /// <summary>
    ///     #391 follow-up: the pooled budget source has to be handed out again
    ///     on every publish instead of being allocated per publish.
    ///     CancellationToken equality is source identity, so counting the
    ///     distinct tokens one handler sees counts the CancellationTokenSources
    ///     allocated behind N publishes: a recycled source shows up as 1, a
    ///     source-per-publish as N.
    /// </summary>
    [Test]
    public async Task Publish_AcrossManyPublishes_HandsOutOneRecycledBudgetSource()
    {
        const int publishes = 200;
        var bus = BusWithBudget(TimeSpan.FromMilliseconds(250));
        var seen = new CancellationToken[publishes];
        int deliveries = 0;
        bus.Subscribe((evt, ct) =>
        {
            seen[Interlocked.Increment(ref deliveries) - 1] = ct;
            return ValueTask.CompletedTask;
        });

        var evt = new TurnStartEvent(1);
        for (int i = 0; i < publishes; i++)
        {
            await bus.PublishAsync(evt);
        }

        await Assert.That(Volatile.Read(ref deliveries)).IsEqualTo(publishes);

        int distinctSources = 1;
        for (int i = 1; i < publishes; i++)
        {
            if (seen[i] != seen[i - 1])
            {
                distinctSources++;
            }
        }

        await Assert.That(distinctSources).IsEqualTo(1);
        await Assert.That(seen[publishes - 1].IsCancellationRequested).IsFalse();
    }

    /// <summary>
    ///     The byte-side proof of the same property: N publishes must not pay
    ///     for a fresh budget source each time. The ceiling is calibrated
    ///     against what one non-recycled source costs on this runtime (source +
    ///     its CancelAfter timer) rather than a hard-coded byte count, so the
    ///     bound tracks the runtime instead of pinning a number it cannot see.
    /// </summary>
    [Test]
    public async Task Publish_AcrossManyPublishes_AllocatesNoFreshBudgetSourcePerPublish()
    {
        const int publishes = 200;
        var budget = TimeSpan.FromMilliseconds(250);

        var budgeted = BusWithBudget(budget);
        budgeted.Subscribe((evt, ct) => ValueTask.CompletedTask);
        var unbudgeted = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance, maxScrollback: 0, handlerBudget: TimeSpan.Zero);
        unbudgeted.Subscribe((evt, ct) => ValueTask.CompletedTask);

        var evt = new TurnStartEvent(1);

        // Warm up (JIT, first CancelAfter timer, pool priming).
        for (int i = 0; i < 50; i++)
        {
            await budgeted.PublishAsync(evt);
            await unbudgeted.PublishAsync(evt);
        }

        long budgetedAlloc = await MeasurePublishBytesAsync(budgeted, evt, publishes);
        long unbudgetedAlloc = await MeasurePublishBytesAsync(unbudgeted, evt, publishes);
        long nonRecycledSourceBytes = NonRecycledSourceBytes(budget, publishes);

        // Once the source recycles, the budgeted fan-out allocates like the
        // unbudgeted one: renting is an Interlocked exchange plus a struct
        // token, and a handler that finished inline never needs the budget
        // timer.
        long extraPerPublish = (budgetedAlloc - unbudgetedAlloc) / publishes;
        await Assert.That(extraPerPublish).IsLessThan(nonRecycledSourceBytes / 2);
    }

    /// <summary>Bytes a publish loop allocates on the calling thread.</summary>
    private static async Task<long> MeasurePublishBytesAsync(InMemoryEventBus bus, AgentEvent evt, int publishes)
    {
        Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < publishes; i++)
        {
            await bus.PublishAsync(evt);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    ///     What a budget source that is <em>not</em> recycled costs per publish
    ///     on this runtime: the CancellationTokenSource plus the timer its
    ///     CancelAfter allocates. Measured in its own steady state (the timer
    ///     machinery is warmed first) and kept alive — a source the pool hands
    ///     back, or one disposed immediately, is not what a per-publish
    ///     allocation costs.
    /// </summary>
    private static long NonRecycledSourceBytes(TimeSpan budget, int iterations)
    {
        for (int i = 0; i < 20; i++)
        {
            var warm = new CancellationTokenSource();
            warm.CancelAfter(budget);
            warm.Dispose();
        }

        var sink = new CancellationTokenSource?[iterations];
        Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            var source = new CancellationTokenSource();
            source.CancelAfter(budget);
            sink[i] = source;
        }

        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(sink);
        return bytes / iterations;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
