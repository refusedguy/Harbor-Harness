using Harbor.Abstractions.Models;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Cost gate over the JSONL APPEND path — the step that puts a message on
///     disk, and the one every turn of every session goes through.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="SessionFileIO.EncodeLine" /> used to ask the
///         serializer for the payload bytes and then copy them into a SECOND
///         array, one byte longer, to carry the newline: two allocations and
///         a full copy of the payload for every message the log keeps. The
///         payload is now written into a pooled buffer and copied once, into
///         the array that goes to disk.
///     </para>
///     <para>
///         The gate is a BUDGET PER UNIT OF WORK (#1948), not a ratio:
///         <code>
///         bytes per encoded line &lt;= 2 000   (machine-independent, HARD)
///         probe units per line    &lt;= 150   (machine-scaled time, SOFT)
///         </code>
///         measured over a FIXED amount of work (20 000 lines per leg) on the
///         SAME runner in the SAME run. The allocation branch is
///         machine-independent and is asserted hard; the time branch is in
///         PROBE UNITS and stands down when the rounds do not reproduce each
///         other. The report prints on every run, pass or fail.
///     </para>
///     <para>
///         The measured unit is the encoder itself, not the file write: the
///         write's cost is the file system's, and no CPU yardstick scales
///         it — the byte budget is what this path is about.
///     </para>
///     <para>
///         BARE <see cref="NotInParallelAttribute" />: the byte counter is
///         per-thread and the window is synchronous, so a neighbour cannot
///         move it — but the STOPWATCH is not thread-local, and a neighbour
///         burning cores slows the window. Exclusive execution is what makes
///         the time half a measurement (the StorePathScalingGates lesson).
///     </para>
/// </remarks>
[NotInParallel]
public class JsonlLineEncodeCostGates
{
    /// <summary>
    ///     Lines encoded per measured leg — the FIXED amount of work. 20 000
    ///     lines is a long session's worth of messages, and about 40 ms of
    ///     real work on the CI runner.
    /// </summary>
    private const int LinesPerLeg = 20_000;

    /// <summary>
    ///     Allocation budget for ONE encoded line, in bytes.
    ///     Machine-independent, so it is compared as measured, with no band.
    ///     <para>
    ///         The number is pinned from a CI MEASUREMENT of the tree this
    ///         gate was introduced on (the pre-fix tree), and it is stated
    ///         with the run number in the commit that pins it. The budget
    ///         carries the headroom the house recipe asks for over that
    ///         measurement — the byte count does not move with the runner's
    ///         speed, so it is the branch that can be a hard gate on any
    ///         machine.
    ///     </para>
    /// </summary>
    private const long MaxBytesPerLine = 2_000;

    /// <summary>
    ///     Time budget for ONE encoded line, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     <para>
    ///         The budget is wide on purpose: the probe's OWN measured cost
    ///         moved 5.3x between two CI runs of the SAME tree, so the
    ///         probe-unit currency is only as stable as the yardstick behind
    ///         it. The BYTE budget is the branch that can fail on any runner
    ///         — it is exact — and the time budget is the gross-regression
    ///         detector.
    ///     </para>
    /// </summary>
    private const double MaxProbeUnitsPerLine = 150.0;

    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     One user message's entry — the shape the append path encodes on
    ///     every turn.
    /// </summary>
    private static MessageEntry Entry()
    {
        var message = TestMessages.User("hello", id: "m1", sessionId: "sess", createdAt: CreatedAt, model: "p/m");
        return new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));
    }

    [Test]
    [Timeout(300_000)]
    public async Task EncodeLine_CostPerMessage_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        MessageEntry entry = Entry();

        // Non-vacuity, warmed: the line really is a JSON object terminated by
        // a single LF. A gate over an encoder that produced nothing would
        // read as a cheap path rather than as a broken one.
        byte[] line = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
        await Assert.That(line.Length).IsGreaterThan(1);
        await Assert.That(line[^1]).IsEqualTo((byte)'\n');
        await Assert.That(line.Contains((byte)'\r')).IsFalse();

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: lines => Task.FromResult(Measure(entry, lines)),
            units: LinesPerLeg,
            options: new PerfGateOptions
            {
                Label = "jsonl-line-encode-cost",
                MaxBytesPerUnit = MaxBytesPerLine,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerLine,
            });

        await result.AssertWithinBudgetsAsync("jsonl message line encoding per message");
    }

    /// <summary>
    ///     One measured leg: <paramref name="lines" /> encodings of the same
    ///     entry, timed and allocation-counter'd as one window. The byte
    ///     count comes from <see cref="AllocationProbe.MeasureThreadLeg" /> —
    ///     the repository's one sanctioned reader of the per-thread counter.
    /// </summary>
    private static PerfGateLeg Measure(MessageEntry entry, int lines) =>
        AllocationProbe.MeasureThreadLeg(() =>
        {
            for (int i = 0; i < lines; i++)
            {
                _ = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
            }
        });
}
