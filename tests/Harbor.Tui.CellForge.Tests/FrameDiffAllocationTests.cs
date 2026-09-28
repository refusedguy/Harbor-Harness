using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// ENG1 moat: the diff/draw split stays zero-alloc in steady state, emits one
/// backend write per frame (single Refresh), skips MoveTo on adjacent cells,
/// and keeps the hinted/full-scan byte-identical contract at the iterator
/// level. Allocation probes use a counting backend (no copies) and the sync
/// <c>EndFrame</c> path so no async machinery pollutes the measurement.
/// </summary>
public class FrameDiffAllocationTests
{
    /// <summary>Backend that counts writes/bytes without copying — keeps
    /// allocation probes clean while exercising the full encode path.</summary>
    private sealed class CountingBackend : ISyncTerminalBackend
    {
        public int Writes { get; private set; }

        public long Bytes { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Writes++;
            Bytes += bytes.Length;
            return ValueTask.CompletedTask;
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            Writes++;
            Bytes += bytes.Length;
        }
    }

    private static (DiffEngine Engine, ScreenBuffer BackA, ScreenBuffer BackB, AnsiWriter Writer, CountingBackend Backend)
        MakeSteadyPair(int cols = 80, int rows = 24)
    {
        var backend = new CountingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(cols, rows);
        var backA = new ScreenBuffer(cols, rows);
        var backB = new ScreenBuffer(cols, rows);
        for (int y = 0; y < rows; y++)
        {
            backA.SetText(0, y, $"row {y} ".PadRight(cols - 1, '.'), CellStyle.Plain);
            backB.SetText(0, y, $"row {y} ".PadRight(cols - 1, '.'), CellStyle.Plain);
        }

        backB.SetText(0, 0, "#", CellStyle.Plain);
        return (engine, backA, backB, writer, backend);
    }

    [Test]
    public async Task Flush_SteadyState_IsAllocationFree()
    {
        var (engine, backA, backB, writer, _) = MakeSteadyPair();

        // Warmup: JIT tier-up + writer buffer growth + row-hash caches settle.
        for (int i = 0; i < 2_000; i++)
        {
            writer.BeginFrame();
            if ((i & 1) == 0)
            {
                engine.Flush(backA, writer);
            }
            else
            {
                engine.FrameHint(new Rect(0, 0, 80, 1));
                engine.Flush(backB, writer);
            }

            writer.EndFrame();
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int frames = 2_000;
        for (int i = 0; i < frames; i++)
        {
            writer.BeginFrame();
            if ((i & 1) == 0)
            {
                engine.Flush(backA, writer);
            }
            else
            {
                engine.FrameHint(new Rect(0, 0, 80, 1));
                engine.Flush(backB, writer);
            }

            writer.EndFrame();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task Diff_Drain_IsAllocationFree()
    {
        var (engine, backA, backB, _, _) = MakeSteadyPair();

        for (int i = 0; i < 2_000; i++)
        {
            var cursor = engine.Diff((i & 1) == 0 ? backA : backB).GetEnumerator();
            while (cursor.MoveNext())
            {
                _ = cursor.X + cursor.Y + cursor.Target.Rune;
            }
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int frames = 2_000;
        for (int i = 0; i < frames; i++)
        {
            var cursor = engine.Diff((i & 1) == 0 ? backA : backB).GetEnumerator();
            while (cursor.MoveNext())
            {
                _ = cursor.X + cursor.Y + cursor.Target.Rune;
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task Flush_UsesSingleBackendWritePerFrame()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(20, 6);
        var back = new ScreenBuffer(20, 6);
        back.SetText(0, 0, "hello", CellStyle.Plain);
        back.SetText(0, 5, "world", CellStyle.Plain);

        // Two disjoint damage rects still leave through a single Refresh.
        engine.FrameHint(new Rect(0, 0, 20, 1));
        engine.FrameHint(new Rect(0, 5, 20, 1));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        await Assert.That(backend.Writes.Count).IsEqualTo(1);

        // Idle frame carries nothing — the Refresh is dropped entirely.
        backend.ResetForTests();
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        await Assert.That(backend.Writes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AdjacentCells_ShareSingleMoveTo()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: false);
        var engine = new DiffEngine(10, 1);
        var back = new ScreenBuffer(10, 1);
        back.SetText(0, 0, "ab", CellStyle.Plain);

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // One CUP for the run; the adjacent cell skips MoveTo, the cached
        // plain style emits a single SGR reset.
        await Assert.That(backend.Text).IsEqualTo("\x1B[1;1H\x1B[0mab");
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task AlwaysUpdate_YieldsFullGrid_WhileDeltaYieldsNothing()
    {
        var engine = new DiffEngine(6, 2);
        var back = new ScreenBuffer(6, 2);
        back.SetText(0, 0, "abcdef", CellStyle.Plain);
        back.SetText(0, 1, "ghijkl", CellStyle.Plain);
        var writer = new AnsiWriter(new RecordingBackend(), syncUpdates: false);
        writer.BeginFrame();
        engine.Flush(back, writer);
        writer.EndFrame();

        int delta = 0;
        var deltaCursor = engine.Diff(back, FrameDiffMode.Delta).GetEnumerator();
        while (deltaCursor.MoveNext())
        {
            delta++;
        }

        int full = 0;
        int firstX = -1, firstY = -1, lastX = -1, lastY = -1;
        var fullCursor = engine.Diff(back, FrameDiffMode.AlwaysUpdate).GetEnumerator();
        while (fullCursor.MoveNext())
        {
            if (full == 0)
            {
                firstX = fullCursor.X;
                firstY = fullCursor.Y;
            }

            lastX = fullCursor.X;
            lastY = fullCursor.Y;
            full++;
        }

        await Assert.That(delta).IsEqualTo(0);
        await Assert.That(full).IsEqualTo(12);
        await Assert.That(firstX).IsEqualTo(0);
        await Assert.That(firstY).IsEqualTo(0);
        await Assert.That(lastX).IsEqualTo(5);
        await Assert.That(lastY).IsEqualTo(1);
    }

    [Test]
    public async Task HintedDrain_MatchesFullDrain()
    {
        var engineFull = new DiffEngine(20, 6);
        var engineHint = new DiffEngine(20, 6);
        var baseline = new ScreenBuffer(20, 6);
        baseline.SetText(0, 0, "baseline content row", CellStyle.Plain);
        var primer = new AnsiWriter(new RecordingBackend(), syncUpdates: false);
        primer.BeginFrame();
        engineFull.Flush(baseline, primer);
        primer.EndFrame();
        primer.BeginFrame();
        engineHint.Flush(baseline, primer);
        primer.EndFrame();

        var mutated = new ScreenBuffer(20, 6);
        mutated.SetText(0, 0, "baseline content row", CellStyle.Plain);
        mutated.SetText(5, 5, "tick", CellStyle.Plain);

        var fullCells = new List<(int X, int Y, Cell Cell)>();
        var fullCursor = engineFull.Diff(mutated).GetEnumerator();
        while (fullCursor.MoveNext())
        {
            fullCells.Add((fullCursor.X, fullCursor.Y, fullCursor.Target));
        }

        engineHint.FrameHint(new Rect(0, 5, 20, 1));
        var hintCells = new List<(int X, int Y, Cell Cell)>();
        var hintCursor = engineHint.Diff(mutated).GetEnumerator();
        while (hintCursor.MoveNext())
        {
            hintCells.Add((hintCursor.X, hintCursor.Y, hintCursor.Target));
        }

        await Assert.That(hintCells.Count).IsEqualTo(fullCells.Count);
        for (int i = 0; i < fullCells.Count; i++)
        {
            await Assert.That(hintCells[i]).IsEqualTo(fullCells[i]);
        }

        await Assert.That(engineFull.FrontMatches(mutated)).IsTrue();
        await Assert.That(engineHint.FrontMatches(mutated)).IsTrue();
    }
}
