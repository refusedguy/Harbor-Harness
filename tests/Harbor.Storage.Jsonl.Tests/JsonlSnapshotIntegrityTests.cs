using System.Text;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
/// #459 — a JSONL read must never publish a truncated message list as a
/// cacheable, authoritative view of the session.
/// </summary>
/// <remarks>
/// <para>
///     The defect had two halves. The read sized its buffer from a
///     <c>FileInfo.Length</c> snapshot taken <em>before</em> the stream was
///     opened and then filled it <c>while (read &lt; buffer.Length)</c> — the
///     pool rounds the buffer <em>up</em>, so an append landing in that window
///     extended the read past the measured end and cut the new record
///     mid-line. The cut line was merely <c>LogWarning</c>ed, and the short
///     list was cached as if it were the whole session.
/// </para>
/// <para>
///     The tests below pin the two properties that make that impossible. The
///     cache key carries the file's <em>length</em> as well as its mtime — a
///     filesystem with coarse timestamps produces exactly the "same mtime,
///     more content" state the old key mistook for a hit. And a read that
///     detected the file moving under it is never published, so a raced read
///     cannot be remembered.
/// </para>
/// </remarks>
public class JsonlSnapshotIntegrityTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int second) => Epoch.AddSeconds(second);

    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    /// <summary>
    ///     Encodes exactly what <c>AppendMessageAsync</c> would have written,
    ///     without going through the store — the whole point of these tests is
    ///     to mutate the file behind the cache's back.
    /// </summary>
    private static byte[] UserLine(string sessionId, string id, int second, string content)
    {
        var message = new UserMessage(id, sessionId, At(second), content, "code", "test-model");
        var entry = new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));
        return SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
    }

    private static async Task<(JsonlSessionStore Store, string SessionId)> SeedAsync(int messageCount)
    {
        var store = CreateStore();
        var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
        for (int i = 0; i < messageCount; i++)
        {
            var appended = await store.AppendMessageAsync(session.Id,
                new UserMessage($"m{i}", session.Id, At(i + 1), $"message {i}", "code", "test-model"));
            await Assert.That(appended.IsSuccess).IsTrue();
        }
        return (store, session.Id);
    }

    private static void Drop(JsonlSessionStore store)
    {
        if (Directory.Exists(store.GetRootDirectory()))
            Directory.Delete(store.GetRootDirectory(), true);
    }

    [Test]
    public async Task GetMessagesAsync_AppendThatKeepsTheSameMtime_IsNotACacheHit()
    {
        // #459, cache half: the freshness key used to be the mtime alone, so a
        // write landing in the same timestamp tick as the cached parse read as
        // a hit — and the appended message stayed invisible. Appending outside
        // the store and rewinding the timestamp reproduces that state exactly.
        var (store, sessionId) = await SeedAsync(1);
        try
        {
            var warm = await store.GetMessagesAsync(sessionId);
            await Assert.That(warm.IsSuccess).IsTrue();
            await Assert.That(warm.Value.Count).IsEqualTo(1);

            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");
            DateTime frozen = File.GetLastWriteTimeUtc(path);

            await File.AppendAllBytesAsync(path, UserLine(sessionId, "m-late", 2, "late but present"));
            File.SetLastWriteTimeUtc(path, frozen);

            var after = await store.GetMessagesAsync(sessionId);
            await Assert.That(after.IsSuccess).IsTrue();
            await Assert.That(after.Value.Count).IsEqualTo(2);
            await Assert.That(after.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m-late" });
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task GetMessagesAsync_TornTrailingRecord_SkipsItWithoutPoisoningTheCache()
    {
        // #459, read half: a record caught mid-write is not a record. It must be
        // skipped — and once the writer finishes it, the store must notice even
        // though the timestamp never moved.
        var (store, sessionId) = await SeedAsync(1);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");
            DateTime frozen = File.GetLastWriteTimeUtc(path);

            // First half of a record, no line terminator: exactly the bytes a
            // reader can observe while the append is still running.
            await File.AppendAllBytesAsync(path, Encoding.UTF8.GetBytes("{\"type\":\"message\",\"id\":\"m-torn\",\"creat"));
            File.SetLastWriteTimeUtc(path, frozen);

            var during = await store.GetMessagesAsync(sessionId);
            await Assert.That(during.IsSuccess).IsTrue();
            await Assert.That(during.Value.Count).IsEqualTo(1);
            await Assert.That(during.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0" });

            // The writer completes the record. Same mtime, different length.
            await File.AppendAllBytesAsync(path, Encoding.UTF8.GetBytes(
                "edAt\":\"2026-01-01T00:00:02.0000000+00:00\",\"role\":\"user\",\"payload\":" +
                "{\"content\":\"completed\",\"agent\":\"code\",\"model\":\"test-model\"}}\n"));
            File.SetLastWriteTimeUtc(path, frozen);

            var after = await store.GetMessagesAsync(sessionId);
            await Assert.That(after.IsSuccess).IsTrue();
            await Assert.That(after.Value.Count).IsEqualTo(2);
            await Assert.That(after.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m-torn" });
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task ParseMessagesFromDiskAsync_QuiescentFile_ReportsAStableSnapshot()
    {
        // The reader now reports the file state its bytes came from. For a file
        // nobody is touching, that pairing must be provable — this is what lets
        // the store cache the result.
        var (store, sessionId) = await SeedAsync(3);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");

            var result = await SessionFileReader.ParseMessagesFromDiskAsync(
                path, sessionId, NullLogger.Instance, CancellationToken.None);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.IsStable).IsTrue();
            await Assert.That(result.Value.Messages.Count).IsEqualTo(3);
            await Assert.That(result.Value.Messages.Select(m => m.Id))
                .IsEquivalentTo(new[] { "m0", "m1", "m2" });
            await Assert.That(result.Value.Stat.Length).IsEqualTo(new FileInfo(path).Length);
            await Assert.That(result.Value.Stat.Matches(SessionFileStat.Read(path))).IsTrue();
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task GetMessagesAsync_HeaderOnlyFile_YieldsAnEmptyStableSnapshot()
    {
        // A session created but never written to must not look like a failure,
        // and its empty view must stay cacheable.
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");

            var result = await SessionFileReader.ParseMessagesFromDiskAsync(
                path, session.Id, NullLogger.Instance, CancellationToken.None);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.IsStable).IsTrue();
            await Assert.That(result.Value.Messages).IsEmpty();
            await Assert.That(result.Value.Stat.Length).IsGreaterThan(0L);

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value).IsEmpty();
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task GetMessagesAsync_WhileAnotherWriterAppends_NeverRegressesAndConverges()
    {
        // The invariant behind the bug report: a message may be missing from a
        // read that raced the write (never a partial one), but it must never
        // disappear from a read that already saw it, and the view must converge
        // on the file once the writer stops.
        var (store, sessionId) = await SeedAsync(0);
        try
        {
            const int messageCount = 200;

            var writer = Task.Run(async () =>
            {
                int landed = 0;
                for (int i = 1; i <= messageCount; i++)
                {
                    var result = await store.AppendMessageAsync(sessionId,
                        new UserMessage($"r{i}", sessionId, At(i), $"racing {i}", "code", "test-model"));

                    // A write can legitimately lose a platform-level race with a
                    // concurrent reader; the store reports that, so count only
                    // what actually landed.
                    if (result.IsSuccess) landed++;
                }
                return landed;
            });

            int maxSeen = 0;
            var reader = Task.Run(async () =>
            {
                while (!writer.IsCompleted)
                {
                    var result = await store.GetMessagesAsync(sessionId);
                    if (!result.IsSuccess) continue;

                    int count = result.Value.Count;
                    await Assert.That(count).IsGreaterThanOrEqualTo(maxSeen);
                    maxSeen = Math.Max(maxSeen, count);
                }
            });

            await Task.WhenAll(writer, reader);
            int appended = await writer;

            var final = await store.GetMessagesAsync(sessionId);
            await Assert.That(final.IsSuccess).IsTrue();
            await Assert.That(final.Value.Count).IsEqualTo(appended);
            await Assert.That(final.Value.Count).IsGreaterThanOrEqualTo(maxSeen);
        }
        finally
        {
            Drop(store);
        }
    }
}
