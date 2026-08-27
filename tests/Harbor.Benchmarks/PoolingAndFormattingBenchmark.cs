using System.Text;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Extensions;

namespace Harbor.Benchmarks;

// ---------------------------------------------------------------------------
// StringBuilderPoolBenchmark
// ---------------------------------------------------------------------------

/// <summary>
///     Benchmarks <see cref="StringBuilderPool"/> hot paths against raw
///     <see cref="StringBuilder"/> allocations. Validates that pooling is a
///     win for the system-prompt / streaming-coalescer / tool-output paths
///     where builders of 1 KiB–16 KiB are rented on every turn.
///     See <c>src/Harbor.Extensions/PLAN.md</c> P2.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class StringBuilderPoolBenchmark
{
    private string _payload1KB = null!;
    private string _payload16KB = null!;

    [GlobalSetup]
    public void Setup()
    {
        _payload1KB = new string('a', 1024);
        _payload16KB = new string('b', 16 * 1024);
    }

    // -- Pooled Rent/Return cycle ------------------------------------------------

    [Benchmark(Description = "Pooled Rent/Append/Return 1KB", Baseline = true)]
    public string Rent_Return_1KB()
    {
        using var pooled = StringBuilderPool.Rent(1024);
        pooled.Builder.Append(_payload1KB);
        return pooled.ToString();
    }

    [Benchmark(Description = "Pooled Rent/Append/Return 16KB")]
    public string Rent_Return_16KB()
    {
        using var pooled = StringBuilderPool.Rent(16 * 1024);
        pooled.Builder.Append(_payload16KB);
        return pooled.ToString();
    }

    [Benchmark(Description = "Pooled Rent/Append/Clear/Append/Return")]
    public string Rent_Append_Clear_Return()
    {
        using var pooled = StringBuilderPool.Rent(1024);
        pooled.Builder.Append(_payload1KB);
        pooled.Builder.Clear();
        pooled.Builder.Append(_payload1KB);
        return pooled.ToString();
    }

    // -- Raw new StringBuilder baselines (no pooling) ----------------------------

    [Benchmark(Description = "new StringBuilder Append 1KB")]
    public string New_StringBuilder_1KB()
    {
        var sb = new StringBuilder(1024);
        sb.Append(_payload1KB);
        return sb.ToString();
    }

    [Benchmark(Description = "new StringBuilder Append 16KB")]
    public string New_StringBuilder_16KB()
    {
        var sb = new StringBuilder(16 * 1024);
        sb.Append(_payload16KB);
        return sb.ToString();
    }

    [Benchmark(Description = "new StringBuilder Append/Clear/Append")]
    public string New_Append_Clear_Append()
    {
        var sb = new StringBuilder(1024);
        sb.Append(_payload1KB);
        sb.Clear();
        sb.Append(_payload1KB);
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// BodyLinesBenchmark
// ---------------------------------------------------------------------------

/// <summary>
///     Benchmarks <c>string.Split</c> vs manual <c>ReadOnlySpan&lt;char&gt;.IndexOf</c>
///     for message-body line splitting (LOW-003). The TUI chat renderer splits
///     every assistant message into lines; doing so via <c>Split</c> allocates
///     a <c>string[]</c> plus one <c>string</c> per line, while a span loop
///     can enumerate without per-line allocations.
///     <para>
///         <c>ChatMessageFormatter.BodyLines</c> lives in
///         <c>contrib/tui/Harbor.Tui.SpectreTui</c> which is not referenced by
///         <c>Harbor.Benchmarks.csproj</c>; this benchmark uses pure
///         <c>string.Split</c> vs span as a proxy (identical work).
///     </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class BodyLinesBenchmark
{
    private string _10Lines = null!;
    private string _100Lines = null!;

    [GlobalSetup]
    public void Setup()
    {
        _10Lines = BuildLines(10, 48);
        _100Lines = BuildLines(100, 48);
    }

    // -- string.Split ------------------------------------------------------------

    [Benchmark(Description = "string.Split 10 lines", Baseline = true)]
    public int Split_ByNewline_10Lines() => _10Lines.Split('\n').Length;

    [Benchmark(Description = "string.Split 100 lines")]
    public int Split_ByNewline_100Lines() => _100Lines.Split('\n').Length;

    // -- Span.IndexOf loop (zero per-line allocation) ---------------------------

    [Benchmark(Description = "Span.IndexOf 10 lines")]
    public int Span_IndexOf_10Lines() => CountLinesViaSpan(_10Lines);

    [Benchmark(Description = "Span.IndexOf 100 lines")]
    public int Span_IndexOf_100Lines() => CountLinesViaSpan(_100Lines);

    // -- Helpers ----------------------------------------------------------------

    private static string BuildLines(int lineCount, int lineLength)
    {
        var sb = new StringBuilder(lineCount * (lineLength + 1));
        for (int i = 0; i < lineCount; i++)
        {
            // Vary content so branch prediction / vectorization is realistic.
            sb.Append('L');
            sb.Append(i.ToString("D4"));
            sb.Append(' ');
            sb.Append('x', lineLength - 7);
            if (i < lineCount - 1)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    private static int CountLinesViaSpan(string text) => CountLinesViaSpan(text.AsSpan());

    private static int CountLinesViaSpan(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
            return 0;

        int count = 1;
        int idx;
        while ((idx = span.IndexOf('\n')) >= 0)
        {
            count++;
            span = span.Slice(idx + 1);
        }

        return count;
    }
}
