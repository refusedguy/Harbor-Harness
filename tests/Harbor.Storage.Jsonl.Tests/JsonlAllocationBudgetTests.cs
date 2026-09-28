using System.Text;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
/// Allocation-budget coverage for the JSONL read hot path (#186).
/// <c>JsonlLineParser.Parse</c> is the per-line span fast path (no
/// per-line <c>JsonDocument</c>); <c>GetMessagesAsync</c> is the
/// store-level read it feeds. Bounds are generous, CI-safe tripwires in the
/// <c>SpanParserTests</c> tradition — a <c>JsonDocument</c>-per-line revert
/// blows them, the span path stays comfortably inside.
/// </summary>
public class JsonlAllocationBudgetTests
{
    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    [Test]
    public async Task Parse_UserLine_StaysBounded()
    {
        byte[] line = Encoding.UTF8.GetBytes(
            """{"type":"message","id":"m1","createdAt":"2026-01-01T00:00:00Z","role":"user","payload":{"content":"hello","agent":"code","model":"test-model"}}""");

        var first = JsonlLineParser.Parse(line, "sess-1");
        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(((UserMessage)first.Value).Content).IsEqualTo("hello");

        for (int i = 0; i < 200; i++)
        {
            _ = JsonlLineParser.Parse(line, "sess-1");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int parses = 1_000;
        for (int i = 0; i < parses; i++)
        {
            _ = JsonlLineParser.Parse(line, "sess-1");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"jsonl-alloc: Parse avg = {(double)allocated / parses:F0} B over {parses} user lines");
        await Assert.That(allocated).IsLessThanOrEqualTo(parses * 8L * 1_024L);
    }

    [Test]
    public async Task GetMessages_SeededStore_StaysBounded()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            for (int i = 0; i < 100; i++)
            {
                await store.AppendMessageAsync(session.Id, new UserMessage(
                    $"m{i}", session.Id, DateTimeOffset.UtcNow, $"message {i}", "code", "claude-opus-4"));
            }

            var first = await store.GetMessagesAsync(session.Id);
            await Assert.That(first.IsSuccess).IsTrue();
            await Assert.That(first.Value.Count).IsEqualTo(100);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int reads = 20;
            for (int i = 0; i < reads; i++)
            {
                _ = await store.GetMessagesAsync(session.Id);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"jsonl-alloc: GetMessages(100 msgs) avg = {(double)allocated / reads:F0} B over {reads} reads");
            await Assert.That(allocated).IsLessThanOrEqualTo(reads * 512L * 1_024L);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}
