using BenchmarkDotNet.Attributes;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Benchmarks;

/// <summary>
///     Packing cost of the status bar: the <c>Fit</c> pass that decides which
///     segments survive the terminal width. This is a per-frame path taken while
///     tokens stream, and it is where #487 removed an O(n²) re-sum of the row and
///     the process-global width-cache lock.
///     <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> one <c>StatusBarLayout.Fit</c> over a composed row,
///         from the projected segments to the survivor count.</item>
///         <item><c>Payload:</c> model + mode hint (both <c>FixedPriority</c>) followed by
///         N flexible segments, packed to a width that leaves the fixed pair plus three
///         flexible ones — the frame shape where the shrink loop actually iterates. A row
///         that already fits would measure a no-op.</item>
///         <item><c>StateReset:</c> the row is restored from a pristine template before every
///         call, because <c>Fit</c> mutates the span in place. That copy is inside the
///         measured region and is attributed below.</item>
///         <item><c>Drain:</c> none — <c>Fit</c> is synchronous.</item>
///         <item><c>RetainedState:</c> the per-thread width cache behind
///         <c>UnicodeWidth.WidthCached</c> (256 slots, bounded, warm after the first
///         iterations). Identical for every row, so these are steady-state packing costs
///         and not rune-decode costs.</item>
///         <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///         <item><c>AllocAttribution:</c> the reset copy allocates nothing (an array
///         <c>CopyTo</c> into a preallocated buffer). On a row resolved by dropping
///         segments <c>Fit</c> is 0 B too; a row that has to be character-cut allocates
///         the string prefix, which is why no row here is a cut row. Width lookups
///         allocate nothing — the cache stores into a fixed per-thread table.</item>
///     </list>
///     <para><b>Why the rows are named rather than parameterised.</b> A <c>[Params]</c>
///     field parameterises <i>every</i> method in the class, so a fixed 12-segment row
///     would silently be reported as "twelve segments" at <c>Segments: 6</c> — a label
///     that disagrees with the row it measures is worse than no label. Every row here
///     keeps 5 survivors; only the segment count varies.</para>
///     <para><b>What these numbers are not.</b> Wall clock still grows faster than
///     linearly in the segment count, and that is not the width measurement: it is the
///     in-place compaction, which shifts one 16-byte struct plus one int per surviving
///     slot every time a victim is dropped. That term is inherent to the "mutate the
///     caller's span, allocate nothing" contract, costs ~30x less per byte than the
///     lock+hash+string-compare it replaced, and takes no monitor. The measurement count
///     itself is pinned exactly — and machine-independently — by
///     <c>StatusBarFitPerfTests.Fit_MeasuresEachSegmentOnce_*</c>.</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class StatusBarLayoutFitBenchmark
{
    private const int KeepFlexible = 3;
    private const int Footer = 0;
    private const int Six = 1;
    private const int Eight = 2;
    private const int TwentyFour = 3;

    // Assigned in GlobalSetup, so it cannot be `readonly` — that is only legal in
    // a constructor or a field initializer.
    private Payload[] _rows = null!;

    /// <summary>One row shape: a pristine template, the buffer it is packed into,
    /// and the width it is packed to. Nothing here grows or accumulates between
    /// iterations — the scratch buffer is overwritten whole on every call.</summary>
    private sealed class Payload
    {
        public StatusSeg[] Template { get; init; } = null!;

        public StatusSeg[] Scratch { get; init; } = null!;

        public int Width { get; init; }
    }

    [GlobalSetup]
    public void Setup()
    {
        _rows =
        [
            NewPayload(12),
            NewPayload(6),
            NewPayload(8),

            // The pathological row that exposed the quadratic. Nothing a renderer
            // builds goes near 24 segments — CellForge sizes its compose buffer at
            // 12 — so this row is here to keep the slope visible, not to gate a
            // budget.
            NewPayload(24),
        ];
    }

    /// <summary>The row the CellForge footer actually composes —
    /// <c>ChatScreenLayout</c> sizes its compose buffer at 12.</summary>
    [Benchmark(Baseline = true, Description = "StatusBarLayout_Fit_TwelveSegments")]
    public int TwelveSegments() => Pack(Footer);

    [Benchmark(Description = "StatusBarLayout_Fit_SixSegments")]
    public int SixSegments() => Pack(Six);

    [Benchmark(Description = "StatusBarLayout_Fit_EightSegments")]
    public int EightSegments() => Pack(Eight);

    [Benchmark(Description = "StatusBarLayout_Fit_TwentyFourSegments")]
    public int TwentyFourSegments() => Pack(TwentyFour);

    private int Pack(int index)
    {
        Payload row = _rows[index];
        row.Template.CopyTo(row.Scratch, 0);
        return StatusBarLayout.Fit(row.Scratch, row.Width);
    }

    private static Payload NewPayload(int segments)
    {
        var template = new StatusSeg[segments];
        template[0] = new StatusSeg("kilocode/hilo3", StatusAccent.Accent, FixedPriority: true);
        template[1] = new StatusSeg("⏸ awaiting approval", StatusAccent.Warning, FixedPriority: true);
        for (int i = 2; i < segments; i++)
        {
            template[i] = new StatusSeg($"seg-{i} 12k↑ 4.5k", StatusAccent.Dim, FixedPriority: false);
        }

        return new Payload
        {
            Template = template,
            Scratch = new StatusSeg[segments],
            Width = StatusBarLayout.TotalWidth(template.AsSpan()[..(2 + KeepFlexible)]),
        };
    }
}
