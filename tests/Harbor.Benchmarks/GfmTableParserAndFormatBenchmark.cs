using BenchmarkDotNet.Attributes;
using Harbor.Terminal.Abstractions.Rendering;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"GfmTableParser.TryParse\" /> and
///     <see cref=\"GfmTableFormatter.Format\" /> — the GFM pipe-table
///     pipeline used by every terminal renderer. Measures parse throughput
///     for tables of varying row counts, and format cost for rendered output.
/// </summary>
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
