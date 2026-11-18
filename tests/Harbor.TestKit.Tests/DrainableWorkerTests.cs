namespace Harbor.TestKit.Tests;

/// <summary>
///     The test worker's one contract: "idle" means the queue is empty AND
///     the in-flight item has finished. The assertions here use gates rather
///     than sleeps, so a slow scheduler cannot make them pass by accident.
/// </summary>
public class DrainableWorkerTests
{
    [Test]
    public async Task DrainAsync_WhenIdle_ReturnsImmediately()
    {
        var worker = new DrainableWorker();

        await worker.DrainAsync();

        await Assert.That(worker.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task DrainAsync_WaitsForBothTheRunningItemAndTheQueuedOne()
    {
        var worker = new DrainableWorker();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        worker.Enqueue(async () =>
        {
            order.Add("first-start");
            await firstGate.Task;
            order.Add("first-done");
        });
        worker.Enqueue(async () =>
        {
            secondStarted.SetResult();
            order.Add("second-start");
            await secondGate.Task;
            order.Add("second-done");
        });

        Task drain = worker.DrainAsync();

        await Assert.That(worker.PendingCount).IsEqualTo(2)
            .Because("one item is running and one is waiting; counting only the queue would report idle here");
        await Assert.That(drain.IsCompleted).IsFalse();
        await Assert.That(order).IsEquivalentTo(new[] { "first-start" });

        firstGate.SetResult();
        await secondStarted.Task;
        await Assert.That(drain.IsCompleted).IsFalse()
            .Because("the second item has only just started; the queue being empty is not the same as being idle");
        await Assert.That(order).IsEquivalentTo(new[] { "first-start", "first-done", "second-start" });

        secondGate.SetResult();
        await drain;

        await Assert.That(worker.PendingCount).IsEqualTo(0);
        await Assert.That(order).IsEquivalentTo(new[]
        {
            "first-start", "first-done", "second-start", "second-done",
        });
    }

    [Test]
    public async Task DrainAsync_RethrowsTheFirstFaultButStillRunsLaterWork()
    {
        var worker = new DrainableWorker();
        var laterRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        worker.Enqueue(() => Task.FromException(new InvalidOperationException("boom")));
        worker.Enqueue(() =>
        {
            laterRan.SetResult();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.DrainAsync());
        await laterRan.Task;

        await Assert.That(worker.PendingCount).IsEqualTo(0)
            .Because("one bad item must not strand the rest of the queue");
    }

    [Test]
    public async Task DrainAsync_WithCancelledWait_ThrowsWithoutCancellingTheWork()
    {
        var worker = new DrainableWorker();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool workFinished = false;
        worker.Enqueue(async () =>
        {
            await gate.Task;
            workFinished = true;
        });

        using var cts = new CancellationTokenSource();
        Task drain = worker.DrainAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await drain);

        gate.SetResult();
        await worker.DrainAsync();
        await Assert.That(workFinished).IsTrue()
            .Because("the token cancels the wait, not the work the test queued");
    }
}
