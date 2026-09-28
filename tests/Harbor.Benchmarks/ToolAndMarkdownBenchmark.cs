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
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>JsonDocument.Parse</c> + <c>RootElement.Clone</c>
///          over a tool-argument payload (14 B / ~1 KB / ~4 KB).</item>
///          <item><c>Payload:</c> three canned JSON documents built once in <c>Setup</c>: a 14
///          B object, a ~1 KB object with one big string, and a ~4 KB array of 8 tool
///          calls.</item>
///          <item><c>StateReset:</c> per invocation — the documents are disposed at the end of
///          each op (<c>using</c>), so no parsed DOM survives an iteration.</item>
///          <item><c>Drain:</c> none — parsing is synchronous.</item>
///          <item><c>RetainedState:</c> only the immutable JSON strings.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the cloned <c>JsonElement</c> (one backing document
///          per call) is the only allocation the tool path makes; that is the cost the
///          streaming/tool-call path pays per tool invocation.</item>
/// </list>
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
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one inline-markdown scan over a ~600-char message either
///          via an <c>IndexOf</c> scan or via a compiled <see
///          cref="System.Text.RegularExpressions.Regex" />.</item>
///          <item><c>Payload:</c> a text with 10 <c>**bold**</c> and 10 <c>`code`</c> segments
///          interleaved with filler, built once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per invocation — the text is immutable and the regex is a
///          read-only compiled instance.</item>
///          <item><c>Drain:</c> none — both scans are synchronous.</item>
///          <item><c>RetainedState:</c> the compiled <c>Regex</c> (its construction cost is
///          amortized by design, matching how the renderer uses it as a static).</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the regex row allocates a <c>Match</c> collection
///          per call; the <c>IndexOf</c> row allocates nothing. The delta is the case for the
///          hand-rolled scan.</item>
/// </list>
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
