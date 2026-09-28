using BenchmarkDotNet.Attributes;
using Harbor.Application.Agents;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"StreamingCoalescer\" /> — the buffer that
///     accumulates text deltas, thinking deltas, and tool-call argument
///     fragments during a streaming LLM turn. Measures the cost of
///     appending deltas, flushing buffers, and materializing tool calls
///     under realistic streaming workloads.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> <c>DeltaCount</c> text or thinking appends,
///          <c>DeltaCount</c> tool-call start/delta pairs plus <c>MaterializeToolCalls</c>, or
///          appends followed by one <c>FlushText</c>.</item>
///          <item><c>Payload:</c> pre-built delta strings ("Token i ", "Thinking token i ",
///          <c>{"argI":"valueI"}</c>) created once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per invocation — every row constructs its own <see
///          cref="StreamingCoalescer" />, so no buffer, tool-call table or flush state
///          survives an iteration.</item>
///          <item><c>Drain:</c> none — the coalescer buffers in memory; there is no queue.</item>
///          <item><c>RetainedState:</c> the coalescer instance of the current op only.
///          <c>DisposeCoalescer</c> (<c>[IterationCleanup]</c>) releases it so the disposable
///          does not pile up across iterations; <c>GlobalCleanup</c> is the final
///          backstop.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the internal buffers plus, on the tool-call row,
///          the materialized tool-call list. <c>FlushText</c> materializes the whole buffer
///          into a string — that allocation is the row's result, not a leak.</item>
/// </list>
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

    [IterationCleanup]
    public void DisposeCoalescer()
    {
        // Each row allocates its own StreamingCoalescer and overwrites the field,
        // so without this the disposables pile up one per iteration until the
        // global cleanup ran. Release it between iterations instead.
        _coalescer?.Dispose();
        _coalescer = null!;
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
