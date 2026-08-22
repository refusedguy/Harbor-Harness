using BenchmarkDotNet.Attributes;
using Harbor.Core.Agents;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"StreamingCoalescer\" /> — the buffer that
///     accumulates text deltas, thinking deltas, and tool-call argument
///     fragments during a streaming LLM turn. Measures the cost of
///     appending deltas, flushing buffers, and materializing tool calls
///     under realistic streaming workloads.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class StreamingCoalescerBenchmark
{
    private StreamingCoalescer _coalescer = null!;
    private string[] _textDeltas = null!;
    private string[] _thinkingDeltas = null!;
    private (string id, string name, string argsDelta)[] _toolCallDeltas = null!;

    [Params(10, 100, 1000)]
    public int DeltaCount;

    [GlobalSetup]
    public void Setup()
    {
        _textDeltas = new string[DeltaCount];
        _thinkingDeltas = new string[DeltaCount];
        _toolCallDeltas = new (string, string, string)[DeltaCount];

        for (int i = 0; i < DeltaCount; i++)
        {
            _textDeltas[i] = $"Token {i} ";
            _thinkingDeltas[i] = $"Thinking token {i} ";
            _toolCallDeltas[i] = ($"tc_{i}", "test_tool", $"{{\"arg{i}\":\"value{i}\"}}");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _coalescer?.Dispose();
    }

    [Benchmark(Description = "AppendTextDelta N times")]
    public void AppendTextDeltas()
    {
        _coalescer = new StreamingCoalescer();
        for (int i = 0; i < DeltaCount; i++)
            _coalescer.AppendTextDelta(_textDeltas[i]);
    }

    [Benchmark(Description = "AppendThinkingDelta N times")]
    public void AppendThinkingDeltas()
    {
        _coalescer = new StreamingCoalescer();
        for (int i = 0; i < DeltaCount; i++)
            _coalescer.AppendThinkingDelta(_thinkingDeltas[i]);
    }

    [Benchmark(Description = "Append tool call deltas + Materialize")]
    public void ToolCallDeltas_AndMaterialize()
    {
        _coalescer = new StreamingCoalescer();
        for (int i = 0; i < DeltaCount; i++)
        {
            var (id, name, argsDelta) = _toolCallDeltas[i];
            _coalescer.StartToolCall(id, name);
            _coalescer.AppendToolCallDelta(id, argsDelta);
        }
        _coalescer.MaterializeToolCalls();
    }

    [Benchmark(Description = "FlushText after N deltas")]
    public string FlushText_AfterNDeltas()
    {
        _coalescer = new StreamingCoalescer();
        for (int i = 0; i < DeltaCount; i++)
            _coalescer.AppendTextDelta(_textDeltas[i]);
        return _coalescer.FlushText();
    }
}
