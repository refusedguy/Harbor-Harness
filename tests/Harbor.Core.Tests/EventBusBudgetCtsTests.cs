using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     #249: the 1/N-subscriber publish path must not allocate a CTS per
///     publish when no handler needs cancellation. The budget CTS is rented
///     from a single-slot pool (linkage to a cancelable outer token is
///     emulated with a registration); delivery semantics are unchanged.
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
}
