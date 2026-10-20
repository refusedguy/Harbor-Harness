using BenchmarkDotNet.Attributes;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Benchmarks;

/// <summary>
/// PF1 acceptance (issue #1237): hand-rolled SIMD cell equality vs the scalar
/// five-compare loop, at terminal widths where the win is NOT obvious
/// (80/120/200 cols × 50 rows), plus the AnsiWriter VS16 text path that the
/// vectorized probe rides. Run with the Benchmarks `--filter` for this class.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
/// <item><c>Operation:</c> one full-screen equality check over identical buffers
/// (<c>ScalarEquals</c> = local per-cell <c>Get != Get</c> loop, the pre-PF1 body of
/// <c>DiffEngine.FrontMatches</c>; <c>SimdFrontMatches</c> = production
/// <c>DiffEngine.FrontMatches</c> via <c>CellSimd</c>), or one
/// <c>AnsiWriter.WriteText</c> call over a pre-built 120-char run.</item>
/// <item><c>Payload:</c> pre-built screens (<c>Cols × 50</c>, identical content so the
/// scan covers every cell — worst case, no early exit) and one pre-built 120-char
/// ASCII run (no VS16, so the probe scans the whole span).</item>
/// <item><c>StateReset:</c> none per iteration — the buffers are read-only during the
/// op; the writer row calls <c>BeginFrame()</c> first so the frame buffer is reused,
/// never grown, inside the measurement.</item>
/// <item><c>Drain:</c> none — the writer backend discards bytes synchronously
/// (<c>ValueTask.CompletedTask</c>), so no tty I/O is measured.</item>
/// <item><c>RetainedState:</c> the synced front/back <c>ScreenBuffer</c> pair per
/// <c>Cols</c> value; the writer + text run for the text row.</item>
/// <item><c>AwaitSemantics:</c> n/a — no async inside any row.</item>
/// <item><c>AllocAttribution:</c> expected zero allocs on the equality rows (span +
/// vector locals only); the text row amortizes the reusable frame buffer.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class CellSimdBenchmark
{
    [Params(80, 120, 200)]
    public int Cols { get; set; }

    private DiffEngine _engine = null!;
    private ScreenBuffer _back = null!;
    private AnsiWriter _writer = null!;
    private string _text = null!;

    [GlobalSetup]
    public void Setup()
    {
        var writer = new AnsiWriter(new NullTerminalBackend(), syncUpdates: false);
        var engine = new DiffEngine(Cols, 50);
        var back = new ScreenBuffer(Cols, 50);
        for (int y = 0; y < 50; y++)
        {
            back.SetText(0, y, $"row {y} " + new string('.', Math.Max(0, Cols - 8)), CellStyle.Plain);
        }

        writer.BeginFrame();
        engine.Flush(back, writer);
        _engine = engine;
        _back = back;

        _writer = new AnsiWriter(new NullTerminalBackend(), syncUpdates: false);
        _text = new string('a', 120);
    }

    /// <summary>Pre-PF1 scalar body: per-cell Get loop, full scan, no early exit.</summary>
    [Benchmark(Baseline = true, Description = "Scalar cell compare (Get loop)")]
    public bool ScalarEquals()
    {
        var front = _engine.Front;
        for (int y = 0; y < _back.Rows; y++)
        {
            for (int x = 0; x < _back.Cols; x++)
            {
                if (front.Get(x, y) != _back.Get(x, y))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Production path: DiffEngine.FrontMatches via CellSimd (Vector{ulong}).</summary>
    [Benchmark(Description = "SIMD FrontMatches (Vector<ulong>)")]
    public bool SimdFrontMatches() => _engine.FrontMatches(_back);

    /// <summary>AnsiWriter text path carrying the vectorized VS16 probe.</summary>
    [Benchmark(Description = "WriteText 120 chars (VS16 probe)")]
    public void WriteText120()
    {
        _writer.BeginFrame();
        _writer.WriteText(_text);
    }
}
