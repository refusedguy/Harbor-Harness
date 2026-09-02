using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"OpenAiSseParser.ParseChunk\" /> — the SSE
///     chunk parser used by OpenAI-compatible providers. Measures the cost
///     of parsing server-sent event data lines into <see cref=\"LlmEvent\" />
///     sequences, focusing on zero-allocation span-based extraction of the
///     <c>content</c> and <c>tool_calls</c> fields.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class OpenAiSseParserBenchmark
{
    private string _smallChunk = null!;
    private string _mediumChunk = null!;
    private string _largeChunk = null!;
    private Dictionary<int, string> _indexToId = null!;

    [GlobalSetup]
    public void Setup()
    {
        _indexToId = new Dictionary<int, string>();
        _smallChunk = BuildSseChunk("Hello!", 1, 32);
        _mediumChunk = BuildSseChunk("This is a medium-length response from the model with multiple sentences and some reasoning content.", 1, 256);
        _largeChunk = BuildSseChunk(
            new string('x', 512),
            toolCalls: 3,
            tokenCount: 4096);
    }

    [Benchmark(Description = "ParseChunk small (32B)", Baseline = true)]
    public int Parse_Small() => OpenAiSseParser.ParseChunk(_smallChunk, _indexToId, NullLogger.Instance).Count();

    [Benchmark(Description = "ParseChunk medium (256B)")]
    public int Parse_Medium() => OpenAiSseParser.ParseChunk(_mediumChunk, _indexToId, NullLogger.Instance).Count();

    [Benchmark(Description = "ParseChunk large with tool_calls (4KB)")]
    public int Parse_Large() => OpenAiSseParser.ParseChunk(_largeChunk, _indexToId, NullLogger.Instance).Count();

    private static string BuildSseChunk(string content, int toolCalls = 0, int tokenCount = 32)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("data: {\"id\":\"chatcmpl-123\",\"object\":\"chat.completion.chunk\",\"created\":1234567890,\"model\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"");
        sb.Append(content.Replace("\"", "\\\""));
        sb.Append("\"},\"finish_reason\":null}]}");

        if (toolCalls > 0)
        {
            sb.Append("\n\ndata: {\"id\":\"chatcmpl-123\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[");
            for (int i = 0; i < toolCalls; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"index\":");
                sb.Append(i);
                sb.Append(",\"id\":\"tc_");
                sb.Append(i);
                sb.Append("\",\"type\":\"function\",\"function\":{\"name\":\"test_tool\",\"arguments\":\"{\\\"path\\\":\\\"file.cs\\\"}\"}}");
            }
            sb.Append("]},\"finish_reason\":\"tool_use\"}]}");
        }

        sb.Append("\n\ndata: {\"id\":\"chatcmpl-123\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"],\"usage\":{\"prompt_tokens\":");
        sb.Append(tokenCount);
        sb.Append(",\"completion_tokens\":");
        sb.Append(tokenCount / 2);
        sb.Append("}}\n\n");
        return sb.ToString();
    }
}
