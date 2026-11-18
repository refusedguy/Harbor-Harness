using System.Collections.Concurrent;
using Harbor.Ui.Framework.Services;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #2550: SessionStatusTracker is a singleton whose message-count map is
///     written from the UI frame and from store notifications on pool threads.
///     A plain Dictionary corrupts under that fan-out; the tracker must keep
///     publishing counts after a parallel burst without throwing.
/// </summary>
public class SessionStatusTrackerConcurrencyTests
{
    private const int Writers = 8;
    private const int PerWriter = 25;
    private const int Total = Writers * PerWriter;

    [Test]
    public async Task NotifyMessageCount_ParallelWrites_PublishesCountsWithoutThrowing()
    {
        var tracker = new SessionStatusTracker();
        var published = new ConcurrentBag<int>();
        tracker.MessageCountChanged += (_, count) => published.Add(count);

        var tasks = new Task[Writers];
        for (int w = 0; w < Writers; w++)
        {
            int writer = w;
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < PerWriter; i++)
                    tracker.NotifyMessageCount("session", writer * PerWriter + i);
            });
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        // The parallel writes alone cannot pin an exact event count: dedup
        // races are benign. A final write after the storm is always observed,
        // and every published value must be one this test actually wrote.
        tracker.NotifyMessageCount("session", Total);
        await Assert.That(published.Any(c => c == Total)).IsTrue();
        await Assert.That(published.All(c => c is >= 0 and <= Total)).IsTrue();
    }

    [Test]
    public async Task NotifyMessageCount_UnchangedCount_DoesNotRaiseTwice()
    {
        var tracker = new SessionStatusTracker();
        int raised = 0;
        tracker.MessageCountChanged += (_, _) => Interlocked.Increment(ref raised);

        tracker.NotifyMessageCount("session", 7);
        tracker.NotifyMessageCount("session", 7);

        await Assert.That(raised).IsEqualTo(1);
    }
}
