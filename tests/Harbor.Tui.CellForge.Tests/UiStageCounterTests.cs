using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
// Markdown comes from the project-wide global usings; PerformanceContracts does not.
using Harbor.Ui.Framework.Rendering.PerformanceContracts;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #46 slice 2 (#409) — the four per-stage counters, and the proof that
///     they are not vacuous.
/// </summary>
/// <remarks>
///     <para>
///         The #46 hazard is an instrument that reports a plausible 0, and #591
///         is the worked example: a check that halved its own rule still
///         passed, because the halved number was indistinguishable from the
///         right one at the scale it was read. Three properties keep these
///         assertions off that floor:
///     </para>
///     <list type="number">
///         <item><description>
///             <b>Structural hits.</b> Each assertion drives content that cannot
///             skip its stage — a fresh block at a fresh width must materialize,
///             and the markdown carries a heading, a two-item list and a fence,
///             so the parser has structure to find.
///         </description></item>
///         <item><description>
///             <b>Exact counts, never "greater than zero".</b> A stuck-at-1 or a
///             double-counting counter satisfies <c>&gt; 0</c>. Every assertion
///             names the number the code path forces, and the memo tests pin it
///             in both directions — 1 on the memo hit, 2 on the width flip — so
///             an off-by-one placement cannot pass.
///         </description></item>
///         <item><description>
///             <b>The "guard off" arm runs the identical workload.</b> A counter
///             that fired unconditionally passes the enabled tests and fails
///             <see cref="GuardOff_IdenticalWorkload_CountsNothing" />, where
///             the workload's occurrence is proved by the backend's own recorded
///             write and the cache's own tally — not by a counter.
///         </description></item>
///     </list>
///     <para>
///         <b>The bare <c>[NotInParallel]</c> is load-bearing, and it was found the hard way.</b>
///         The first run of this class used the keyed form,
///         <c>[NotInParallel("ui-stage-counters")]</c>, and four of these tests failed — all inside
///         the same millisecond. TUnit documents that a keyed attribute only excludes tests that
///         <i>share that key</i>; every other test in the assembly still runs alongside. That
///         matters because the counters are process-global <b>by contract</b> — a diagnostics
///         surface reads them from a different thread than the one that rendered, so a thread-local
///         would make the public read API useless in production — and the neighbouring CellForge
///         tests exercise the very paths under instrumentation, so any concurrent
///         <c>AnsiWriter</c> test moves <c>TerminalWrites</c>. The bare attribute is TUnit's
///         "run exclusively" form. Serializing an assembly for eight sub-millisecond tests is the
///         cheap side of that trade; the alternative is an exact-count assertion that no amount of
///         care can make deterministic.
///     </para>
/// </remarks>
[NotInParallel]
public class UiStageCounterTests
{
    /// <summary>
    ///     Markdown with real block structure. A bare paragraph can be absorbed
    ///     by a memo or a fast path; a heading, a two-item list and a fence
    ///     cannot, which is what makes the parse hit structural rather than
    ///     incidental.
    /// </summary>
    private const string StructuredMarkdown = "# heading\n\n- one\n- two\n\n```\ncode\n```\n";

    /// <summary>
    ///     Runs <paramref name="work" /> with the counters enabled and returns
    ///     what they saw. Always hands the switch back and zeroes the totals, so
    ///     one test cannot tax or contaminate the next — the #46 "leaked state"
    ///     hazard, closed by construction rather than by convention.
    /// </summary>
    private static UiStageSnapshot Counted(Action work)
    {
        UiStageCounters.Reset();
        UiStageCounters.Enabled = true;
        try
        {
            work();
            return UiStageCounters.Snapshot();
        }
        finally
        {
            UiStageCounters.Enabled = false;
            UiStageCounters.Reset();
        }
    }

    /// <summary>
    ///     Async twin of <see cref="Counted" />. <c>EndFrameAsync</c> returns a
    ///     <c>ValueTask</c>, and CA2012 is an error in this repo, so the async
    ///     write path cannot be pumped with <c>GetAwaiter().GetResult()</c> from
    ///     inside a synchronous action.
    /// </summary>
    private static async ValueTask<UiStageSnapshot> CountedAsync(Func<ValueTask> work)
    {
        UiStageCounters.Reset();
        UiStageCounters.Enabled = true;
        try
        {
            await work();
            return UiStageCounters.Snapshot();
        }
        finally
        {
            UiStageCounters.Enabled = false;
            UiStageCounters.Reset();
        }
    }

    /// <summary>
    ///     Engine twin of <see cref="Counted" /> (#436: the write stage moved
    ///     into the standalone leaf with its writer, so its instrument moved
    ///     with it — <c>EngineStageCounters</c> over the same switch discipline).
    /// </summary>
    private static EngineCells.EngineStageSnapshot CountedEngine(Action work)
    {
        EngineCells.EngineStageCounters.Reset();
        EngineCells.EngineStageCounters.Enabled = true;
        try
        {
            work();
            return EngineCells.EngineStageCounters.Snapshot();
        }
        finally
        {
            EngineCells.EngineStageCounters.Enabled = false;
            EngineCells.EngineStageCounters.Reset();
        }
    }

    /// <summary>Async twin of <see cref="CountedEngine" />.</summary>
    private static async ValueTask<EngineCells.EngineStageSnapshot> CountedEngineAsync(Func<ValueTask> work)
    {
        EngineCells.EngineStageCounters.Reset();
        EngineCells.EngineStageCounters.Enabled = true;
        try
        {
            await work();
            return EngineCells.EngineStageCounters.Snapshot();
        }
        finally
        {
            EngineCells.EngineStageCounters.Enabled = false;
            EngineCells.EngineStageCounters.Reset();
        }
    }

    /// <summary>
    ///     Parse fires exactly once per fresh push. This is the positive
    ///     control the store-path suite depends on:
    ///     <c>StorePathSkipsMarkdownParseTests</c> asserts
    ///     <c>MarkdownParses == 0</c>, which only carries information because
    ///     this test proves the same counter reaches 1 when a parse genuinely
    ///     happens.
    /// </summary>
    [Test]
    public async Task MarkdownParse_FiresExactlyOnce_PerFreshPush()
    {
        var snap = Counted(() =>
        {
            var renderer = new StreamingMarkdownRenderer();
            renderer.Push(StructuredMarkdown);
            _ = renderer.RenderTail(80);
        });

        await Assert.That(snap.MarkdownParses)
            .IsEqualTo(1)
            .Because("one Push then one RenderTail reaches MarkdownBlockParser.ParseInto exactly once");
        await Assert.That(snap.Materializations)
            .IsEqualTo(1)
            .Because("that same RenderTail materialized once — the parse call sits inside the materialize, not beside it");
    }

    /// <summary>
    ///     The memo guard, pinned in the direction that catches the specific
    ///     placement mistake this slice could have shipped. The renderer class
    ///     doc states that a frame issues <c>RenderTail</c> twice — Measure then
    ///     Paint — and "the second call must be free". A counter at method entry
    ///     reports 2 here; the counter belongs past the memo and reports 1.
    /// </summary>
    [Test]
    public async Task Materialization_IgnoresTheMemoHit_SoMeasurePaintPairCountsOnce()
    {
        var snap = Counted(() =>
        {
            var renderer = new StreamingMarkdownRenderer();
            renderer.Push(StructuredMarkdown);
            _ = renderer.RenderTail(80); // the Measure half — real work
            _ = renderer.RenderTail(80); // the Paint half — memo must absorb it
        });

        await Assert.That(snap.Materializations)
            .IsEqualTo(1)
            .Because("the second RenderTail matches the (width, sourceLen, complete) memo and does no work");
        await Assert.That(snap.MarkdownParses)
            .IsEqualTo(1)
            .Because("the memo returns before ParseInto, so the repeat call is not a second parse either");
    }

    /// <summary>
    ///     The control for the test above, and the reason that one is not
    ///     vacuous. A width change invalidates the frozen geometry by contract,
    ///     so the counter <b>must</b> move again. Without this, a counter that
    ///     never fired at all would also report 1 for the repeat-call pair and
    ///     the memo assertion would pass on an instrument that measures nothing.
    /// </summary>
    [Test]
    public async Task Materialization_FiresAgain_WhenWidthChangeInvalidatesTheMemo()
    {
        var snap = Counted(() =>
        {
            var renderer = new StreamingMarkdownRenderer();
            renderer.Push(StructuredMarkdown);
            _ = renderer.RenderTail(80);
            _ = renderer.RenderTail(40); // width changed — the memo cannot absorb this
        });

        await Assert.That(snap.Materializations)
            .IsEqualTo(2)
            .Because("a width change invalidates the memo, so the second render is real work and must be counted");
        await Assert.That(snap.MarkdownParses)
            .IsEqualTo(2)
            .Because("each width change re-parses the tail — that per-frame cost is what #412 has to keep bounded");
    }

    /// <summary>
    ///     Layout, through the real cache. Ten unmeasured blocks in a ten-row
    ///     viewport: the counter must agree with the cache's own
    ///     <c>MeasureCallsLastFrame</c> (both tallies are incremented on one
    ///     source line, so a mismatch means the counter is wired elsewhere), be
    ///     non-zero, and be strictly below the block count — blocks below the
    ///     fold are never measured, which is the claim #412 turns into a gate.
    /// </summary>
    [Test]
    public async Task BlockLayout_CountsOnlyTheBlocksTheFrameActuallyMeasured()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 10; i++)
        {
            cache.Append(new CountingBlock($"b{i}", 3));
        }

        var snap = Counted(() => _ = cache.PrepareLayout(width: 40, viewportH: 10, scrollY: 0));

        await Assert.That(snap.BlocksLaidOut)
            .IsEqualTo(cache.MeasureCallsLastFrame)
            .Because("both tallies sit on the same source line; a mismatch means the counter is wired elsewhere");
        await Assert.That(snap.BlocksLaidOut)
            .IsGreaterThan(0)
            .Because("a fresh ten-block cache in a ten-row viewport has visible blocks to settle");
        await Assert.That(snap.BlocksLaidOut)
            .IsLessThan(10)
            .Because("blocks below the fold are never measured — that is virtualization, not a rounding detail");
    }

    /// <summary>
    ///     Write, on the synchronous path, against a recording backend. One
    ///     frame carrying bytes reaches the backend once; the next frame carries
    ///     nothing and <c>AnsiWriter</c> drops it <b>before</b> touching the
    ///     backend. The counter sits past that drop deliberately — "terminal
    ///     writes" is a device-bound number, and counting a dropped frame would
    ///     quietly turn it into a method-call tally the cost argument never
    ///     priced.
    /// </summary>
    [Test]
    public async Task TerminalWrite_CountsOnlyFramesThatReachedTheBackend()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, true);

        // #436: the write stage lives in the engine now — this reads the
        // engine counter, which the backend's own tally keeps honest.
        var snap = CountedEngine(() =>
        {
            writer.BeginFrame();
            writer.WriteText("x");
            writer.EndFrame(); // carries a byte → one real write

            writer.BeginFrame();
            writer.EndFrame(); // empty → dropped by the writer, no backend write
        });

        await Assert.That(snap.TerminalWrites)
            .IsEqualTo(backend.Writes.Count)
            .Because("the counter must agree with the backend's own recorded writes, not with a frame count");
        await Assert.That(snap.TerminalWrites)
            .IsEqualTo(1)
            .Because("one non-empty frame plus one dropped empty frame reach the backend exactly once between them");
    }

    /// <summary>
    ///     The async twin. <c>EndFrame</c> and <c>EndFrameAsync</c> are separate
    ///     methods reaching the backend separately, so each carries its own
    ///     counter site: instrumenting only the synchronous one would have left
    ///     the async path — the one the CLI frame loop actually uses — silently
    ///     uncounted, and reading a plausible 0.
    /// </summary>
    [Test]
    public async Task TerminalWrite_CountsTheAsyncFramePath_Too()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);

        var snap = await CountedEngineAsync(async () =>
        {
            writer.BeginFrame();
            writer.WriteText("x");
            await writer.EndFrameAsync();
        });

        await Assert.That(snap.TerminalWrites)
            .IsEqualTo(1)
            .Because("EndFrameAsync reaches the backend on its own and carries its own counter site");
        await Assert.That(backend.Writes.Count)
            .IsEqualTo(1)
            .Because("the async path really shipped a frame");
        await Assert.That(System.Text.Encoding.UTF8.GetString(backend.Writes[0]))
            .IsEqualTo("x")
            .Because("the frame content is unchanged — #409 adds no bytes to the stream");
    }

    /// <summary>
    ///     The switch, in the direction that catches an unconditional counter.
    ///     The identical workload runs with <see cref="UiStageCounters.Enabled" />
    ///     off and every total must stay at zero, while the backend and the
    ///     cache prove the work happened. Without that second proof, "counted
    ///     nothing" would also be satisfied by a workload that never ran.
    /// </summary>
    [Test]
    public async Task GuardOff_IdenticalWorkload_CountsNothing()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, true);
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 10; i++)
        {
            cache.Append(new CountingBlock($"b{i}", 3));
        }

        UiStageCounters.Reset();
        UiStageCounters.Enabled = false;
        UiStageSnapshot snap;
        try
        {
            var renderer = new StreamingMarkdownRenderer();
            renderer.Push(StructuredMarkdown);
            _ = renderer.RenderTail(80);
            _ = cache.PrepareLayout(width: 40, viewportH: 10, scrollY: 0);
            writer.BeginFrame();
            writer.WriteText("x");
            writer.EndFrame();
            snap = UiStageCounters.Snapshot();
        }
        finally
        {
            UiStageCounters.Enabled = false;
            UiStageCounters.Reset();
        }

        await Assert.That(snap.MarkdownParses)
            .IsEqualTo(0)
            .Because("Enabled == false — the same workload counted 1 and 2 in the enabled arms above");
        await Assert.That(snap.Materializations)
            .IsEqualTo(0)
            .Because("Enabled == false — an unconditional counter passes the other tests and fails here");
        await Assert.That(snap.BlocksLaidOut).IsEqualTo(0).Because("Enabled == false");
        await Assert.That(snap.TerminalWrites).IsEqualTo(0).Because("Enabled == false");
        await Assert.That(EngineCells.EngineStageCounters.TerminalWrites)
            .IsEqualTo(0)
            .Because("the engine counter obeys the same switch discipline — off here, so the writer above must not move it");

        await Assert.That(backend.Writes.Count)
            .IsEqualTo(1)
            .Because("the workload really ran; a 'counted nothing' over a workload that never executed proves nothing");
        await Assert.That(cache.MeasureCallsLastFrame)
            .IsGreaterThan(0)
            .Because("the layout work really ran, proved by the cache's own independent tally");
    }

    /// <summary>
    ///     Reset really zeroes. The armed parse comes first, so a reset that
    ///     did nothing surfaces as a non-zero total instead of passing on an
    ///     already-empty counter.
    /// </summary>
    [Test]
    public async Task Reset_ZeroesEveryCounter()
    {
        var armed = Counted(() =>
        {
            var renderer = new StreamingMarkdownRenderer();
            renderer.Push(StructuredMarkdown);
            _ = renderer.RenderTail(80);
        });
        await Assert.That(armed.MarkdownParses)
            .IsEqualTo(1)
            .Because("the armed window must have parsed, or the reset assertion below is satisfied by an empty counter");

        UiStageCounters.Reset();
        var after = UiStageCounters.Snapshot();
        await Assert.That(after.BlocksLaidOut).IsEqualTo(0);
        await Assert.That(after.Materializations).IsEqualTo(0);
        await Assert.That(after.MarkdownParses).IsEqualTo(0);
        await Assert.That(after.TerminalWrites).IsEqualTo(0);
    }
}