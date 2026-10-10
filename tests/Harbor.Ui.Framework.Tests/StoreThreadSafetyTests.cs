using System.Collections.Concurrent;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Regression tests for issue #81 (TUI cross-thread mutation,
///     <c>UiStore</c> notifications leg): concurrent dispatches from pool
///     threads must each apply exactly once through the CAS reducer, every
///     notification must carry a usable monotonic revision, and one failing
///     subscriber must never starve the rest.
///     Fully deterministic: bounded <c>Task.WhenAll</c> joins, no sleeps,
///     no timeouts, no shared global state (parallel-safe, no
///     <c>[NotInParallel]</c> needed).
/// </summary>
public class StoreThreadSafetyTests
{
    private const int Writers = 8;
    private const int PerWriter = 25;
    private const int Total = Writers * PerWriter;

    [Test]
    public async Task ConcurrentDispatch_AllTransitionsAppliedExactlyOnce()
    {
        var store = new UiStore();
        var revisions = new ConcurrentBag<long>();

        store.Changed += (_, e) => revisions.Add(e.Revision);

        var tasks = new Task[Writers];
        for (int w = 0; w < Writers; w++)
        {
            int writer = w;
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < PerWriter; i++)
                    store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, $"w{writer}-l{i}"));
            });
        }

        await Task.WhenAll(tasks);

        // Every dispatch won exactly one CAS slot: revisions are dense 1..N.
        await Assert.That(store.State.Revision).IsEqualTo(Total);
        await Assert.That(store.State.Chat.Lines.Length).IsEqualTo(Total);
        await Assert.That(revisions.Count).IsEqualTo(Total);
        await Assert.That(revisions.Max()).IsEqualTo(Total);
        await Assert.That(revisions.All(r => r is >= 1 and <= Total)).IsTrue();
    }

    [Test]
    public async Task ConcurrentDispatch_StaleNotificationsDroppableViaIsStale()
    {
        var store = new UiStore();
        var gate = new object();
        long lastApplied = 0;
        UiState? current = null;

        // Frame-loop style consumer: consume e.State, drop stale revisions.
        store.Changed += (_, e) =>
        {
            lock (gate)
            {
                if (e.IsStale(lastApplied))
                    return;
                lastApplied = e.Revision;
                current = e.State;
            }
        };

        var tasks = new Task[Writers];
        for (int w = 0; w < Writers; w++)
        {
            int writer = w;
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < PerWriter; i++)
                    store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, $"w{writer}-l{i}"));
            });
        }

        await Task.WhenAll(tasks);

        // The consumer never rewound: it settled on the terminal revision.
        long applied;
        UiState? settled;
        lock (gate)
        {
            applied = lastApplied;
            settled = current;
        }

        await Assert.That(applied).IsEqualTo(Total);
        await Assert.That(settled).IsNotNull();
        await Assert.That(settled!.Revision).IsEqualTo(Total);

        await Assert.That(store.State.Revision).IsEqualTo(Total);
    }

    [Test]
    public async Task FailingSubscriber_DoesNotStarveOthers()
    {
        var store = new UiStore();
        int delivered = 0;

        store.Changed += (_, _) => throw new InvalidOperationException("renderer blew up");
        store.Changed += (_, _) => Interlocked.Increment(ref delivered);

        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "hello"));

        await Assert.That(delivered).IsEqualTo(1);
        await Assert.That(store.State.Revision).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentSubscribeDuringDispatch_NoDeadlockNoTornDelivery()
    {
        // Control for the UiStore subscribe/dispatch asymmetry: Dispatch/Notify
        // never take _subscribersGate, so subscription churn must neither deadlock
        // dispatchers nor tear the delivery set (the stable subscriber observes
        // every dispatch exactly once). Bounded wait: a real deadlock must fail
        // the test, not hang the CI job.
        var store = new UiStore();
        int deliveredA = 0;
        int deliveredB = 0;
        EventHandler<UiStateChangedEventArgs> handlerA = (_, _) => Interlocked.Increment(ref deliveredA);
        EventHandler<UiStateChangedEventArgs> handlerB = (_, _) => Interlocked.Increment(ref deliveredB);
        store.Changed += handlerA;

        var dispatchers = new Task[Writers];
        for (int w = 0; w < Writers; w++)
        {
            int writer = w;
            dispatchers[w] = Task.Run(() =>
            {
                for (int i = 0; i < PerWriter; i++)
                    store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, $"w{writer}-l{i}"));
            });
        }

        var churn = Task.Run(() =>
        {
            for (int i = 0; i < Total; i++)
            {
                store.Changed += handlerB;
                store.Changed -= handlerB;
            }
        });

        var all = new Task[Writers + 1];
        Array.Copy(dispatchers, all, Writers);
        all[Writers] = churn;
        await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(30));

        // Every dispatch applied exactly once; the never-unsubscribed handler saw all.
        await Assert.That(store.State.Revision).IsEqualTo(Total);
        await Assert.That(store.State.Chat.Lines.Length).IsEqualTo(Total);
        await Assert.That(deliveredA).IsEqualTo(Total);
        await Assert.That(deliveredB).IsLessThanOrEqualTo(Total);
    }
}
