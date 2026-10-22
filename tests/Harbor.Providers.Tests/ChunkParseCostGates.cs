extern alias CompatWire;

using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using CompatOpenAiWire = CompatWire::Harbor.Providers.Internal.OpenAiWire;
using CompatChunkState = CompatWire::Harbor.Providers.Internal.ChunkStreamState;

namespace Harbor.Providers.Tests;

/// <summary>
///     Cost gate over the CHUNK-PARSE step of the token path — the step
///     every token of every answer goes through.
/// </summary>
/// <remarks>
///     <para>
///         A chunk arrives per streamed token, and the parser used to build a
///         fresh <see cref="string" /> for EVERY property name it walked
///         past, only to compare that string against a handful of literals
///         ("choices", "delta", "content", …). A chat-completions chunk
///         carries nine such names, so the dispatch alone cost nine
///         throwaway strings per token — the single largest allocation on the
///         hottest line of the product.
///     </para>
///     <para>
///         The gate is a BUDGET PER UNIT OF WORK (#1948), not a ratio:
///         <code>
///         bytes per parsed chunk &lt;= 1 200   (machine-independent, HARD)
///         probe units per chunk    &lt;= 100   (machine-scaled time, SOFT)
///         </code>
///         measured over a FIXED amount of work (20 000 chunks per leg) on the
///         SAME runner in the SAME run. The allocation branch is
///         machine-independent and is asserted hard; the time branch is in
///         PROBE UNITS and stands down when the rounds do not reproduce each
///         other. The report prints on every run, pass or fail.
///     </para>
///     <para>
///         The measured unit is the PRODUCT entry point
///         (<c>TryParseChatChunkLine</c>), not an internal seam, so the
///         number is the cost of parsing one chunk as the product parses it.
///         The SSE line's own string is the pump's documented allowance and
///         is built once, outside the measured window.
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
public class ChunkParseCostGates
{
    /// <summary>
    ///     Chunks parsed per measured leg — the FIXED amount of work. 20 000
    ///     chunks is a full minute of streamed text, and about 40 ms of real
    ///     work on the CI runner: long enough for the round-to-round spread
    ///     to mean something, short enough not to dominate the shard.
    /// </summary>
    private const int ChunksPerLeg = 20_000;

    /// <summary>
    ///     Allocation budget for ONE parsed chunk, in bytes.
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
    private const long MaxBytesPerChunk = 1_200;

    /// <summary>
    ///     Time budget for ONE parsed chunk, in PROBE UNITS (see
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
    private const double MaxProbeUnitsPerChunk = 100.0;

    /// <summary>
    ///     A chat-completions chunk of the shape a provider actually sends:
    ///     nine property names, one text delta, one null finish reason.
    /// </summary>
    private const string ChunkJson =
        """{"id":"chatcmpl-B1","object":"chat.completion.chunk","created":1767225600,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{"content":"Hello there"},"finish_reason":null}]}""";

    [Test]
    [Timeout(300_000)]
    public async Task ParseChatChunk_CostPerChunk_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var state = new CompatChunkState();

        // Non-vacuity, warmed: the chunk really does parse into one text
        // delta, and nothing is counted as malformed. A gate over a chunk
        // the parser silently drops is the #591 shape.
        var first = CompatOpenAiWire.TryParseChatChunkLine(ChunkJson, state, NullLogger.Instance);
        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(state.MalformedChunks).IsEqualTo(0);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: chunks => Task.FromResult(Measure(state, chunks)),
            units: ChunksPerLeg,
            options: new PerfGateOptions
            {
                Label = "chunk-parse-cost",
                MaxBytesPerUnit = MaxBytesPerChunk,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerChunk,
            });

        await result.AssertWithinBudgetsAsync("chat chunk parse per chunk");
    }

    /// <summary>
    ///     One measured leg: <paramref name="chunks" /> parses of the same
    ///     chunk, timed and allocation-counter'd as one window. The byte
    ///     count comes from <see cref="AllocationProbe.MeasureThreadLeg" /> —
    ///     the repository's one sanctioned reader of the per-thread counter.
    /// </summary>
    private static PerfGateLeg Measure(CompatChunkState state, int chunks) =>
        AllocationProbe.MeasureThreadLeg(() =>
        {
            for (int i = 0; i < chunks; i++)
            {
                _ = CompatOpenAiWire.TryParseChatChunkLine(ChunkJson, state, NullLogger.Instance);
            }
        });
}
