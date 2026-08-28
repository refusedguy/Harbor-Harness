using System.Text.Json;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> for
///     tool-call argument payloads of varying sizes. Represents the cost of
///     <c>StreamingCoalescer.Materialize</c> / tool argument deserialization on
///     the hot path (every tool call parses its JSON args).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ToolArgsJsonBenchmark
{
    private string _smallJson = null!;
    private string _mediumJson = null!;
    private string _largeJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallJson = """{"input":"x"}""";

        // Medium ~1 KB: object with a ~1 KB string value.
        _mediumJson = JsonSerializer.Serialize(new { input = new string('a', 1024) });

        // Large ~4 KB: array of tool_calls with args.
        var calls = new object[8];
        for (int i = 0; i < 8; i++)
            calls[i] = new { id = $"call_{i:D3}", name = "read", arguments = new { path = $"/tmp/file_{i}.txt", limit = 100, offset = i * 10 } };
        _largeJson = JsonSerializer.Serialize(new { tool_calls = calls });
    }

    [Benchmark(Description = "JsonDocument.Parse small (~14 B) + Clone")]
    public JsonElement Parse_Small()
    {
        using var doc = JsonDocument.Parse(_smallJson);
        return doc.RootElement.Clone();
    }

    [Benchmark(Description = "JsonDocument.Parse medium (~1 KB) + Clone")]
    public JsonElement Parse_Medium()
    {
        using var doc = JsonDocument.Parse(_mediumJson);
        return doc.RootElement.Clone();
    }

    [Benchmark(Description = "JsonDocument.Parse large (~4 KB) + Clone")]
    public JsonElement Parse_Large()
    {
        using var doc = JsonDocument.Parse(_largeJson);
        return doc.RootElement.Clone();
    }
}

/// <summary>
///     Benchmarks inline markdown scanning for <c>**bold**</c>, <c>*italic*</c>,
///     and <c>`code`</c> patterns using <see cref="string.IndexOf(char)"/> loop
///     vs <see cref="Regex"/>. Proxy for ChatMarkdown / streaming markdown
///     rendering without taking a dependency on contrib.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class InlineMarkdownScanBenchmark
{
    private string _text = null!;
    private Regex _regex = null!;

    [GlobalSetup]
    public void Setup()
    {
        // ~100 chars base + 10 bold segments spread through the text.
        var filler = "hello world ";
        var parts = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            parts.Add(filler);
            parts.Add($"**bold{i}**");
            parts.Add(" and ");
            parts.Add($"`code{i}`");
            parts.Add(" ");
        }

        _text = string.Concat(parts);

        // Matches **bold**, *italic*, and `code` - representative inline scan.
        _regex = new Regex(@"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`)", RegexOptions.Compiled);
    }

    [Benchmark(Description = "IndexOf scan for ** ` *", Baseline = true)]
    public int IndexOf_Scan()
    {
        int count = 0;
        int pos = 0;
        while (pos < _text.Length)
        {
            int bold = _text.IndexOf("**", pos, StringComparison.Ordinal);
            int code = _text.IndexOf('`', pos);
            int italic = _text.IndexOf('*', pos);

            int next = -1;
            if (bold >= 0) next = next < 0 ? bold : Math.Min(next, bold);
            if (code >= 0) next = next < 0 ? code : Math.Min(next, code);
            if (italic >= 0) next = next < 0 ? italic : Math.Min(next, italic);

            if (next < 0) break;
            count++;
            pos = next + 1;
        }

        return count;
    }

    [Benchmark(Description = "Regex scan for **bold** *italic* `code`")]
    public int Regex_Scan() => _regex.Matches(_text).Count;
}
