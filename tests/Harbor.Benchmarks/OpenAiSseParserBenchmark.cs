using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref="OpenAiWire.TryParseChatChunkLine"/> — the SSE
///     chunk parser used by OpenAI-compatible providers. Measures the cost
///     of parsing server-sent event data lines into <see cref="LlmEvent" />
///     sequences, focusing on zero-allocation span-based extraction of the
///     <c>content</c> and <c>tool_calls</c> fields.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>OpenAiWire.TryParseChatChunkLine</c> call over a
///          canned multi-<c>data:</c> SSE chunk.</item>
///          <item><c>Payload:</c> three chunk shapes built once in <c>Setup</c>: 32 B text,
///          256 B text, and a 4 KB chunk carrying 3 <c>tool_calls</c> plus a usage
///          frame.</item>
///          <item><c>StateReset:</c> per invocation — a fresh <c>ChunkStreamState</c> (with
///          its own index→id map) is built inside every row, so tool-call indices never carry
///          over between iterations.</item>
///          <item><c>Drain:</c> none — parsing is synchronous and the resulting event list is
///          fully consumed.</item>
///          <item><c>RetainedState:</c> none; the three chunk strings are read-only
///          fixtures.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the parsed <c>LlmEvent</c> list plus any string
///          that escapes into it. The tool-call row's extra cost is the argument-fragment
///          string materialisation.</item>
/// </list>
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
    public int Parse_Small() => ParseChunk(_smallChunk, _indexToId, NullLogger.Instance).Count;

    [Benchmark(Description = "ParseChunk medium (256B)")]
    public int Parse_Medium() => ParseChunk(_mediumChunk, _indexToId, NullLogger.Instance).Count;

    [Benchmark(Description = "ParseChunk large with tool_calls (4KB)")]
    public int Parse_Large() => ParseChunk(_largeChunk, _indexToId, NullLogger.Instance).Count;

    private static IReadOnlyList<LlmEvent> ParseChunk(string data, Dictionary<int, string> indexToId, ILogger logger)
    {
        var state = new ChunkStreamState();
        foreach ((int index, string id) in indexToId)
        {
            state.IndexToId[index] = id;
        }

        return OpenAiWire.TryParseChatChunkLine(data, state, logger);
    }

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
