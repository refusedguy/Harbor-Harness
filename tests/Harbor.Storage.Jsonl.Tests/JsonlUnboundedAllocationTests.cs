// JsonlUnboundedAllocationTests.cs — #460, the unbounded allocations on
// untrusted session input.
//
// The hazards the issue names, and what each one is now:
//
//   1. the read path's ArrayPool<byte>.Rent((int)fileLength) behind a
//      `length > int.MaxValue` guard  →  streamed through one pooled 256 KiB
//      block, with a hard 32 MiB ceiling per record;
//   2. the rewrite paths' File.ReadAllLines(...).ToList() + WriteAllLinesAtomic
//      under the per-session semaphore  →  streamed record by record into the
//      temp sibling, raw bytes in and out, nothing decoded;
//   3. (WordDiff's LCS table is in the CellForge suite — WordDiffBoundsTests.)
//
// The bounds, and why these numbers:
//
//   • ChunkedLineReader.ChunkBytes = 256 KiB — inside ArrayPool.Shared's 1 MiB
//     poolable tier, so the block is a genuinely pooled array rather than a
//     fresh LOH one.
//   • ChunkedLineReader.MaxRecordBytes = 32 MiB — more than 3x ReadTool's 10 MiB
//     text ceiling, which is the largest payload a message can legitimately
//     carry (JSON escaping inflates it further), and 64x below the 2 GiB buffer
//     it replaces.
//
//   ⇒ peak memory is one block plus one record, INDEPENDENT of the file size.
//
// The reader tests assert that bound directly (ChunkedLineReader.BufferSize),
// which is deterministic; the store-level tests assert the file is still read
// and rewritten correctly at sizes where the old shapes would have allocated
// one or two more copies of the file. Nothing here writes 2 GB on purpose:
// that is the old guard's failure point, and a CI runner that could do it would
// not survive it.

using System.Text;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>#460 — every JSONL path bounds the size it is willing to materialize.</summary>
public class JsonlUnboundedAllocationTests
{
    private const long MiB = 1024 * 1024;

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-460-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    private static void Drop(JsonlSessionStore store)
    {
        if (Directory.Exists(store.GetRootDirectory()))
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    /// <summary>Temps the rewrite path could have left behind.</summary>
    private static string[] LeftoverTemps(JsonlSessionStore store) =>
        [.. Directory.GetFiles(store.GetRootDirectory())
            .Where(f => f.EndsWith(".tmp", StringComparison.Ordinal))];

    private static async Task<(JsonlSessionStore Store, string SessionId)> SeedAsync(int messageCount)
    {
        var store = CreateStore();
        var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
        for (int i = 0; i < messageCount; i++)
        {
            var appended = await store.AppendMessageAsync(session.Id, new UserMessage(
                $"m{i}", session.Id, Epoch.AddSeconds(i + 1), $"message {i}", "code", "claude-opus-4"));
            await Assert.That(appended.IsSuccess).IsTrue();
        }
        return (store, session.Id);
    }

    /// <summary>
    ///     One message carrying <paramref name="payloadBytes" /> of filler — the
    ///     shape a 10 MB <c>read</c> result takes in a session file.
    /// </summary>
    private static async Task AppendFatMessageAsync(
        JsonlSessionStore store, string sessionId, string id, int payloadBytes)
    {
        var appended = await store.AppendMessageAsync(sessionId, new UserMessage(
            id, sessionId, Epoch, new string('x', payloadBytes), "code", "claude-opus-4"));
        await Assert.That(appended.IsSuccess).IsTrue();
    }

    // --------------------------------------------------- the bound, directly

    [Test]
    public async Task ChunkedLineReader_TwentyFourMegabytesOfRecords_NeverGrowsTheBlock()
    {
        // The deterministic form of the #460 gate. The old read sized one
        // ArrayPool rent from the file length, so the block was the file. This
        // reader's block is sized by ChunkBytes and grows only for a record
        // that does not fit inside it — and ordinary records always fit.
        byte[] line = SampleRecord(64 * 1024);
        int repeats = (int)(24 * MiB / line.Length);

        using var stream = new MemoryStream();
        for (int i = 0; i < repeats; i++)
        {
            stream.Write(line, 0, line.Length);
        }
        stream.Position = 0;

        using var reader = new ChunkedLineReader(stream);
        int seen = 0;
        while (reader.Fill())
        {
            while (reader.TryGetRecord(out _))
            {
                seen++;
            }
        }
        if (reader.TryGetTrailingRecord(out _))
        {
            seen++;
        }

        await Assert.That(seen).IsEqualTo(repeats);
        await Assert.That(reader.BytesRead).IsEqualTo(stream.Length);

        // 24 MiB of records through a block the size of the CONSTANT — this is
        // the whole #460 claim for the read path, and it is checked against a
        // 24 MiB stream rather than a 24 MiB allocation total, so it cannot
        // drift with a JIT or a GC tweak.
        await Assert.That(reader.BufferSize).IsLessThanOrEqualTo(ChunkedLineReader.InitialBlockBytes);
        await Assert.That(stream.Length).IsGreaterThan(20L * MiB);
    }

    [Test]
    public async Task ChunkedLineReader_RecordSpanningBlocks_IsCarriedWholeAndGrowsOnlyForIt()
    {
        // The block DOES grow for a record that does not fit — bounded by the
        // record, and by MaxRecordBytes, which is the whole point of the design.
        // A three-block record must come back byte-identical.
        int payloadBytes = ChunkedLineReader.ChunkBytes * 3;
        byte[] line = SampleRecord(payloadBytes);
        byte[] header = Encoding.UTF8.GetBytes("{\"type\":\"session\"}\n");
        byte[] trailing = Encoding.UTF8.GetBytes("trailing record with no terminator");

        using var stream = new MemoryStream();
        stream.Write(header, 0, header.Length);
        stream.Write(line, 0, line.Length);
        stream.Write(trailing, 0, trailing.Length);
        stream.Position = 0;

        using var reader = new ChunkedLineReader(stream);
        var records = new List<string>();
        while (reader.Fill())
        {
            while (reader.TryGetRecord(out var record))
            {
                records.Add(Encoding.UTF8.GetString(record));
            }
        }
        if (reader.TryGetTrailingRecord(out var last))
        {
            records.Add(Encoding.UTF8.GetString(last));
        }

        await Assert.That(records.Count).IsEqualTo(3);
        await Assert.That(records[0]).IsEqualTo("{\"type\":\"session\"}");

        // Byte-identical, not just the right length: the carry has to
        // reassemble the record exactly, and it has to stop at its own LF
        // rather than running on into the next record.
        await Assert.That(records[1]).IsEqualTo(Encoding.UTF8.GetString(line).TrimEnd('\n'));

        // And the unterminated tail is exactly the bytes that followed it — no
        // stale pool memory from the previous tenant of the block, which is
        // what an unbounded AsSpan(_cursor) here would have produced.
        await Assert.That(records[2]).IsEqualTo("trailing record with no terminator");

        // It grew past the initial block to carry the record, and no further
        // (the bound is loose on purpose: ArrayPool rounds to a power of two,
        // so an exact figure would be testing the pool, not the reader).
        await Assert.That(reader.BufferSize).IsGreaterThan(ChunkedLineReader.InitialBlockBytes);
        await Assert.That(reader.BufferSize).IsLessThan(line.Length * 2);
    }

    /// <summary>
    ///     One well-formed message record, terminated, of the requested size.
    ///     <paramref name="id" /> is part of it because the reader keys messages
    ///     by id, so a fixture that repeats one id would collapse 1 000 records
    ///     into 1 and quietly stop testing anything.
    /// </summary>
    private static byte[] SampleRecord(int payloadBytes, string id = "m0") => Encoding.UTF8.GetBytes(
        $"{{\"type\":\"message\",\"id\":\"{id}\",\"createdAt\":\"2026-01-01T00:00:00+00:00\"," +
        "\"role\":\"user\",\"payload\":{\"content\":\"" + new string('x', payloadBytes) +
        "\",\"agent\":\"code\",\"model\":\"test-model\"}}\n");

    // ---------------------------------------------------------------- read path

    [Test]
    public async Task Read_ThousandRecordSession_ReadsEveryRecordWithoutAddingTheFile()
    {
        // A long session is thousands of tool results and assistant turns, and
        // that is the shape the file-sized rent was guarding. The file here is
        // >20 MiB; the read must return all of it and must not allocate another
        // file's worth on top of the message graph it legitimately returns.
        var (store, sessionId) = await SeedAsync(0);
        try
        {
            const int records = 1_000;
            const int payloadBytes = 24 * 1024;

            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                for (int i = 0; i < records; i++)
                {
                    byte[] line = SampleRecord(payloadBytes, $"m{i}");
                    fs.Write(line, 0, line.Length);
                }
            }

            long fileBytes = new FileInfo(path).Length;
            await Assert.That(fileBytes).IsGreaterThan(20 * MiB);

            // Warm the pool, the file cache and the JIT, then measure a cold miss.
            _ = await store.GetMessagesAsync(sessionId);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetTotalAllocatedBytes(precise: true);
            var read = await SessionFileReader.ParseMessagesFromDiskAsync(
                path, sessionId, NullLogger.Instance, CancellationToken.None);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Console.WriteLine($"jsonl-460: read of a {fileBytes / MiB} MiB session allocated {allocated / MiB} MiB");

            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.IsStable).IsTrue();
            await Assert.That(read.Value.Messages.Count).IsEqualTo(records);

            // Each record decodes to a payloadBytes-long UTF-16 string, and that
            // message graph is the irreducible result. The question is only what
            // the READ adds on top: the old shape added a second file-sized
            // array, this one adds a 256 KiB block. The gate sits just under the
            // old shape, so the new one clears it with a whole file of slack.
            long graphFloor = (long)records * 2 * payloadBytes;
            await Assert.That(allocated).IsLessThan(graphFloor + fileBytes);
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task Read_RecordOverTheCeiling_IsSkippedAndTheRestStillParses()
    {
        // The ceiling end to end, through a real file: the oversized record is
        // not a message, the seeded one before it and the well-formed one after
        // it both land, and the reader reports success.
        var (store, sessionId) = await SeedAsync(1);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");

            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] prefix = Encoding.UTF8.GetBytes(
                    "{\"type\":\"message\",\"id\":\"huge\",\"role\":\"user\",\"payload\":{\"content\":\"");
                byte[] block = Encoding.UTF8.GetBytes(new string('z', (int)MiB));
                byte[] suffix = Encoding.UTF8.GetBytes("\"}}\n");
                long budget = ChunkedLineReader.MaxRecordBytes + MiB;

                fs.Write(prefix, 0, prefix.Length);
                long written = prefix.Length;
                while (written < budget)
                {
                    int take = (int)Math.Min(block.Length, budget - written);
                    fs.Write(block, 0, take);
                    written += take;
                }
                fs.Write(suffix, 0, suffix.Length);
            }

            // A well-formed record AFTER the oversized one, to prove the stream
            // resynced on the LF rather than swallowing the rest of the file.
            await AppendFatMessageAsync(store, sessionId, "after-huge", 32);

            var read = await SessionFileReader.ParseMessagesFromDiskAsync(
                path, sessionId, NullLogger.Instance, CancellationToken.None);

            await Assert.That(read.IsSuccess).IsTrue();

            var ids = read.Value.Messages.Select(m => m.Id).ToList();
            await Assert.That(ids).DoesNotContain("huge");
            await Assert.That(ids).Contains("m0");
            await Assert.That(ids).Contains("after-huge");
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task Read_MalformedRecords_BoundTheErrorReport()
    {
        // The error list is the same family of hazard: a file that is nothing
        // but garbage used to append one string per failed line — 5 000 of them
        // alive at once, all of them concatenated into a single LogWarning. The
        // report is bounded now, and that is observable from the outside: the
        // warning names every failure but carries only the first 20.
        var (store, sessionId) = await SeedAsync(1);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");

            const int garbageLines = 5_000;
            var garbage = new StringBuilder();
            for (int i = 0; i < garbageLines; i++)
            {
                garbage.Append("{not json ").Append(i).Append('\n');
            }
            await File.AppendAllTextAsync(path, garbage.ToString());

            var logger = new CapturingLogger();
            var read = await SessionFileReader.ParseMessagesFromDiskAsync(
                path, sessionId, logger, CancellationToken.None);

            // The good record before the garbage is still there.
            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.Messages.Select(m => m.Id)).IsEquivalentTo(new[] { "m0" });

            await Assert.That(logger.Warnings).IsNotEmpty();

            string malformed = logger.Warnings.First(w => w.Contains("malformed", StringComparison.Ordinal));
            // Every failure is counted…
            await Assert.That(malformed).Contains(garbageLines.ToString());
            // …but the body is capped, so this stays a warning rather than
            // becoming 5 000 concatenated parse errors of log.
            await Assert.That(malformed.Length).IsLessThan(8_192);
        }
        finally
        {
            Drop(store);
        }
    }

    /// <summary>Keeps the warnings, so a bound on the report can be asserted.</summary>
    private sealed class CapturingLogger : ILogger
    {
        internal List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    // ------------------------------------------------------------ rewrite paths

    [Test]
    public async Task Update_HeaderEditOnABigSession_CopiesBytesWithoutMaterializingThem()
    {
        // The rewrite half of the issue: every title/status/git-branch change
        // did File.ReadAllLines(...).ToList() plus a full rewrite under the
        // per-session semaphore, to change line 1. Here the session holds a
        // 24 MB record and the whole operation allocates a few hundred KiB,
        // because records are copied as raw bytes and never decoded.
        //
        // A single big record is the right fixture HERE (and not in the read
        // test): the rewrite's whole claim is that it never materializes a
        // record at all, so the fat record is invisible to it.
        var (store, sessionId) = await SeedAsync(1);
        try
        {
            const int payloadBytes = 24 * 1024 * 1024;
            await AppendFatMessageAsync(store, sessionId, "fat", payloadBytes);

            var session = await store.GetAsync(sessionId);
            await Assert.That(session.IsSuccess).IsTrue();
            var renamed = session.Value with { Title = "renamed by #460" };

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetTotalAllocatedBytes(precise: true);
            var updated = await store.UpdateAsync(renamed);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Console.WriteLine($"jsonl-460: header rewrite of a 24 MiB session allocated {allocated / 1024} KiB");

            await Assert.That(updated.IsSuccess).IsTrue();

            // Old shape: a 24 MB string[] plus a List over it plus a 24 MB
            // re-encode — hundreds of MiB. New shape: the block, two FileStream
            // buffers and the small header record.
            await Assert.That(allocated).IsLessThan(8 * MiB);

            // And the payload survived the rewrite, byte for byte.
            var reread = await store.GetMessagesAsync(sessionId);
            await Assert.That(reread.IsSuccess).IsTrue();
            await Assert.That(reread.Value.Count).IsEqualTo(2);
            await Assert.That(((UserMessage)reread.Value.Single(m => m.Id == "fat")).Content.Length)
                .IsEqualTo(payloadBytes);
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task UpdateMessage_SeveralMessagesWithTheSameId_DropsThemAll()
    {
        // The read path is "latest id wins", so a file can legitimately hold
        // several records for one id (an update that raced an append). Every one
        // of them is stale, so every one of them has to go — a plan that dropped
        // only the first would leave a duplicate that the reader then silently
        // picks between.
        var (store, sessionId) = await SeedAsync(2);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");

            // Same id as the seeded m1, appended twice more, out of order.
            await AppendFatMessageAsync(store, sessionId, "m1", 11);
            await AppendFatMessageAsync(store, sessionId, "m1", 22);
            await AppendFatMessageAsync(store, sessionId, "m1", 33);

            var updated = await store.UpdateMessageAsync(sessionId, new UserMessage(
                "m1", sessionId, Epoch.AddSeconds(99), "final body", "code", "claude-opus-4"));
            await Assert.That(updated.IsSuccess).IsTrue();

            var reread = await store.GetMessagesAsync(sessionId);
            await Assert.That(reread.IsSuccess).IsTrue();

            // Exactly one m1, and it is the one we just wrote.
            await Assert.That(reread.Value.Count(m => m.Id == "m1")).IsEqualTo(1);
            await Assert.That(((UserMessage)reread.Value.Single(m => m.Id == "m1")).Content)
                .IsEqualTo("final body");
            await Assert.That(reread.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m2", "m1" });
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task UpdateMessage_DropsTheStaleEntryAndLeavesTheRestReadable()
    {
        // The rewrite's correctness contract, unchanged by streaming: the old
        // entry goes, the fresh one lands at the end, everything else survives.
        var (store, sessionId) = await SeedAsync(3);
        try
        {
            var updated = await store.UpdateMessageAsync(sessionId, new UserMessage(
                "m1", sessionId, Epoch.AddSeconds(99), "edited body", "code", "claude-opus-4"));
            await Assert.That(updated.IsSuccess).IsTrue();

            var reread = await store.GetMessagesAsync(sessionId);
            await Assert.That(reread.IsSuccess).IsTrue();
            await Assert.That(reread.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m2", "m1" });
            await Assert.That(((UserMessage)reread.Value.Single(m => m.Id == "m1")).Content)
                .IsEqualTo("edited body");
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task UpdateMessage_UnknownId_LeavesTheFileUntouched()
    {
        // "Not found" is an outcome, not a rewrite. Nothing matches, so the
        // plan reports Found == false, the temp copy is discarded and the
        // original keeps its bytes AND its mtime — which is what stops this
        // from degenerating into an appended duplicate.
        var (store, sessionId) = await SeedAsync(2);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");
            string before = await File.ReadAllTextAsync(path);
            DateTime stampBefore = File.GetLastWriteTimeUtc(path);

            // The directory too: the rewrite stages through a temp sibling, so
            // creating and deleting one would move the directory's mtime even
            // with the file untouched. "Not found" must not create a temp at all.
            DateTime dirStampBefore = File.GetLastWriteTimeUtc(store.GetRootDirectory());

            var updated = await store.UpdateMessageAsync(sessionId, new UserMessage(
                "does-not-exist", sessionId, Epoch, "nope", "code", "claude-opus-4"));

            await Assert.That(updated.IsFailure).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(before);
            await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(stampBefore);
            await Assert.That(File.GetLastWriteTimeUtc(store.GetRootDirectory())).IsEqualTo(dirStampBefore);
            await Assert.That(LeftoverTemps(store)).IsEmpty();
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task Update_EmptySessionFile_IsAnOutcomeAndLeavesNoTemp()
    {
        // The header is written by CreateAsync, so a file with no records is
        // corruption, not a session to edit. It has to report that rather than
        // rewrite — and the abandoned temp must not be left on disk.
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            await File.WriteAllTextAsync(path, string.Empty);

            var updated = await store.UpdateAsync(session);

            await Assert.That(updated.IsFailure).IsTrue();
            await Assert.That(updated.Error).Contains("is empty");
            await Assert.That(LeftoverTemps(store)).IsEmpty();
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task DeleteMessagesAfter_AnchorsOnTheFirstMatchingId()
    {
        // A duplicate id makes "which record is the anchor" ambiguous, and the
        // pre-#460 code resolved it by index-scan order: the FIRST match. The
        // streaming plan has to land on the same one, or a rewind would drop a
        // different set of messages than it used to for the same input.
        var (store, sessionId) = await SeedAsync(4);
        try
        {
            // A second record for m1, after m2 and m3 — so anchoring on the
            // first m1 keeps m2/m3 and the second m1, while anchoring on the
            // second would keep one more message.
            await AppendFatMessageAsync(store, sessionId, "m1", 8);

            var result = await store.DeleteMessagesAfterAsync(sessionId, "m1");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo(3); // m2, m3, and the dup m1

            var reread = await store.GetMessagesAsync(sessionId);
            await Assert.That(reread.IsSuccess).IsTrue();
            await Assert.That(reread.Value.Select(m => m.Id))
                .IsEquivalentTo(new[] { "m0", "m1", "m2", "m3" });
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task DeleteMessagesAfter_RewindKeepsThePrefixAndDropsTheRest()
    {
        var (store, sessionId) = await SeedAsync(5);
        try
        {
            var result = await store.DeleteMessagesAfterAsync(sessionId, "m2");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo(2);

            var reread = await store.GetMessagesAsync(sessionId);
            await Assert.That(reread.IsSuccess).IsTrue();
            await Assert.That(reread.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1", "m2" });
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task DeleteMessagesAfter_RewindToTheLastMessage_LeavesTheFileAlone()
    {
        // Found, zero removed. The pre-#460 code only rewrote when something
        // was actually dropped, so the mtime stayed put and the store's parse
        // cache stayed warm. A streaming plan can find its target and still
        // have nothing to do; that must stay a no-op.
        var (store, sessionId) = await SeedAsync(3);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");
            string before = await File.ReadAllTextAsync(path);

            var result = await store.DeleteMessagesAfterAsync(sessionId, "m2");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo(0);
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(before);
            await Assert.That(LeftoverTemps(store)).IsEmpty();
        }
        finally
        {
            Drop(store);
        }
    }

    [Test]
    public async Task DeleteMessagesAfter_UnknownAnchor_IsAnOutcomeNotARewrite()
    {
        var (store, sessionId) = await SeedAsync(2);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");
            string before = await File.ReadAllTextAsync(path);

            var result = await store.DeleteMessagesAfterAsync(sessionId, "no-such-message");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(before);
            await Assert.That(LeftoverTemps(store)).IsEmpty();
        }
        finally
        {
            Drop(store);
        }
    }
}
