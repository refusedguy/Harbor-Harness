using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class DiffEngineTests
{
    private static (DiffEngine Engine, RecordingBackend Backend, AnsiWriter Writer) Make(int cols = 20, int rows = 6, bool sync = false)
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, sync);
        return (new DiffEngine(cols, rows), backend, writer);
    }

    [Test]
    public async Task FirstFlush_EmitsEveryVisibleCell()
    {
        var (engine, backend, writer) = Make(4, 1);
        var back = new ScreenBuffer(4, 1);
        back.SetText(0, 0, "ab", CellStyle.Plain);

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // FRONT starts blank; every non-blank cell must be emitted.
        await Assert.That(backend.Text.Contains("ab")).IsTrue();
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task IdleFrame_EmitsNothing()
    {
        var (engine, backend, writer) = Make();
        var back = new ScreenBuffer(20, 6);
        writer.BeginFrame();
        engine.Flush(back, writer);   // first: blanks sync
        await writer.EndFrameAsync();
        string firstFrame = backend.Text;

        backend.ResetForTests();
        writer.BeginFrame();
        engine.Flush(back, writer);   // idle: nothing changed
        await writer.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("");
        _ = firstFrame;
    }

    [Test]
    public async Task SmallChange_EmitsOnlyDelta()
    {
        var (engine, backend, writer) = Make(10, 2);
        var back = new ScreenBuffer(10, 2);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        backend.ResetForTests();

        back.SetText(3, 1, "x", CellStyle.Plain);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[2;4H\x1B[0mx");
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task StyleOnlyChange_RecolorsWithoutMovingText()
    {
        var (engine, backend, writer) = Make(10, 1);
        var back = new ScreenBuffer(10, 1);
        back.SetText(0, 0, "hi", CellStyle.Plain);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        backend.ResetForTests();

        back.SetStyleAt(1, 0, new CellStyle(attrs: StyleAttr.Underline));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // Only the recolored cell re-emitted, with SGR delta around it.
        await Assert.That(backend.Text.Contains("\x1B[4m")).IsTrue();
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task WideRuneChange_RepaintsLead_SkipsTailEmit()
    {
        var (engine, backend, writer) = Make(8, 1);
        var back = new ScreenBuffer(8, 1);
        var cjk = new Rune(0x4E2D);

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        backend.ResetForTests();

        back.SetRune(0, 0, cjk, CellStyle.Plain);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // Lead rune emitted once; tail mirrored silently.
        string text = backend.Text;
        await Assert.That(text.Contains((char)0x4E2D)).IsTrue();
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task NarrowOverWide_ForceRepaintsBothHalves()
    {
        var (engine, backend, writer) = Make(10, 1);
        var back = new ScreenBuffer(10, 1);
        var cjk = new Rune(0x4E2D);
        back.SetRune(0, 0, cjk, CellStyle.Plain);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        backend.ResetForTests();

        // Replace the wide pair with two narrow chars.
        back.SetText(0, 0, "XY", CellStyle.Plain);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        string text = backend.Text;
        await Assert.That(text.Contains('X')).IsTrue();
        await Assert.That(text.Contains('Y')).IsTrue(); // tail half repainted — no ghost glyph
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task RowHashSkip_SilentRowsCostNothing()
    {
        var (engine, backend, writer) = Make(200, 50);
        var back = new ScreenBuffer(200, 50);
        // Realistic content so the baseline frame carries real payload.
        for (int y = 0; y < 50; y++)
        {
            back.SetText(0, y, $"row {y} ".PadRight(199, '.'), CellStyle.Plain);
        }

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        long first = backend.TotalBytes;
        await Assert.That(first > 10_000).IsTrue();

        // Mutate a single row out of 50.
        back.Fill(new Rect(0, 25, 200, 1), Cell.From(new Rune('#'), CellStyle.Plain));
        backend.ResetForTests();
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        long second = backend.TotalBytes;

        // Silent rows skipped → second frame is a small fraction of the first.
        await Assert.That(second < first / 5).IsTrue();
    }

    [Test]
    public async Task FrameHint_SmallDamage_ScansOnlyHintedArea()
    {
        var (engine, backend, writer) = Make(40, 20);
        var back = new ScreenBuffer(40, 20);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        backend.ResetForTests();

        back.SetText(5, 5, "tick", CellStyle.Plain);
        engine.FrameHint(new Rect(0, 5, 40, 1));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        await Assert.That(backend.Text.Contains("tick")).IsTrue();
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task FrameHint_MutationOutsideHint_IsMissedByHintPath_ButCaughtByFull()
    {
        var (engine, backend, writer) = Make(20, 10);
        var back = new ScreenBuffer(20, 10);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // Mutation far outside the hint.
        back.SetText(15, 8, "miss", CellStyle.Plain);
        engine.FrameHint(new Rect(0, 0, 20, 1));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        backend.ResetForTests();

        // Hint path missed it — FRONT does not match BACK...
        await Assert.That(engine.FrontMatches(back)).IsFalse();

        // ...and the next hintless flush falls back to full scan and heals.
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        await Assert.That(engine.FrontMatches(back)).IsTrue();
        await Assert.That(backend.Text.Contains("miss")).IsTrue();
    }

    [Test]
    public async Task HintAreaAboveThreshold_FallsBackToFullScan()
    {
        var (engine, backend, writer) = Make(10, 4);
        var back = new ScreenBuffer(10, 4);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        backend.ResetForTests();

        back.Fill(new Rect(0, 0, 10, 4), Cell.From(new Rune('*'), CellStyle.Plain));
        engine.FrameHint(new Rect(0, 0, 10, 4)); // 100 % of screen
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // Full-scan fallback still produces the correct frame.
        await Assert.That(engine.FrontMatches(back)).IsTrue();
        await Assert.That(backend.Text.Count(c => c == '*')).IsEqualTo(40);
    }

    [Test]
    public async Task FrameHint_OverlappingRects_MergeIntoOne()
    {
        var (engine, backend, writer) = Make(20, 10);
        var back = new ScreenBuffer(20, 10);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        back.SetText(2, 2, "abcdefgh", CellStyle.Plain);
        engine.FrameHint(new Rect(0, 2, 4, 1)); // x ∈ [0..4)
        engine.FrameHint(new Rect(3, 2, 4, 1)); // overlaps → merged into x ∈ [0..7)
        engine.FrameHint(new Rect(100, 100, 5, 5)); // fully off-screen → dropped
        await Assert.That(engine.HintArea()).IsEqualTo(7); // union width 7, not 4+4

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        await Assert.That(backend.Text.Contains("abcde")).IsTrue();
        await Assert.That(backend.Text.Contains('h')).IsFalse(); // x=9 sits outside the union
    }

    [Test]
    public async Task FrameHint_ClipsToScreen()
    {
        var (engine, backend, writer) = Make(10, 5);
        var back = new ScreenBuffer(10, 5);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        back.SetText(8, 4, "tail", CellStyle.Plain);
        engine.FrameHint(new Rect(5, 3, 50, 50)); // extends past the bottom-right corner
        await Assert.That(engine.HintArea()).IsEqualTo(10); // 5×2 clipped window, not 2500
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task HintedFullWidthRow_AdoptsHash_PartialRow_Invalidates()
    {
        var engine = new DiffEngine(10, 2);
        var back = new ScreenBuffer(10, 2);
        var writer = new AnsiWriter(new RecordingBackend(), syncUpdates: true);

        // Full-width hint → FRONT row hash cache must be adopted (authoritative).
        back.SetText(0, 0, "full", CellStyle.Plain);
        engine.FrameHint(new Rect(0, 0, 10, 1));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        bool adoptedAfterFullRow = engine.Front.IsRowHashValid(0);

        // Partial-width hint → cells outside the span are unscanned; FRONT's
        // cache must be invalidated instead of adopted, or a later flush could
        // skip the row on a stale hash.
        back.SetText(6, 1, "part", CellStyle.Plain);
        engine.FrameHint(new Rect(4, 1, 4, 1));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        await Assert.That(adoptedAfterFullRow).IsTrue();
        await Assert.That(engine.Front.IsRowHashValid(1)).IsFalse();
        await Assert.That(engine.FrontMatches(back)).IsFalse(); // cols [0..4) of row 1 unscanned
    }

    [Test]
    public async Task HintBoundary_SplittingWidePair_RepairsLeadHalf()
    {
        var (engine, backend, writer) = Make(10, 1);
        var back = new ScreenBuffer(10, 1);
        var cjk = new Rune(0x4E2D); // wide: lead + tail pair

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        backend.ResetForTests();

        // Paint the pair at columns 2..3 but hint ONLY the tail half.
        back.SetRune(2, 0, cjk, CellStyle.Plain);
        engine.FrameHint(new Rect(3, 0, 7, 1));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // The lead half must have been repaired — no ghost glyph survives.
        await Assert.That(engine.FrontMatches(back)).IsTrue();
        await Assert.That(backend.Text.Contains((char)0x4E2D)).IsTrue();
    }
}
