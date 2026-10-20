// SessionFileReaderScalingTests.cs — #1533, the RELATIVE gate over the JSONL
// read path ('#97' slice) plus the ordering-semantics pins that make it safe.
//
// #1533: SessionFileReader.ParseMessagesFromDiskAsync finished EVERY read with
//     messages.Values.OrderBy(m => m.CreatedAt).ToList()
// — a full O(n log n) stable sort plus a second O(n) list per parse, on the
// per-turn read path: JsonlSessionStore's cache is keyed on the file's
// (mtime, length) and invalidates on every appended turn, so a run that
// appends re-parses on every turn and paid the sort every one of them. The
// reader now collects records in arrival order (same "latest id wins,
// first-occurrence slot" semantics the dictionary fed the OrderBy with — see
// the MessageInbox doc in the source) and proves ordering with ONE comparison
// pass; the stable sort runs exactly once, on the first provably
// out-of-order read.
//
// The GATE is the #410/#1528 convention, ridden on the shared TestScalingGate
// skeleton (#1652): N against 2N inside one run, no absolute budgets. The
// unit here is one FULL read of a session file (parse + ordering — the shape
// a per-turn cache miss pays). The small leg reads an N-message file, the
// large leg reads a 2N-message file, and both loop the same `units` reads.
//
//     t(parse+order of 2N) <= 3 * t(parse+order of N)      (time, plus the #1652 1.05 band)
//     alloc(2N)            <= 3 * alloc(N)                  (allocations, strict)
//
// Linear is 2.00 and super-linear machinery is 3+; absolute columns print for
// information only (they feed docs/BENCHMARKS.md), same non-convention as the
// #410 rows there. The pre-#1533 ordering shape runs as a REPORT-ONLY
// companion (parse through the NEW reader, then the full stable sort
// re-applied on top of the result — exactly the ordering work the old reader
// did after every parse) so the BENCHMARKS.md before/after rows are measured
// in the same CI run. It is never asserted.
//
// The three semantics tests below pin the replacement against the old
// Dictionary behaviour the OrderBy used to ride on: arrival order is
// chronological order; an out-of-order file still arrives sorted; a
// repeated id keeps its first-occurrence position with its latest value.

using System.Diagnostics;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Storage.Jsonl.Tests;

// The `[NotInParallel]` is the BARE form, and it is load-bearing (#1790). The
// gate reads wall-clock elapsed, and elapsed time is not thread-local: a
// neighbouring test burning cores — or, under the server GC every CI job
// enables, a collection that pauses this thread mid-window — decides the
// ratio. Run 38129838108 (PR #1285) is the receipt: the same tree measured
// time x3.10 against the x3.00 limit while the same run measured alloc x1.99
// — linear memory with an inflated stopwatch, on a tree that had passed the
// identical binary forty minutes earlier. A NAMED key would not help: it
// excludes only same-key peers, and every other class in this assembly still
// runs alongside. See docs/TEST_PATTERNS.md §Parallelism.
[NotInParallel]
public class SessionFileReaderScalingTests
{
    /// <summary>Messages in the small leg's file; the large leg doubles it.</summary>
    private const int Messages = 2_000;

    /// <summary>
    ///     Messages in the file the cost gate MEASURES - the larger one, so a
    ///     per-read cost that grows with the message count lands in the
    ///     measurement instead of hiding behind the smaller file.
    /// </summary>
    private const int MeasuredMessages = Messages * 2;

    /// <summary>
    ///     Full file reads per measured leg - the FIXED amount of work. 8
    ///     reads of a 4 000-message file is about 77 ms of real work on the
    ///     CI runner.
    /// </summary>
    private const int ReadsPerLeg = 8;

    /// <summary>
    ///     Allocation budget for ONE full file read, in bytes. Machine-
    ///     independent, so it is compared as measured, with no band. Pinned
    ///     from the CI measurement of the ratio gate this replaced (run
    ///     39423151864): 16 583 576 B over 13 reads of a 4 000-message file =
    ///     1 275 659 B per read. The budget leaves ~1.25x headroom.
    /// </summary>
    private const long MaxBytesPerRead = 1_600_000;

    /// <summary>
    ///     Time budget for ONE full file read, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the same run: 124.699 ms over 13 reads = 9.59 ms per
    ///     read against a probe that costs 0.000380 ms/unit, i.e. ~25 200 probe
    ///     units per read.
    ///     The budget is wide on purpose: the probe's OWN measured cost moved
    ///     5.3x between two CI runs of the SAME tree (0.000380 ms/unit in run
    ///     39423151864, 0.000072 ms/unit in run 39436405496), so the
    ///     probe-unit currency is only as stable as the yardstick behind it.
    ///     The BYTE budget is the branch that can fail on any runner - it is
    ///     exact - and the time budget is the gross-regression detector.
    /// </summary>
    private const double MaxProbeUnitsPerRead = 300_000.0;

    /// <summary>Full file reads where the leg-scaling probe starts; one read is a few ms over this size.</summary>
    private const int InitialReads = 8;

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int second) => Epoch.AddSeconds(second);

    // =====================================================================
    // 1. The gate.
    // =====================================================================

    /// <summary>
    ///     The relative read-path gate: parse + arrival-order ordering over an
    ///     N-message versus a 2N-message session file.
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task ReadPath_ParsePlusOrdering_DoesNotScaleSuperLinearlyWithHistory()
    {
        string dir = TestTempDirs.NewDirectory("harbor-1533-reader-gate");
        try
        {
            string smallFile = await WriteSessionFileAsync(
                Path.Combine(dir, $"small-{Messages}.jsonl"), CreateSessionId(), Messages);
            string largeFile = await WriteSessionFileAsync(
                Path.Combine(dir, $"large-{MeasuredMessages}.jsonl"), CreateSessionId(), MeasuredMessages);

            // THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It
            // used to be `t(2N) <= 3*t(N)` and `alloc(2N) <= 3*alloc(N)` over a
            // 2 000-message against a 4 000-message file, decided by the shared
            // TestScalingGate (#1652), whose leg SCALER chose how much work a
            // leg carried. It is now:
            //
            //     bytes per full read   <= 1 600 000   (machine-independent, HARD)
            //     probe units per read   <= 300 000     (machine-scaled time, SOFT)
            //
            // measured over a FIXED amount of work (8 full reads of the
            // 4 000-message file) on the SAME runner in the SAME run. The
            // allocation branch is machine-independent and is asserted hard;
            // the time branch is in PROBE UNITS and stands down when the rounds
            // do not reproduce each other. The report prints on every run,
            // pass or fail.
            //
            // A re-emergent per-read O(n log n) sort - the #1533 shape -
            // allocates a second list per read and lands far above the byte
            // budget.
            PerfGateResult fresh = await PerfGate.MeasureAsync(
                work: reads => MeasureReadLegAsync(reads, largeFile),
                units: ReadsPerLeg,
                options: new PerfGateOptions
                {
                    Label = "session-read-cost",
                    MaxBytesPerUnit = MaxBytesPerRead,
                    MaxProbeUnitsPerUnit = MaxProbeUnitsPerRead,
                });

            await fresh.AssertWithinBudgetsAsync("jsonl session file read per read");

            // Non-vacuity: the files are what the ratios claim they are, and
            // the reads the gate measured are whole reads.
            var small = await SessionFileReader.ParseMessagesFromDiskAsync(smallFile, "s", NullLogger.Instance, CancellationToken.None);
            var large = await SessionFileReader.ParseMessagesFromDiskAsync(largeFile, "s", NullLogger.Instance, CancellationToken.None);
            await Assert.That(small.IsSuccess).IsTrue();
            await Assert.That(large.IsSuccess).IsTrue();
            await Assert.That(small.Value.Messages.Count).IsEqualTo(Messages);
            await Assert.That(large.Value.Messages.Count).IsEqualTo(MeasuredMessages);
            await Assert.That(small.Value.IsStable).IsTrue()
                .Because("the gate measures whole reads; a racing-file measurement is not what this gate grades");

            // REPORT-ONLY: the pre-#1533 ordering shape (the same parse, with
            // the full stable sort re-applied on its result — the ordering
            // work the old read did every time). Feeds docs/BENCHMARKS.md,
            // never gates.
            TestScalingGateResult legacy = await TestScalingGate.MeasureAsync(
                measureSmallLeg: units => MeasureLegacyReadLegAsync(units, smallFile),
                measureLargeLeg: units => MeasureLegacyReadLegAsync(units, largeFile),
                initialUnits: InitialReads,
                options: new TestScalingGateOptions
                {
                    Label = "session-read#1533-legacy",
                });

            TestContext.Current?.Output.WriteLine(legacy.Report);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    // =====================================================================
    // 2. The semantics the ordering fast path has to preserve.
    // =====================================================================

    /// <summary>An append-linear file already is chronological: arrival order out.</summary>
    [Test]
    [Timeout(30_000)]
    public async Task InOrderRecords_ParseInArrivalOrder_WithoutAnyResort()
    {
        string sessionId = CreateSessionId();
        string file = await WriteSessionFileAsync(
            Path.Combine(TestTempDirs.NewDirectory("harbor-1533-inorder"), "inorder.jsonl"), sessionId, 5);

        var read = await SessionFileReader.ParseMessagesFromDiskAsync(file, sessionId, NullLogger.Instance, CancellationToken.None);
        await Assert.That(read.IsSuccess).IsTrue();

        // TUnit's IsEqualTo on collections compares by reference (both
        // List<string> and string[] degrade to reference equality — CI logs
        // show "same contents, different reference" for both), and
        // IsEquivalentTo ignores order — useless for an order proof. Assert
        // the order per index instead.
        string[] ids = read.Value.Messages.Select(m => m.Id).ToArray();
        await Assert.That(ids.Length).IsEqualTo(5);
        await Assert.That(ids[0]).IsEqualTo("0");
        await Assert.That(ids[1]).IsEqualTo("1");
        await Assert.That(ids[2]).IsEqualTo("2");
        await Assert.That(ids[3]).IsEqualTo("3");
        await Assert.That(ids[4]).IsEqualTo("4");
    }

    /// <summary>
    ///     Records written out of time order still come out chronological —
    ///     the slow arm the one-pass proof exists to skip, reachable only on
    ///     the read that detects disorder.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task OutOfOrderRecords_AreSortedChronologically()
    {
        string sessionId = CreateSessionId();
        string dir = TestTempDirs.NewDirectory("harbor-1533-outoforder");
        try
        {
            string file = Path.Combine(dir, "outoforder.jsonl");
            var records = new (string Id, int Second, string Payload)[]
            {
                ("3", 4, "payload 3"),
                ("0", 1, "payload 0"),
                ("2", 3, "payload 2"),
                ("1", 2, "payload 1"),
            };

            await File.WriteAllBytesAsync(file, ToLineBytes(sessionId, ToDated(records)));

            var read = await SessionFileReader.ParseMessagesFromDiskAsync(file, sessionId, NullLogger.Instance, CancellationToken.None);
            await Assert.That(read.IsSuccess).IsTrue();
            // Order proof per index (IsEqualTo is reference-based on
            // collections in this TUnit version — see the comment in
            // InOrderRecords_ParseInArrivalOrder_WithoutAnyResort).
            string[] sorted = read.Value.Messages.Select(m => m.Id).ToArray();
            await Assert.That(sorted.Length).IsEqualTo(4);
            await Assert.That(sorted[0]).IsEqualTo("0");
            await Assert.That(sorted[1]).IsEqualTo("1");
            await Assert.That(sorted[2]).IsEqualTo("2");
            await Assert.That(sorted[3]).IsEqualTo("3");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>
    ///     A repeated id keeps the FIRST-occurrence slot with its LATEST
    ///     value — the exact Dictionary+OrderBy order the read path handed
    ///     out before #1533. Here the latest value's own timestamp is
    ///     out-of-order for slot 0, so the read must take the sort arm, and
    ///     the chronological result must still be the old shape:
    ///     [1 (2 s), 0′ (3 s)].
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task RepeatedId_LatestValueWins_AtTheFirstInsertionPosition()
    {
        string sessionId = CreateSessionId();
        string dir = TestTempDirs.NewDirectory("harbor-1533-dupid");
        try
        {
            string file = Path.Combine(dir, "dup.jsonl");
            var records = new (string Id, int Second, string Payload)[]
            {
                ("0", 1, "first write"),
                ("1", 2, "second message"),
                ("0", 3, "latest write"),
            };

            await File.WriteAllBytesAsync(file, ToLineBytes(sessionId, ToDated(records)));

            var read = await SessionFileReader.ParseMessagesFromDiskAsync(file, sessionId, NullLogger.Instance, CancellationToken.None);
            await Assert.That(read.IsSuccess).IsTrue();

            var messages = read.Value.Messages.ToList();
            await Assert.That(messages.Count).IsEqualTo(2);
            string[] winners = messages.Select(m => m.Id).ToArray();
            await Assert.That(winners.Length).IsEqualTo(2);
            await Assert.That(winners[0]).IsEqualTo("1");
            await Assert.That(winners[1]).IsEqualTo("0");
            await Assert.That(((UserMessage)messages[1]).Content).IsEqualTo("latest write")
                .Because("latest id wins, exactly as the Dictionary the old read used did");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    // ---- helpers -------------------------------------------------------

    private static string CreateSessionId() => $"sess-{Guid.NewGuid():N}";

    private static IEnumerable<(string Id, DateTimeOffset CreatedAt, string Payload)> ToDated(
        (string Id, int Second, string Payload)[] records) =>
        records.Select(r => (r.Id, At(r.Second), r.Payload));

    /// <summary>
    ///     Encodes what <c>AppendMessageAsync</c> writes, one chronological
    ///     record per message — the exact line format the reader parses.
    /// </summary>
    private static async Task<string> WriteSessionFileAsync(string path, string sessionId, int messageCount)
    {
        var records = new (string Id, DateTimeOffset CreatedAt, string Payload)[messageCount];
        for (int i = 0; i < messageCount; i++)
        {
            records[i] = ($"{i}", At(i + 1), $"message {i}");
        }

        await File.WriteAllBytesAsync(path, ToLineBytes(sessionId, records));
        return path;
    }

    private static byte[] ToLineBytes(
        string sessionId,
        IEnumerable<(string Id, DateTimeOffset CreatedAt, string Payload)> records)
    {
        var buffer = new MemoryStream();
        foreach (var (id, createdAt, payload) in records)
        {
            var message = new UserMessage(id, sessionId, createdAt, payload, "code", "test-model");
            var entry = new MessageEntry(
                "message",
                message.Id,
                message.ParentId,
                message.Role,
                message.CreatedAt,
                JsonlMessageCodec.SerializeMessagePayload(message));
            buffer.Write(SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry));
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> full reads of the
    ///     session file. The wall clock and the process-wide allocation probe
    ///     wrap the SAME window, so both columns answer for the same reads.
    /// </summary>
    private static async Task<PerfGateLeg> MeasureReadLegAsync(int units, string file)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        (int ReadCount, long Allocated) = await AllocationProbe.MeasureProcessAsync(async () =>
        {
            int readCount = 0;
            for (int u = 0; u < units; u++)
            {
                var result = await SessionFileReader.ParseMessagesFromDiskAsync(file, "s", NullLogger.Instance, CancellationToken.None);
                if (result.IsSuccess)
                {
                    readCount += result.Value.Messages.Count;
                }
            }

            return readCount;
        });
        sw.Stop();

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, Allocated);
    }

    /// <summary>
    ///     The pre-#1533 ordering shape for the report-only legs: the same
    ///     reads, each followed by the full stable sort the old read
    ///     re-applied after every parse.
    /// </summary>
    private static async Task<TestScalingGateLeg> MeasureLegacyReadLegAsync(int units, string file)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        (_, long Allocated) = await AllocationProbe.MeasureProcessAsync(async () =>
        {
            for (int u = 0; u < units; u++)
            {
                var result = await SessionFileReader.ParseMessagesFromDiskAsync(file, "s", NullLogger.Instance, CancellationToken.None);
                if (result.IsSuccess)
                {
                    _ = result.Value.Messages.OrderBy(m => m.CreatedAt).ToList();
                }
            }

            return 0;
        });
        sw.Stop();

        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, Allocated);
    }
}
