using System.Text;
using BenchmarkDotNet.Attributes;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Benchmarks;

/// <summary>
/// Cell-diff core costs (celldiff §7) against the frame budget of 16 ms:
/// idle frame via row-hash skip (~0.05 ms @ 200×50), typical token frame
/// with ~300 changed cells (0.5–1 ms), full repaint at 200×50 and 400×120
/// (~6 ms worst case), and the cold layout solve for a 20-panel tree
/// (&lt; 0.01 ms). All flushes go through the real DiffEngine + AnsiWriter
/// into a discarding backend — no tty I/O, encode cost included.
/// </summary>
/// <remarks>
/// The <see cref="StreamingDelta_Unpaced1000Fps" /> and
/// <see cref="StreamingDelta_Paced60Fps" /> scenarios measure the streaming
/// acceptance (perf sprint §3.10): ANSI bytes per 1000 text deltas — each op
/// types 1000 deltas (~5 chars, content varies per delta so every frame
/// carries real change) into a chat tail and returns the total ANSI byte
/// count emitted <b>during the stream</b> (the one-time initial paint is
/// excluded via <c>CountingBackend.Reset</c>). The value is the ANSI bytes/sec
/// budget at 1000 deltas/sec; target &lt; 10 000 B for the paced path
/// (vs ~1920 cells × full redraw before the diff engine). BDN does not
/// surface benchmark return values in its summary table, so the validated
/// per-run minimum/maximum is echoed into the log by
/// <see cref="ReportStreamBytes" /> (min = paced, max = unpaced).
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class DiffEngineBenchmark
{
    private sealed class NullBackend : ITerminalBackend
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class CountingBackend : ITerminalBackend
    {
        public long TotalBytes;

        public void Reset() => TotalBytes = 0;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            TotalBytes += bytes.Length;
            return ValueTask.CompletedTask;
        }
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

    private long _minStreamBytes = long.MaxValue;
    private long _maxStreamBytes;

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

    private static (DiffEngine Engine, ScreenBuffer Back, AnsiWriter Writer, CountingBackend Backend)
        MakeCountedScreen(int cols, int rows)
    {
        var backend = new CountingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(cols, rows);
        var back = new ScreenBuffer(cols, rows);
        for (int y = 0; y < rows; y++)
        {
            back.SetText(0, y, $"row {y} " + new string('.', Math.Max(0, cols - 8)), CellStyle.Plain);
        }

        writer.BeginFrame();
        engine.Flush(back, writer);
        writer.EndFrameAsync().AsTask().GetAwaiter().GetResult();
        backend.Reset(); // exclude the one-time initial paint from the streaming metric
        return (engine, back, writer, backend);
    }

    /// <summary>
    ///     Mutates the first delta char per step so every typed delta changes
    ///     cells (a constant pattern would converge with the residue and emit
    ///     nothing after the first sweep — an unrealistic best case).
    /// </summary>
    private static void Vary(char[] delta, int step) => delta[0] = (char)('a' + (step & 15));

    /// <summary>
    ///     Types one ~5-char delta into the chat tail: appends at the pen,
    ///     advancing to the next row when the line is full — mirroring how a
    ///     streaming chat tail actually grows (no synthetic clear+rewrite).
    /// </summary>
    private static void TypeDelta(ScreenBuffer back, char[] delta, ref int col, ref int row, int step)
    {
        if (col + delta.Length >= 190)
        {
            col = 8;
            row++;
        }

        Vary(delta, step);
        back.SetText(col, row, delta, CellStyle.Plain);
        col += delta.Length;
    }

    /// <summary>Worst case: every delta flushes its own frame (no pacing).
    /// Returns ANSI bytes emitted for the 1000-delta stream (validated).</summary>
    [Benchmark(Description = "Streaming 1000 deltas, 1 frame each (ANSI bytes/sec)")]
    public async Task<long> StreamingDelta_Unpaced1000Fps()
    {
        var (engine, back, writer, backend) = MakeCountedScreen(200, 50);
        var delta = new char[] { 'x', 'y', 'z', '0', '9' };
        int col = 8;
        int row = 21; // 28 tail rows hold 1000 deltas of ~5 chars
        for (int i = 0; i < 1000; i++)
        {
            TypeDelta(back, delta, ref col, ref row, i);

            writer.BeginFrame();
            engine.Flush(back, writer);
            await writer.EndFrameAsync();
        }

        TrackStreamBytes(backend.TotalBytes);
        return backend.TotalBytes;
    }

    /// <summary>Paced case: ~1000 deltas/sec batched into ~60 frames/sec —
    /// the renderer's commit-pacing path and the &lt; 10 KB/s acceptance.</summary>
    [Benchmark(Description = "Streaming 1000 deltas paced to 60 fps (ANSI bytes/sec)")]
    public async Task<long> StreamingDelta_Paced60Fps()
    {
        var (engine, back, writer, backend) = MakeCountedScreen(200, 50);
        var delta = new char[] { 'x', 'y', 'z', '0', '9' };
        int col = 8;
        int row = 21;
        int step = 0;
        for (int frame = 0; frame < 60; frame++)
        {
            for (int d = 0; d < 17; d++)
            {
                TypeDelta(back, delta, ref col, ref row, step++);
            }

            writer.BeginFrame();
            engine.Flush(back, writer);
            await writer.EndFrameAsync();
        }

        TrackStreamBytes(backend.TotalBytes);
        return backend.TotalBytes;
    }

    private void TrackStreamBytes(long bytes)
    {
        if (bytes < _minStreamBytes)
        {
            _minStreamBytes = bytes;
        }

        if (bytes > _maxStreamBytes)
        {
            _maxStreamBytes = bytes;
        }
    }

    /// <summary>Echos the validated ANSI-bytes-per-1000-deltas acceptance
    /// number into the run log (BDN does not print benchmark return values).</summary>
    [GlobalCleanup]
    public void ReportStreamBytes() => Console.WriteLine(
        $"// STREAM-BYTES (acceptance < 10000 B per 1000 deltas): min={_minStreamBytes} max={_maxStreamBytes}");

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
