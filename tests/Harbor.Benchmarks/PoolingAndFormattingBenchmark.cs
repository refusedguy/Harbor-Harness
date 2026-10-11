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
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one rent/append/return cycle at 1 KB or 16 KB, with and
///          without a <c>Clear()</c> in the middle, plus the #1239 system-prompt rows
///          (append a fixed list of ~10 KB of sections) — each against a raw
///          <c>new StringBuilder</c> baseline doing identical work.</item>
///          <item><c>Payload:</c> pre-built 1 KB and 16 KB strings of a single repeated
///          character, and a fixed section list summing to ~10 KiB (the shape of a
///          real system prompt: many short tool descriptions).</item>
///          <item><c>StateReset:</c> per invocation — the rent/return pair is scoped to the op
///          via <c>using</c>, so the pooled builder is always returned before the measurement
///          ends.</item>
///          <item><c>Drain:</c> none — no queues are involved.</item>
///          <item><c>RetainedState:</c> <b>the pool itself</b>, which is the thing under test:
///          rented capacity survives across iterations (that is the win being measured), so it
///          is intentionally not cleared between iterations.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the pooled rows allocate only the returned
///          <c>string</c>; the raw rows additionally allocate the <c>StringBuilder</c> + its
///          char buffer. Any growth-triggered re-allocation shows up as extra bytes on the
///          pooled rows.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class StringBuilderPoolBenchmark
{
    private string _payload1KB = null!;
    private string _payload16KB = null!;
    private string[] _promptSections = [];

    [GlobalSetup]
    public void Setup()
    {
        _payload1KB = new string('a', 1024);
        _payload16KB = new string('b', 16 * 1024);
        _promptSections = BuildPromptSections();
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

    // -- #1239: the system-prompt shape, ~10 KiB of many small sections ------------

    [Benchmark(Description = "Pooled 10KB prompt assembly")]
    public string Prompt_Pooled_10KB()
    {
        using var pooled = StringBuilderPool.Rent(10 * 1024);
        AppendPromptSections(pooled.Builder);
        return pooled.ToString();
    }

    [Benchmark(Description = "new StringBuilder 10KB prompt assembly")]
    public string Prompt_New_10KB()
    {
        var sb = new StringBuilder(10 * 1024);
        AppendPromptSections(sb);
        return sb.ToString();
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

    // -- Helpers ----------------------------------------------------------------

    private void AppendPromptSections(StringBuilder sb)
    {
        string[] sections = _promptSections;
        for (int i = 0; i < sections.Length; i++)
        {
            sb.Append(sections[i]).Append('\n');
        }
    }

    /// <summary>
    ///     Many short sections summing to ~10 KiB — the shape of the real
    ///     <c>SystemPromptBuilder</c> output (one block per tool), not one giant string.
    /// </summary>
    private static string[] BuildPromptSections()
    {
        const int SectionCount = 64;
        var sections = new string[SectionCount];
        for (int i = 0; i < SectionCount; i++)
        {
            sections[i] =
                $"## tool_{i}: Description of tool number {i:D2}, its arguments and its guidelines "
                + "for when the model should call it, written out at realistic prompt length. ";
        }

        return sections;
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
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> count lines in a message body either via
///          <c>string.Split('\n')</c> or via a <c>ReadOnlySpan&lt;char&gt;</c> <c>IndexOf</c>
///          loop.</item>
///          <item><c>Payload:</c> a 10-line and a 100-line body (48-char lines, varying
///          content so branch prediction is realistic), built once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per invocation — the input strings are immutable and
///          read-only, so there is nothing to reset.</item>
///          <item><c>Drain:</c> none — both variants are synchronous and fully consumed.</item>
///          <item><c>RetainedState:</c> none.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the <c>Split</c> rows allocate a <c>string[]</c>
///          plus one <c>string</c> per line; the span rows allocate nothing. That delta is the
///          reason the span loop exists.</item>
/// </list>
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

// ---------------------------------------------------------------------------
// DictionaryPoolBenchmark (#1239)
// ---------------------------------------------------------------------------

/// <summary>
///     Benchmarks <see cref="DictionaryPool{TKey, TValue}" /> against raw
///     <c>new Dictionary&lt;TKey, TValue&gt;</c> for the per-call dictionaries the
///     message converters and tool-dispatcher argument maps build and throw away.
///     The pool's own rent/return cycle is measured on its own row too, so the
///     trade is visible rather than assumed: a dictionary is cheaper to construct
///     than a StringBuilder, so the pool has to earn its keep on reuse, not on
///     the rent.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one rent/fill/return cycle at 8 or 256 entries
///          against a raw <c>new Dictionary</c> baseline doing identical work, plus a
///          rent/return-only row that isolates the pool's own cost.</item>
///          <item><c>Payload:</c> pre-built <c>string</c> keys ("key_0" … "key_N") and
///          <c>int</c> values, so the hashing work is identical in every row.</item>
///          <item><c>StateReset:</c> per invocation — the wrapper is scoped to the op
///          via <c>using</c>, so the pooled dictionary is always returned before the
///          measurement ends.</item>
///          <item><c>Drain:</c> none — the dictionary is filled synchronously and read
///          back inside the same op.</item>
///          <item><c>RetainedState:</c> <b>the pool itself</b>, which is the thing under
///          test: a dictionary returned with N entries keeps its internal bucket
///          array, and the next rent of that band reuses it — that is the win being
///          measured, so it is intentionally not cleared between iterations.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> every row returns an <c>int</c>, which is not
///          boxed, so the pooled rows target 0 B per operation; the raw rows additionally
///          allocate the <c>Dictionary</c> + its <c>Entry</c> and bucket arrays. A pooled row
///          that shows allocations means a rent missed its band and re-allocated.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class DictionaryPoolBenchmark
{
    private const int EntryCount8 = 8;
    private const int EntryCount256 = 256;

    private string[] _keys8 = [];
    private string[] _keys256 = [];

    [GlobalSetup]
    public void Setup()
    {
        _keys8 = BuildKeys(EntryCount8);
        _keys256 = BuildKeys(EntryCount256);
    }

    // -- Pooled rent/fill/return -------------------------------------------------

    [Benchmark(Description = "Pooled 8-entry dictionary", Baseline = true)]
    public int Pooled_8Entries()
    {
        using var rented = DictionaryPool<string, int>.Rent(EntryCount8);
        Dictionary<string, int> map = rented.Dictionary;
        Fill(map, _keys8);
        return Sum(map, _keys8);
    }

    [Benchmark(Description = "Pooled 256-entry dictionary")]
    public int Pooled_256Entries()
    {
        using var rented = DictionaryPool<string, int>.Rent(EntryCount256);
        Dictionary<string, int> map = rented.Dictionary;
        Fill(map, _keys256);
        return Sum(map, _keys256);
    }

    [Benchmark(Description = "Pooled rent/return only (no entries)")]
    public int Pooled_RentReturnOnly()
    {
        using var rented = DictionaryPool<string, int>.Rent();
        return rented.Dictionary.Count;
    }

    // -- Raw new Dictionary baselines (no pooling) -------------------------------

    [Benchmark(Description = "new Dictionary 8 entries")]
    public int New_8Entries()
    {
        var map = new Dictionary<string, int>(EntryCount8);
        Fill(map, _keys8);
        return Sum(map, _keys8);
    }

    [Benchmark(Description = "new Dictionary 256 entries")]
    public int New_256Entries()
    {
        var map = new Dictionary<string, int>(EntryCount256);
        Fill(map, _keys256);
        return Sum(map, _keys256);
    }

    // -- Helpers ----------------------------------------------------------------

    private static void Fill(Dictionary<string, int> map, string[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            map[keys[i]] = i;
        }
    }

    private static int Sum(Dictionary<string, int> map, string[] keys)
    {
        int total = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            total += map[keys[i]];
        }

        return total;
    }

    private static string[] BuildKeys(int count)
    {
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = $"key_{i:D6}";
        }

        return keys;
    }
}
