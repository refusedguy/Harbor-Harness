using BenchmarkDotNet.Attributes;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Benchmarks;

/// <summary>
///     Packing cost of the status bar: the <c>Fit</c> pass that decides which
///     segments survive the terminal width. This is a per-frame path taken while
///     tokens stream, and it is where #487 removed an O(n²) re-sum and the
///     process-global width-cache lock.
///     <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> one <c>StatusBarLayout.Fit</c> over a composed row,
///         from the projected segments to the survivor count.</item>
///         <item><c>Payload:</c> model + mode hint (both <c>FixedPriority</c>) followed by
///         N flexible segments, packed to a width that leaves the fixed pair plus three
///         flexible ones — the frame shape where the shrink loop actually iterates.</item>
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
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class StatusBarLayoutFitBenchmark
{
    private const int KeepFlexible = 3;
    private const int RealFooterSegments = 12;

    [Params(6, 8, 24)]
    public int Segments;

    private StatusSeg[] _template = null!;
    private StatusSeg[] _scratch = null!;
    private StatusSeg[] _footerTemplate = null!;
    private StatusSeg[] _footerScratch = null!;
    private int _width;
    private int _footerWidth;

    [GlobalSetup]
    public void Setup()
    {
        _template = BuildRow(Segments);
        _scratch = new StatusSeg[Segments];
        _width = StatusBarLayout.TotalWidth(_template.AsSpan()[..(2 + KeepFlexible)]);

        _footerTemplate = BuildRow(RealFooterSegments);
        _footerScratch = new StatusSeg[RealFooterSegments];
        _footerWidth = StatusBarLayout.TotalWidth(_footerTemplate.AsSpan()[..(2 + KeepFlexible)]);
    }

    [Benchmark(Baseline = true, Description = "StatusBarLayout_Fit")]
    public int Fit()
    {
        _template.CopyTo(_scratch, 0);
        return StatusBarLayout.Fit(_scratch, _width);
    }

    /// <summary>
    /// The row the CellForge footer actually composes — <c>ChatScreenLayout</c> sizes
    /// its compose buffer at 12. Named so the BENCHMARKS.md row has a stable label
    /// rather than one that moves with the <c>Segments</c> sweep.
    /// </summary>
    [Benchmark(Description = "StatusBarLayout_Fit_TwelveSegments")]
    public int FitTwelveSegments()
    {
        _footerTemplate.CopyTo(_footerScratch, 0);
        return StatusBarLayout.Fit(_footerScratch, _footerWidth);
    }

    private static StatusSeg[] BuildRow(int segments)
    {
        var row = new StatusSeg[segments];
        row[0] = new StatusSeg("kilocode/hilo3", StatusAccent.Accent, FixedPriority: true);
        row[1] = new StatusSeg("⏸ awaiting approval", StatusAccent.Warning, FixedPriority: true);
        for (int i = 2; i < segments; i++)
        {
            row[i] = new StatusSeg($"seg-{i} 12k↑ 4.5k", StatusAccent.Dim, FixedPriority: false);
        }

        return row;
    }
}
