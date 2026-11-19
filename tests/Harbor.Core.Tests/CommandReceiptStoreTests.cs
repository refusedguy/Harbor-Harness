using Harbor.Application.Agents;

namespace Harbor.Core.Tests;

/// <summary>
///     Issue #2662, idempotent command receipts: one receipt per command id,
///     a replay observes the first receipt, and concurrent presenters cannot
///     record the same command twice.
/// </summary>
public class CommandReceiptStoreTests
{
    private static readonly DateTimeOffset FirstStamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task TryRecordAsync_FirstCall_RecordsTheReceipt()
    {
        var store = new InMemoryCommandReceiptStore();

        CommandReceiptSubmission submission = await store.TryRecordAsync(
            "cmd-1", "run-1", "node-1", FirstStamp);

        await Assert.That(submission.IsRecorded).IsTrue();
        await Assert.That(submission.IsDuplicate).IsFalse();
        await Assert.That(submission.Receipt.CommandId).IsEqualTo("cmd-1");
        await Assert.That(submission.Receipt.RunId).IsEqualTo("run-1");
        await Assert.That(submission.Receipt.NodeId).IsEqualTo("node-1");
        await Assert.That(submission.Receipt.RecordedAtUtc).IsEqualTo(FirstStamp);
    }

    [Test]
    public async Task TryRecordAsync_RepeatedCommandId_ReturnsTheFirstReceiptAsDuplicate()
    {
        var store = new InMemoryCommandReceiptStore();
        CommandReceiptSubmission first = await store.TryRecordAsync(
            "cmd-1", "run-1", "node-1", FirstStamp);

        CommandReceiptSubmission replay = await store.TryRecordAsync(
            "cmd-1", "run-2", "node-2", FirstStamp.AddDays(1));

        await Assert.That(replay.IsDuplicate).IsTrue();
        await Assert.That(replay.IsRecorded).IsFalse();
        await Assert.That(replay.Receipt).IsEqualTo(first.Receipt);
        await Assert.That(replay.Receipt.RunId).IsEqualTo("run-1");
        await Assert.That(replay.Receipt.RecordedAtUtc).IsEqualTo(FirstStamp);

        CommandReceipt? stored = await store.GetAsync("cmd-1");
        await Assert.That(stored).IsEqualTo(first.Receipt);
    }

    [Test]
    public async Task TryRecordAsync_DifferentCommandIds_RecordSeparately()
    {
        var store = new InMemoryCommandReceiptStore();

        CommandReceiptSubmission a = await store.TryRecordAsync("a", "run-1", "n1", FirstStamp);
        CommandReceiptSubmission b = await store.TryRecordAsync("b", "run-1", "n2", FirstStamp);

        await Assert.That(a.IsRecorded).IsTrue();
        await Assert.That(b.IsRecorded).IsTrue();
        await Assert.That(a.Receipt.CommandId).IsEqualTo("a");
        await Assert.That(b.Receipt.CommandId).IsEqualTo("b");
        await Assert.That(await store.GetAsync("a")).IsEqualTo(a.Receipt);
        await Assert.That(await store.GetAsync("b")).IsEqualTo(b.Receipt);
    }

    [Test]
    public async Task TryRecordAsync_ConcurrentPresenters_RecordExactlyOnce()
    {
        var store = new InMemoryCommandReceiptStore();

        CommandReceiptSubmission[] results = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(i => store.TryRecordAsync(
                "shared", $"run-{i}", $"node-{i}", FirstStamp.AddSeconds(i))));

        await Assert.That(results.Count(r => r.IsRecorded)).IsEqualTo(1);
        await Assert.That(results.Count(r => r.IsDuplicate)).IsEqualTo(63);
        await Assert.That(results.All(r => r.Receipt == results[0].Receipt)).IsTrue();
        await Assert.That((await store.GetAsync("shared"))!).IsEqualTo(results[0].Receipt);
    }

    [Test]
    public async Task GetAsync_UnknownCommandId_ReturnsNull()
    {
        var store = new InMemoryCommandReceiptStore();

        await Assert.That(await store.GetAsync("never-recorded")).IsNull();
    }
}
