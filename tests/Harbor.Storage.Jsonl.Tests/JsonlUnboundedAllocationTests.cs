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
        // record and, at the top end, by MaxRecordBytes, which is the point of
        // the design. A record three times the block's initial size must come
        // back byte-identical.
        int payloadBytes = ChunkedLineReader.InitialBlockBytes * 3;
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

        // It grew past the initial block to carry the record, and no further.
        // The upper bound is loose on purpose: ArrayPool rounds to a power of
        // two, so an exact figure would be testing the pool, not the reader.
        await Assert.That(reader.BufferSize).IsGreaterThan(ChunkedLineReader.InitialBlockBytes);
        await Assert.That(reader.BufferSize).IsLessThan(line.Length * 2);
    }

    [Test]
    public async Task ChunkedLineReader_RecordAtTheCeiling_NeverExceedsIt()
    {
        // The hard bound itself, checked directly on both sides of the line. What
        // it stops: a hostile 2 GB single-line file renting 2 GB. The stream is
        // synthetic rather than a real file, so the test never writes 32 MiB to
        // disk to check a number.
        long ceiling = ChunkedLineReader.MaxRecordBytes;

        // One byte under the ceiling: still a record, still assembled.
        using (var stream = SyntheticRecord(ceiling - 1))
        {
            using var reader = new ChunkedLineReader(stream);
            int seen = Drain(reader);
            await Assert.That(seen).IsEqualTo(1);
            await Assert.That(reader.SawOversizedRecord).IsFalse();
            await Assert.That((long)reader.BufferSize).IsLessThanOrEqualTo(ceiling);
        }

        // Exactly at the ceiling: refused. This is the case that matters — with
        // a `>` check the block fills to the ceiling, there is no room to read
        // the bytes that end the record, and the whole thing comes back as if it
        // were a legitimate unterminated final line.
        using (var stream = SyntheticRecord(ceiling))
        {
            using var reader = new ChunkedLineReader(stream);
            int seen = Drain(reader);
            await Assert.That(seen).IsEqualTo(0);
            await Assert.That(reader.SawOversizedRecord).IsTrue();
            await Assert.That((long)reader.BufferSize).IsLessThanOrEqualTo(ceiling);
        }

        // And well past it: same answer, and still no bigger block.
        using (var stream = SyntheticRecord(ceiling + 4096))
        {
            using var reader = new ChunkedLineReader(stream);
            int seen = Drain(reader);
            await Assert.That(seen).IsEqualTo(0);
            await Assert.That(reader.SawOversizedRecord).IsTrue();
            await Assert.That((long)reader.BufferSize).IsLessThanOrEqualTo(ceiling);
        }
    }

    /// <summary>
    ///     Takes the first record and reports its length. A local helper because
    ///     the span cannot live across an await (CS4007) and the test method is
    ///     async.
    /// </summary>
    private static int ReadHeadLength(ChunkedLineReader reader)
    {
        if (!reader.TryGetRecord(out var head))
        {
            return -1;
        }

        return head.Length;
    }

    /// <summary>Counts every record the reader yields, to EOF.</summary>
    private static int Drain(ChunkedLineReader reader)
    {
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

        return seen;
    }

    /// <summary>
    ///     A single <paramref name="bytes" />-long record with no LF in it, and
    ///     nothing after it. Only the length matters here — the reader never
    ///     decodes, so zeroing is enough and nothing is materialized.
    /// </summary>
    private static Stream SyntheticRecord(long bytes) => new SyntheticLengthStream(bytes);

    /// <summary>Reports <see cref="Length" /> without materializing a byte of it.</summary>
    private sealed class SyntheticLengthStream : Stream
    {
        private readonly long _length;
        private long _position;

        internal SyntheticLengthStream(long length) => _length = length;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = _length - _position;
            if (left <= 0)
            {
                return 0;
            }

            int n = (int)Math.Min(count, left);
            Array.Clear(buffer, offset, n); // the content is irrelevant
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
    public async Task CopyRemainderTo_PipesEveryByteExactlyOnce()
    {
        // The verbatim path's own contract, and it is a byte-counting contract:
        // no gap, no duplication, no reordering. The cursor has to reset with
        // each refill or every block after the first is skipped, which looks
        // from the outside exactly like a lost record.
        //
        // The payload is several refills' worth so the bug cannot hide behind a
        // single-block pass, and the trailing LF is a separate byte so a dropped
        // terminator is caught too.
        byte[] payload = new byte[3 * ChunkedLineReader.ChunkBytes + 977];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        using var source = new MemoryStream();
        source.Write("head\n"u8);
        source.Write(payload, 0, payload.Length);
        source.WriteByte((byte)'\n');
        source.Position = 0;

        using var reader = new ChunkedLineReader(source);

        // Fill first: the block is empty until a refill, so TryGetRecord on a
        // fresh reader has nothing to hand out.
        await Assert.That(reader.Fill()).IsTrue();
        await Assert.That(ReadHeadLength(reader)).IsEqualTo(4);

        using var sink = new MemoryStream();
        reader.CopyRemainderTo(sink);

        byte[] piped = sink.ToArray();
        await Assert.That(piped.Length).IsEqualTo(payload.Length + 1);
        await Assert.That(piped[^1]).IsEqualTo((byte)'\n');

        // Span comparison in a sync helper for the same reason as above.
        await Assert.That(PayloadMatches(piped, payload)).IsTrue();
    }

    /// <summary>
    ///     Whether <paramref name="piped" /> starts with exactly
    ///     <paramref name="payload" />. A helper because a span cannot cross an
    ///     await (CS4007).
    /// </summary>
    private static bool PayloadMatches(byte[] piped, byte[] payload) =>
        piped.AsSpan(0, payload.Length).SequenceEqual(payload);

    [Test]
    public async Task Update_HeaderEditOnABigSession_CopiesBytesWithoutMaterializingThem()
    {
        // The rewrite half of the issue: every title/status/git-branch change
        // did File.ReadAllLines(...).ToList() plus a full rewrite under the
        // per-session semaphore, to change line 1. Here the session holds a
        // 24 MiB record.
        //
        // A single big record is the right fixture HERE (and not in the read
        // test): the header plan keeps every record after the first verbatim, so
        // the rewrite pipes them straight through without assembling one — the
        // fat record is not merely cheap, it never becomes a record. What IS
        // assembled is the head, and the head is the session header: one line.
        // So the memory is bounded by the header, not by the file.
        var (store, sessionId) = await SeedAsync(1);
        try
        {
            // Baseline FIRST, on the same session while it is still small: the
            // fixed cost of a rename (temp file, two FileStreams, the block) with
            // none of the bulk involved.
            long smallBaseline = await MeasureHeaderRewriteAsync(store, sessionId, "baseline");

            const int payloadBytes = 24 * 1024 * 1024;
            await AppendFatMessageAsync(store, sessionId, "fat", payloadBytes);

            long allocated = await MeasureHeaderRewriteAsync(store, sessionId, "renamed by #460");

            Console.WriteLine(
                $"jsonl-460: header rewrite = {smallBaseline / 1024} KiB on a small session, " +
                $"{allocated / 1024} KiB on a 24 MiB one");

            // Asserted RELATIVE to the baseline, not against a magic absolute
            // number. What #460 changed is the SCALING, and a fixed budget would
            // mostly be measuring the platform's FileStream and ArrayPool
            // overhead — a few MiB, and it moves between runs. The old shape put
            // a second copy of the 24 MiB file on top of that overhead; this one
            // puts at most a couple of MiB on top, and the gate says exactly
            // that: growing the file 3 000x must not grow the cost with it.
            await Assert.That(allocated - smallBaseline).IsLessThan(2 * MiB);

            // And every record survived the rewrite — the count first, because a
            // byte-piping bug shows up as a missing record, not as a wrong one.
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

    /// <summary>
    ///     What one header rename costs before any of the session's bulk is
    ///     involved: the fixed overhead of the temp file, the two FileStreams
    ///     and the block. The big-session measurement is compared against this,
    ///     so the assertion is about the file's contribution and nothing else.
    /// </summary>
    private static async Task<long> MeasureHeaderRewriteAsync(
        JsonlSessionStore store, string sessionId, string title)
    {
        var session = await store.GetAsync(sessionId);
        if (!session.IsSuccess)
        {
            throw new InvalidOperationException($"session lookup failed: {session.Error}");
        }

        var renamed = session.Value with { Title = title };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var updated = await store.UpdateAsync(renamed);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        if (!updated.IsSuccess)
        {
            throw new InvalidOperationException($"header rewrite failed: {updated.Error}");
        }

        return allocated;
    }

    [Test]
    public async Task UpdateMessage_SeveralMessagesWithTheSameId_DropsThemAll()
    {
        // The read path is "latest id wins", so a file can legitimately hold
        // several records for one id (an update that raced an append). Every one
        // of them is stale, so every one of them has to go — a plan that dropped
        // only the first would leave a duplicate that the reader then silently
        // picks between.
        var (store, sessionId) = await SeedAsync(3);
        try
        {
            string path = Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");

            // Three more records for the seeded m1. The reader collapses these
            // to one m1 (latest wins), so the file really does hold four.
            await AppendFatMessageAsync(store, sessionId, "m1", 11);
            await AppendFatMessageAsync(store, sessionId, "m1", 22);
            await AppendFatMessageAsync(store, sessionId, "m1", 33);

            var updated = await store.UpdateMessageAsync(sessionId, new UserMessage(
                "m1", sessionId, Epoch.AddSeconds(99), "final body", "code", "claude-opus-4"));
            await Assert.That(updated.IsSuccess).IsTrue();

            var reread = await store.GetMessagesAsync(sessionId);
            await Assert.That(reread.IsSuccess).IsTrue();

            // Exactly one m1 — all four stale ones went, not just the first —
            // and it is the one we just wrote.
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
            // A second record for m1, at the END — past m2 and m3. Anchoring on
            // the first m1 therefore drops m2, m3 and this duplicate, while
            // anchoring on the last one would drop nothing. 3 vs 0 is the
            // whole difference between the two readings.
            await AppendFatMessageAsync(store, sessionId, "m1", 8);

            var result = await store.DeleteMessagesAfterAsync(sessionId, "m1");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo(3); // m2, m3, and the duplicate

            var reread = await store.GetMessagesAsync(sessionId);
            await Assert.That(reread.IsSuccess).IsTrue();

            // The prefix up to and including the FIRST m1, and nothing after.
            await Assert.That(reread.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1" });
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
