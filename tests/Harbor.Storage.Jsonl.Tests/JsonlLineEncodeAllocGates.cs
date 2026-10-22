// JsonlLineEncodeAllocGates.cs — #97: the cost of writing ONE JSONL record.
//
// WHAT IS MEASURED
// ----------------
// The session log is written on the append path of every turn: the assistant
// message, then the tool-result message. `SessionFileIO.EncodeLine` is the one
// place a record becomes bytes on disk, and before #97 it allocated the bytes
// TWICE — the serializer handed back an exact-size array, and the encoder
// allocated a second array of length+1 to carry the same bytes plus the LF.
//
// A tool result is allowed to carry hundreds of kilobytes (the output cap is
// 200 000 chars), so the second copy is not a rounding error: it is the
// biggest single allocation of a turn with a large tool result.
//
// THE GATE (#1747 discipline)
// ---------------------------
//     bytes per encoded line        <= <budget>   (machine-independent, HARD)
//     probe units per encoded line  <= <budget>   (machine-scaled time, SOFT)
//
// The byte branch is the load-bearing one: a byte count does not move with the
// runner's speed, so it is compared as measured, with no band. The time branch
// is written in PROBE UNITS — the cost of one unit of work divided by the cost
// of one unit of the fixed calibration probe measured on the SAME runner in the
// SAME run — so the runner's speed cancels and the budget is a constant in a
// machine-independent currency.
//
// [NotInParallel] — the allocation counter is read inside a synchronous window,
// and the encoder's scratch buffer is per-thread, so the measurement must not
// share a thread with another measurement.

using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using TUnit.Core;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     The JSONL line encoder's cost per record, and the proof that the record
///     on disk did not change while the cost did.
/// </summary>
[NotInParallel("jsonl-line-encode")]
public sealed class JsonlLineEncodeAllocGates
{
    /// <summary>
    ///     Characters of tool output the encoded record carries — the shape a
    ///     real turn has after a <c>read</c> or a <c>bash</c> call. 32 KiB is
    ///     a small tool output; the cap is 200 000 chars, so the waste the old
    ///     shape paid scaled with the output.
    /// </summary>
    private const int ToolOutputChars = 32 * 1024;

    /// <summary>Records encoded per measured window.</summary>
    private const int LinesPerWindow = 200;

    // ── Budgets: bytes per unit of work (machine-independent, HARD) ─────────

    /// <summary>
    ///     Byte budget for ONE encoded record. Pinned from the CI measurement
    ///     of the PRE-#97 tree (the "before" column of the #97 report): the
    ///     old shape allocated the record's bytes TWICE, so the budget sits
    ///     BELOW the old cost and ABOVE what one copy leaves behind.
    /// </summary>
    private const long MaxBytesPerLine = 40 * 1024;

    // ── Time budgets: probe units per unit of work (machine-scaled, SOFT) ───

    /// <summary>Time budget for ONE encoded record, in probe units.</summary>
    private const double MaxProbeUnitsPerLine = 2_000.0;

    /// <summary>
    ///     The floor below which a measured leg is not evidence. A record of
    ///     32 KiB is milliseconds of work, so the shared 5 ms default is about
    ///     right; 1 ms is the shortest leg that still decides something.
    /// </summary>
    private const double MinLegMs = 1.0;

    /// <summary>
    ///     A tool-result message whose payload carries a 32 KiB tool output —
    ///     the record shape the budget is pinned against.
    /// </summary>
    private static MessageEntry Entry()
    {
        string output = new('x', ToolOutputChars);
        var message = TestMessages.ToolResult(
            [new ToolResultEntry("tc_1", "read", output, IsError: false)],
            id: "m1",
            sessionId: "sess",
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        return new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));
    }

    /// <summary>
    ///     Drives one window: encode the same record <paramref name="lines" />
    ///     times, timed and allocation-counter'd as one window.
    /// </summary>
    private static PerfGateLeg Measure(MessageEntry entry, int lines)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] last = [];
        for (int i = 0; i < lines; i++)
        {
            last = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The window must have encoded the record it claims to measure.
        if (last.Length == 0 || last[^1] != (byte)'\n')
        {
            throw new InvalidOperationException(
                $"Line-encode script drifted: the last encoded record is {last.Length} byte(s) and does not end with LF.");
        }

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    [Test]
    [Timeout(300_000)]
    public async Task EncodeLine_CostPerRecord_StaysWithinBudget() =>
        await RunGate("encoded JSONL record carrying a 32 KiB tool output", LinesPerWindow,
            Gate("jsonl-line-encode-cost", MaxBytesPerLine, MaxProbeUnitsPerLine), DriveEncodeCost);

    private static PerfGateLeg DriveEncodeCost(int lines) => Measure(Entry(), lines);

    // ── The behaviour proof: the bytes on disk did not change ───────────────

    /// <summary>
    ///     The encoder must produce BYTE-FOR-BYTE what the pre-#97 shape
    ///     produced: the serializer's own UTF-8 bytes, plus the LF.
    /// </summary>
    /// <remarks>
    ///     The oracle is the old implementation itself, written out here: the
    ///     exact-size byte array the serializer hands back, and the second
    ///     array that carried it plus the newline. If the retained scratch
    ///     buffer ever leaked a previous record's bytes, or the encoder wrote
    ///     the LF in the wrong place, or the encoder's options changed the
    ///     escaping, this test goes red — the session log is a file other
    ///     versions of Harbor read.
    /// </remarks>
    [Test]
    public async Task EncodeLine_ProducesThePre97Bytes()
    {
        MessageEntry entry = Entry();

        byte[] actual = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(entry, JsonlCodecContext.Default.MessageEntry);
        var expected = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, expected, 0, payload.Length);
        expected[payload.Length] = (byte)'\n';

        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    /// <summary>
    ///     The encoder is called with DIFFERENT records in one process, so the
    ///     retained scratch buffer must not carry one record's bytes into the
    ///     next. A big record followed by a small one must produce the small
    ///     record's bytes and nothing else.
    /// </summary>
    [Test]
    public async Task EncodeLine_AfterABiggerRecord_CarriesNoTailOfIt()
    {
        MessageEntry big = Entry();

        var small = new MessageEntry(
            "message",
            "m2",
            null,
            "user",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            JsonlMessageCodec.SerializeMessagePayload(
                TestMessages.User("hi", id: "m2", sessionId: "sess")));

        // The big record first: it grows the scratch buffer to its high-water
        // mark, so the small record that follows is written into a buffer that
        // still holds the big one's bytes.
        _ = SessionFileIO.EncodeLine(big, JsonlCodecContext.Default.MessageEntry);
        byte[] smallLine = SessionFileIO.EncodeLine(small, JsonlCodecContext.Default.MessageEntry);

        byte[] smallPayload = JsonSerializer.SerializeToUtf8Bytes(small, JsonlCodecContext.Default.MessageEntry);
        var smallExpected = new byte[smallPayload.Length + 1];
        Buffer.BlockCopy(smallPayload, 0, smallExpected, 0, smallPayload.Length);
        smallExpected[smallPayload.Length] = (byte)'\n';

        await Assert.That(smallLine.Length).IsEqualTo(smallExpected.Length)
            .Because(
                "the small record's line must be exactly the small record's bytes plus the LF; a longer "
                + "line means the scratch buffer carried a tail of the previous (bigger) record.");
        await Assert.That(smallLine).IsEquivalentTo(smallExpected);
    }

    // ── The shared gate shape ────────────────────────────────────────────────

    /// <summary>
    ///     The budget shape every gate here shares: a constant per unit of
    ///     work, the allocation branch asserted hard and the time branch
    ///     asserted only when the measurement is reproducible.
    /// </summary>
    private static PerfGateOptions Gate(string label, long maxBytesPerUnit, double maxProbeUnitsPerUnit) =>
        new()
        {
            Label = label,
            MaxBytesPerUnit = maxBytesPerUnit,
            MaxProbeUnitsPerUnit = maxProbeUnitsPerUnit,
            RequirePositiveBytes = true,
            MinMeasurableLegMs = MinLegMs,
        };

    /// <summary>
    ///     Measure → report → assert. The report is printed before the
    ///     assertions run, so a red gate still leaves the numbers that
    ///     produced it in the log.
    /// </summary>
    private static async Task RunGate(
        string topic,
        int units,
        PerfGateOptions options,
        Func<int, PerfGateLeg> work)
    {
        PerfGateResult result = await PerfGate.MeasureAsync(
            work: u => Task.FromResult(work(u)),
            units: units,
            options: options);

        await result.AssertWithinBudgetsAsync(topic);
    }
}
