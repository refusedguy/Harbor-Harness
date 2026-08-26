using System.Text;
using BenchmarkDotNet.Attributes;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Benchmarks;

/// <summary>
/// Cell-diff core costs (celldiff §7) against the frame budget of 16 ms:
/// idle frame via row-hash skip (~0.05 ms @ 200×50), typical token frame
/// with ~300 changed cells (0.5–1 ms), full repaint at 200×50 and 400×120
/// (~6 ms worst case), and the cold layout solve for a 20-panel tree
/// (&lt; 0.01 ms). All flushes go through the real DiffEngine + AnsiWriter
/// into a discarding backend — no tty I/O, encode cost included.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class DiffEngineBenchmark
{
    private sealed class NullBackend : ITerminalBackend
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class StubPanel : Panel
    {
        public StubPanel(string id, int minW, int minH, int priority = 0)
            : base(id, new Size(minW, minH), priority)
        {
        }

        public override void Paint(ScreenBuffer buffer)
        {
        }
    }

    private AnsiWriter _writer = null!;
    private DiffEngine _engineIdle = null!;
    private ScreenBuffer _backIdle = null!;

    private DiffEngine _engineToken = null!;
    private ScreenBuffer _backToken = null!;
    private string[] _bandA = null!;
    private string[] _bandB = null!;
    private int _bandIndex;

    private DiffEngine _engineFull200 = null!;
    private ScreenBuffer _backFull200 = null!;
    private DiffEngine _engineFull400 = null!;
    private ScreenBuffer _backFull400 = null!;

    private LayoutTree _tree = null!;

    [GlobalSetup]
    public void Setup()
    {
        _writer = new AnsiWriter(new NullBackend(), syncUpdates: true);

        (_engineIdle, _backIdle) = MakeSyncedScreen(200, 50);

        (_engineToken, _backToken) = MakeSyncedScreen(200, 50);
        _bandA = new string[16];
        _bandB = new string[16];
        var rng = new Random(7);
        for (int i = 0; i < 16; i++)
        {
            _bandA[i] = RandomText(rng, 200);
            _bandB[i] = RandomText(rng, 100);
        }

        (_engineFull200, _backFull200) = MakeSyncedScreen(200, 50);
        (_engineFull400, _backFull400) = MakeSyncedScreen(400, 120);

        _tree = BuildTree20Panels();
    }

    private static (DiffEngine Engine, ScreenBuffer Back) MakeSyncedScreen(int cols, int rows)
    {
        var writer = new AnsiWriter(new NullBackend(), syncUpdates: true);
        var engine = new DiffEngine(cols, rows);
        var back = new ScreenBuffer(cols, rows);
        for (int y = 0; y < rows; y++)
        {
            back.SetText(0, y, $"row {y} " + new string('.', Math.Max(0, cols - 8)), CellStyle.Plain);
        }

        writer.BeginFrame();
        engine.Flush(back, writer);
        writer.EndFrameAsync().AsTask().GetAwaiter().GetResult();
        return (engine, back);
    }

    private static string RandomText(Random rng, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            sb.Append((char)('a' + rng.Next(26)));
        }

        return sb.ToString();
    }

    [Benchmark(Description = "Idle frame (row-hash skip, 200x50)")]
    public async Task IdleFrame200x50()
    {
        _writer.BeginFrame();
        _engineIdle.Flush(_backIdle, _writer);
        await _writer.EndFrameAsync();
    }

    [Benchmark(Description = "Token frame (~300 changed cells, 200x50)")]
    public async Task TokenFrame300Cells200x50()
    {
        int i = ++_bandIndex & 15;
        _backToken.SetText(0, 24, _bandA[i], CellStyle.Plain);
        _backToken.SetText(0, 25, _bandB[i], CellStyle.Plain);
        _writer.BeginFrame();
        _engineToken.Flush(_backToken, _writer);
        await _writer.EndFrameAsync();
    }

    [Benchmark(Description = "Full repaint 200x50")]
    public async Task FullRepaint200x50()
    {
        _engineFull200.Front.BlankAll();
        _writer.BeginFrame();
        _engineFull200.Flush(_backFull200, _writer);
        await _writer.EndFrameAsync();
    }

    [Benchmark(Description = "Full repaint 400x120")]
    public async Task FullRepaint400x120()
    {
        _engineFull400.Front.BlankAll();
        _writer.BeginFrame();
        _engineFull400.Flush(_backFull400, _writer);
        await _writer.EndFrameAsync();
    }

    [Benchmark(Description = "Layout cold solve, 20 panels (alternating geometry defeats cache)")]
    public int LayoutColdSolve20Panels()
    {
        // Alternating sizes always miss the (w,h,capsVer) cache → solver path.
        bool flip = (++_bandIndex & 1) == 0;
        _tree.Solve(flip ? 200 : 201, flip ? 50 : 51);
        return _tree.Panels.Count;
    }

    private static LayoutTree BuildTree20Panels()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("p00", 2, 2));
        string lastId = "p00";
        for (int i = 1; i < 20; i++)
        {
            string id = $"p{i:00}";
            tree.Split(lastId, (i & 1) == 0 ? SplitDir.Horizontal : SplitDir.Vertical, 0.6f, new StubPanel(id, 2, 2));
            lastId = id;
        }

        return tree;
    }
}
