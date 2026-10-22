// StreamingFlushAllocGates.cs — #97: the cost of ONE streaming-text flush.
//
// WHAT IS MEASURED
// ----------------
// The agent's hottest unit is the DELTA: a provider streams a message in
// pieces, and every piece lands in the UI state as a `MessageUpdateEvent`.
// The text accumulates in a `ChunkedBuffer` (O(1) push) and is materialized
// into the synced display string when the flush policy says so.
//
// Before #97 that flush allocated the pending text TWICE: `ChunkedBuffer.
// Materialize` built a `string[]` of the chunks and handed it to
// `string.Concat`, and `StreamingSync.Concat` then concatenated the prefix
// with that result — a second string, and a second copy of the pending text,
// on the per-delta path.
//
// THE GATE (#1747 discipline)
// ---------------------------
//     bytes per flush                <= <budget>   (machine-independent, HARD)
//     probe units per flush          <= <budget>   (machine-scaled time, SOFT)
//
// The byte branch is the load-bearing one: a byte count does not move with the
// runner's speed, so it is compared as measured, with no band. The time branch
// is written in PROBE UNITS — the cost of one unit of work divided by the cost
// of one unit of the fixed calibration probe measured on the SAME runner in the
// SAME run — so the runner's speed cancels.
//
// [NotInParallel] — the allocation counter is read inside a synchronous window,
// so only the time leg needs the exclusivity; both legs live in one test, so
// one attribute covers them.

using System.Diagnostics;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     The streaming-text flush's cost per delta, and the proof that the
///     flushed text did not change while the cost did.
/// </summary>
[NotInParallel("streaming-flush-cost")]
public sealed class StreamingFlushAllocGates
{
    /// <summary>
    ///     Characters in one streamed delta — the shape a provider's chunk
    ///     arrives in (a token, a word, a few words).
    /// </summary>
    private const int DeltaChars = 12;

    /// <summary>
    ///     Characters already synced when the measured flush runs. The flush
    ///     policy keeps the first <see cref="StreamingSync.ExactThresholdChars" />
    ///     characters exact — one flush per delta — and after that flushes on a
    ///     growing lag limit. 4 096 characters is past the exact threshold, so
    ///     the measured flush is the steady-state one: a prefix PLUS pending
    ///     chunks, which is the shape that cost two strings.
    /// </summary>
    private const int SyncedChars = 4_096;

    /// <summary>Flushes driven per measured window.</summary>
    private const int FlushesPerWindow = 20_000;

    // ── Budgets: bytes per unit of work (machine-independent, HARD) ─────────

    /// <summary>
    ///     Byte budget for ONE flush of a 4 096-character prefix plus a
    ///     12-character delta. The result is 4 108 characters — 8 216 bytes —
    ///     and the old shape paid for that string TWICE, plus the chunk array.
    ///     The budget sits BELOW the old cost and ABOVE what one copy leaves
    ///     behind.
    /// </summary>
    private const long MaxBytesPerFlush = 9_000;

    // ── Time budgets: probe units per unit of work (machine-scaled, SOFT) ───

    /// <summary>Time budget for ONE flush, in probe units.</summary>
    private const double MaxProbeUnitsPerFlush = 2_000.0;

    /// <summary>
    ///     The floor below which a measured leg is not evidence. A flush of a
    ///     4 KiB prefix is tens of microseconds, so the shared 5 ms default
    ///     would stand the time branch down on every run; 1 ms is the shortest
    ///     leg that still decides something on a hosted runner.
    /// </summary>
    private const double MinLegMs = 1.0;

    /// <summary>
    ///     Drives one window: flush the same prefix-plus-delta
    ///     <paramref name="flushes" /> times, timed and allocation-counter'd as
    ///     one window.
    /// </summary>
    private static PerfGateLeg Measure(string prefix, string delta, int flushes)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        string last = string.Empty;
        for (int i = 0; i < flushes; i++)
        {
            ChunkedBuffer pending = ChunkedBuffer.Empty.Append(delta);
            last = StreamingSync.Concat(prefix, pending);
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The window must have flushed the text it claims to measure.
        if (last.Length != prefix.Length + delta.Length)
        {
            throw new InvalidOperationException(
                $"Flush script drifted: the last flush is {last.Length} char(s) instead of {prefix.Length + delta.Length}.");
        }

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    [Test]
    [Timeout(300_000)]
    public async Task Flush_CostPerDelta_StaysWithinBudget() =>
        await RunGate("streaming flush of a 4 KiB prefix plus one delta", FlushesPerWindow,
            Gate("streaming-flush-cost", MaxBytesPerFlush, MaxProbeUnitsPerFlush), DriveFlushCost);

    private static PerfGateLeg DriveFlushCost(int flushes)
    {
        string prefix = new('p', SyncedChars);
        string delta = new('d', DeltaChars);
        return Measure(prefix, delta, flushes);
    }

    // ── The behaviour proof: the flushed text did not change ────────────────

    /// <summary>
    ///     The flush must produce the same characters as before #97: the
    ///     synced prefix followed by the pending chunks in INSERTION order.
    /// </summary>
    /// <remarks>
    ///     The multi-chunk case is the one the old shape got right by
    ///     accident (it filled the array from the end) and the new shape has
    ///     to get right on purpose (it fills the destination from the end): a
    ///     reversed chunk order would show up here and nowhere else.
    /// </remarks>
    [Test]
    public async Task Concat_KeepsThePrefixThenTheChunksInInsertionOrder()
    {
        ChunkedBuffer pending = ChunkedBuffer.Empty
            .Append("world")
            .Append(" ")
            .Append("hello");

        string flushed = StreamingSync.Concat("hello ", pending);

        await Assert.That(flushed).IsEqualTo("hello world hello");
    }

    /// <summary>
    ///     The materialized text on its own must be the chunks in insertion
    ///     order — the same string the old <c>string.Concat(parts)</c> shape
    ///     produced.
    /// </summary>
    [Test]
    public async Task Materialize_KeepsTheChunksInInsertionOrder()
    {
        ChunkedBuffer pending = ChunkedBuffer.Empty
            .Append("second")
            .Append("first");

        await Assert.That(pending.Materialize()).IsEqualTo("firstsecond");
    }

    /// <summary>
    ///     A single-chunk buffer materializes to the chunk ITSELF — the same
    ///     reference the old <c>string.Concat(parts)</c> one-element fast path
    ///     returned. A copy here would be a per-delta allocation the old shape
    ///     did not pay.
    /// </summary>
    [Test]
    public async Task Materialize_OfOneChunk_ReturnsThatChunk()
    {
        const string Chunk = "the only chunk";
        ChunkedBuffer pending = ChunkedBuffer.Empty.Append(Chunk);

        await Assert.That(pending.Materialize()).IsSameReferenceAs(Chunk);
    }

    /// <summary>
    ///     Nothing pending means the prefix comes back untouched — no
    ///     allocation, and the same reference.
    /// </summary>
    [Test]
    public async Task Concat_OfNothingPending_ReturnsThePrefix()
    {
        const string Prefix = "untouched";

        await Assert.That(StreamingSync.Concat(Prefix, ChunkedBuffer.Empty)).IsSameReferenceAs(Prefix);
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
