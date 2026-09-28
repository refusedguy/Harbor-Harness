using BenchmarkDotNet.Attributes;
using Harbor.Terminal.Abstractions.Rendering;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"GfmTableParser.TryParse\" /> and
///     <see cref=\"GfmTableFormatter.Format\" /> — the GFM pipe-table
///     pipeline used by every terminal renderer. Measures parse throughput
///     for tables of varying row counts, and format cost for rendered output.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>GfmTableParser.TryParse</c>, one parse +
///          <c>GfmTableFormatter.Format</c> to a Unicode grid, or the parse+format
///          roundtrip.</item>
///          <item><c>Payload:</c> a <c>RowCount</c>-row GFM pipe table (3 columns, header +
///          alignment rows), built once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per invocation — the parser is stateless and each row
///          parses the same immutable <c>string[]</c> afresh; no accumulated table state
///          exists.</item>
///          <item><c>Drain:</c> none — both operations are synchronous and fully consumed by
///          the op.</item>
///          <item><c>RetainedState:</c> none between iterations (the source lines are
///          read-only and shared).</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the parsed table (rows + cells + alignment)
///          dominates; the format rows add the rendered grid and the <c>ToArray()</c>
///          materialisation.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class GfmTableParserAndFormatBenchmark
{
    private string[] _tableLines = null!;

    [Params(5, 50, 500)]
    public int RowCount;

    [GlobalSetup]
    public void Setup()
    {
        _tableLines = BuildGfmTable(RowCount);
    }

    [Benchmark(Description = "Parse GFM table lines", Baseline = true)]
    public GfmTable Parse_Table()
    {
        GfmTableParser.TryParse(_tableLines, 0, out var table, out _);
        return table;
    }

    [Benchmark(Description = "Format parsed table to Unicode grid")]
    public string[] Format_Table()
    {
        GfmTableParser.TryParse(_tableLines, 0, out var table, out _);
        return GfmTableFormatter.Format(table, 120).ToArray();
    }

    [Benchmark(Description = "Parse + Format roundtrip")]
    public string[] ParseAndFormat()
    {
        GfmTableParser.TryParse(_tableLines, 0, out var table, out _);
        return GfmTableFormatter.Format(table, 120).ToArray();
    }

    private static string[] BuildGfmTable(int rowCount)
    {
        var lines = new List<string>();
        lines.Add("| Column A | Column B | Column C |");
        lines.Add("| -------- | -------- | -------- |");
        lines.Add("| :---     | :---:    | ---:     |");

        for (int i = 0; i < rowCount; i++)
        {
            lines.Add($"| Value A{i} | Value B{i} | Value C{i} |");
        }

        return lines.ToArray();
    }
}
