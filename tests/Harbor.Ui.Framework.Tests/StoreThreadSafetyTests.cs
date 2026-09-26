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
                    store.Dispatch(new UiMsg.AppendLine(ChatRole.User, $"w{writer}-l{i}"));
            });
        }

        await Task.WhenAll(tasks);

        // Every dispatch won exactly one CAS slot: revisions are dense 1..N.
        await Assert.That(store.State.Revision).IsEqualTo(Total);
        await Assert.That(store.State.Lines.Length).IsEqualTo(Total);
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
                    store.Dispatch(new UiMsg.AppendLine(ChatRole.User, $"w{writer}-l{i}"));
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

        store.Dispatch(new UiMsg.AppendLine(ChatRole.User, "hello"));

        await Assert.That(delivered).IsEqualTo(1);
        await Assert.That(store.State.Revision).IsEqualTo(1);
    }
}
