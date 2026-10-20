using BenchmarkDotNet.Attributes;
using EngineCells = Harbor.Tui.CellForge.Rendering;

namespace Harbor.Benchmarks;

/// <summary>
/// #1524 — the cell style on the diff hot path: reading a cell's style
/// (the getter that "builds a CellStyle per access") against comparing two
/// cells' styles without building anything (the packed compare). The rows
/// exist to put a number on the flyweight question the issue asked to be
/// measured before it is applied: a shared style object can only pay if
/// reading a style costs something.
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
/// <item><c>Operation:</c> one full-screen pass over a 200x50 grid (10 000 cells):
/// <c>StyleAccessPerCell</c> reads <c>Cell.Style</c> per cell (the shape the issue
/// names), <c>RebuiltStyleComparePerCell</c> compares two cells through their
/// <c>CellStyle</c> values (the shape the diff path had before #1524), and
/// <c>PackedStyleComparePerCell</c> compares the same two cells through the packed
/// fields (the shape #1524 landed).</item>
/// <item><c>Payload:</c> pre-built screens — a next/front pair of 200x50 grids
/// holding 16 distinct styles, so the scan covers every cell and the style
/// resolution is the real per-cell work, not a single-style fast path.</item>
/// <item><c>StateReset:</c> none per iteration — both grids are read-only during
/// the op and the comparison result is folded into a counter, so no iteration
/// can be cheaper than the one before it.</item>
/// <item><c>Drain:</c> none — nothing is written; the rows touch no terminal.</item>
/// <item><c>RetainedState:</c> the two pre-built grids and their cell arrays; they
/// are allocated once in the setup and reused by every iteration.</item>
/// <item><c>AwaitSemantics:</c> n/a — no async inside any row.</item>
/// <item><c>AllocAttribution:</c> expected zero allocs on every row: the style is a
/// readonly struct and the packed compare reads the cell's own fields. A non-zero
/// number here would mean a style had become a heap object, and the flyweight
/// question would change shape.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class CellStyleBenchmark
{
    private const int Cols = 200;
    private const int Rows = 50;

    private EngineCells.ScreenBuffer _next = null!;
    private EngineCells.ScreenBuffer _front = null!;
    private int _distinctStyles;

    [GlobalSetup]
    public void Setup()
    {
        _next = BuildGrid(0);
        _front = BuildGrid(1);
        _distinctStyles = CountDistinctStyles(_next);
    }

    /// <summary>
    /// Echoes the distinct-vs-cells ratio into the run log: the issue's own
    /// gate number (a shared style representation can only pay if the distinct
    /// styles are far below the cells that carry them).
    /// </summary>
    [GlobalCleanup]
    public void ReportDistinctStyles() => Console.WriteLine(
        $"// STYLE-DISTINCT (cells={Cols * Rows}, distinct={_distinctStyles}, "
        + $"cells-per-style={(double)(Cols * Rows) / _distinctStyles:F1})");

    /// <summary>
    /// The shape the issue names: every cell access builds a CellStyle.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Style access per cell (builds a CellStyle)")]
    public long StyleAccessPerCell()
    {
        long sink = 0;
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                var cell = _next.Get(x, y);
                sink += cell.Style.Fg.Value;
            }
        }

        return sink;
    }

    /// <summary>
    /// The pre-#1524 compare: both styles are materialised, then compared.
    /// </summary>
    [Benchmark(Description = "Style compare per cell (rebuilds both CellStyles)")]
    public int RebuiltStyleComparePerCell()
    {
        int matches = 0;
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                var nextCell = _next.Get(x, y);
                var frontCell = _front.Get(x, y);
                if (nextCell.Style == frontCell.Style)
                {
                    matches++;
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// The post-#1524 compare: the packed fields are read in place, and no
    /// CellStyle is materialised on either side.
    /// </summary>
    [Benchmark(Description = "Style compare per cell (packed fields, no CellStyle)")]
    public int PackedStyleComparePerCell()
    {
        int matches = 0;
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                var nextCell = _next.Get(x, y);
                var frontCell = _front.Get(x, y);
                if (nextCell.HasSameStyle(in frontCell))
                {
                    matches++;
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Builds a 200x50 grid that uses 16 distinct styles, so the measured
    /// pass is the realistic one: many cells, few distinct styles.
    /// </summary>
    private static EngineCells.ScreenBuffer BuildGrid(int variant)
    {
        var buffer = new EngineCells.ScreenBuffer(Cols, Rows);
        for (int y = 0; y < Rows; y++)
        {
            var style = new EngineCells.CellStyle(
                EngineCells.PackedColor.Indexed((byte)((y * 7 + variant) % 256)),
                EngineCells.PackedColor.Indexed((byte)((255 - (y * 5) + variant) % 256)),
                (EngineCells.StyleAttr)((y % 2 == 0) ? 1 : 2));
            buffer.SetText(0, y, $"row {y} " + new string('.', Cols - 8), style);
        }

        return buffer;
    }

    private static int CountDistinctStyles(EngineCells.ScreenBuffer buffer)
    {
        var distinct = new HashSet<EngineCells.CellStyle>();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                distinct.Add(buffer.Get(x, y).Style);
            }
        }

        return distinct.Count;
    }
}
