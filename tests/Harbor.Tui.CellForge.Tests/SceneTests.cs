using System.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2353: a recorded display list must be indistinguishable from the paint
/// code it replaces. Frame equivalence is checked at the grid level and at
/// the ANSI byte level through DiffEngine.
/// </summary>
public class SceneTests
{
    private static (EngineCells.DiffEngine Engine, RecordingBackend Backend, EngineCells.AnsiWriter Writer) Make(
        int cols, int rows)
    {
        var backend = new RecordingBackend();
        var writer = new EngineCells.AnsiWriter(backend);
        return (new EngineCells.DiffEngine(cols, rows), backend, writer);
    }

    private static EngineCells.CellStyle Plain => EngineCells.CellStyle.Plain;

    [Test]
    public async Task ReplayTo_MatchesDirectPainting_CellForCell()
    {
        const int cols = 12;
        const int rows = 4;
        var bold = new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Bold);
        var underline = new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Underline);
        var dotted = EngineCells.Cell.From(new Rune('.'), Plain);

        var scene = new EngineCells.Scene();
        scene.Fill(new EngineCells.Rect(0, 0, 4, 2), dotted);
        scene.SetRune(5, 0, new Rune(0x4E2D), Plain);
        scene.SetText(1, 2, "hello", bold);
        scene.SetStyleAt(2, 2, underline);
        scene.SetDiffOption(1, 2, EngineCells.CellDiffOption.AlwaysUpdate);

        var replayed = new EngineCells.ScreenBuffer(cols, rows);
        scene.ReplayTo(replayed);

        var direct = new EngineCells.ScreenBuffer(cols, rows);
        direct.Fill(new EngineCells.Rect(0, 0, 4, 2), dotted);
        direct.SetRune(5, 0, new Rune(0x4E2D), Plain);
        direct.SetText(1, 2, "hello", bold);
        direct.SetStyleAt(2, 2, underline);
        direct.SetDiffOption(1, 2, EngineCells.CellDiffOption.AlwaysUpdate);

        await Assert.That(replayed.ContentEquals(direct)).IsTrue();
        await Assert.That(replayed.Get(6, 0).Width).IsEqualTo(EngineCells.Cell.WSkip);
        await Assert.That(replayed.GetDiffOption(1, 2))
            .IsEqualTo(EngineCells.CellDiffOption.AlwaysUpdate);
    }

    [Test]
    public async Task Replay_FromCachedScene_ReusesOpsWithoutRepainting()
    {
        const int cols = 12;
        const int rows = 3;
        int paintCalls = 0;
        var cached = new EngineCells.Scene();
        var header = cached.Record(s =>
        {
            paintCalls++;
            s.SetText(0, 0, "HEADER", Plain);
        });
        var footer = cached.Record(s =>
        {
            paintCalls++;
            s.SetText(0, 2, "FOOTER", Plain);
        });

        var frame = new EngineCells.Scene();
        var replayedHeader = frame.Replay(cached, header);
        frame.SetText(0, 1, "BODY", Plain);
        var replayedFooter = frame.Replay(cached, footer);

        await Assert.That(paintCalls).IsEqualTo(2);
        await Assert.That(frame.Count).IsEqualTo(header.Count + 1 + footer.Count);
        await Assert.That(replayedHeader.Count).IsEqualTo(header.Count);
        await Assert.That(replayedFooter.Start).IsEqualTo(frame.Count - footer.Count);
        await Assert.That(frame[replayedHeader.Start]).IsEqualTo(cached[header.Start]);

        var replayed = new EngineCells.ScreenBuffer(cols, rows);
        frame.ReplayTo(replayed);
        var direct = new EngineCells.ScreenBuffer(cols, rows);
        direct.SetText(0, 0, "HEADER", Plain);
        direct.SetText(0, 1, "BODY", Plain);
        direct.SetText(0, 2, "FOOTER", Plain);
        await Assert.That(replayed.ContentEquals(direct)).IsTrue();

        // Next frame reuses the same recorded ranges: paint delegates stay at 2.
        var next = new EngineCells.Scene();
        next.Replay(cached, header);
        next.SetText(0, 1, "BODY2", Plain);
        next.Replay(cached, footer);
        await Assert.That(paintCalls).IsEqualTo(2);

        var nextGrid = new EngineCells.ScreenBuffer(cols, rows);
        next.ReplayTo(nextGrid);
        var nextDirect = new EngineCells.ScreenBuffer(cols, rows);
        nextDirect.SetText(0, 0, "HEADER", Plain);
        nextDirect.SetText(0, 1, "BODY2", Plain);
        nextDirect.SetText(0, 2, "FOOTER", Plain);
        await Assert.That(nextGrid.ContentEquals(nextDirect)).IsTrue();
    }

    [Test]
    public async Task ReplayTo_WithRange_AppliesOnlyThatRange()
    {
        var scene = new EngineCells.Scene();
        _ = scene.Record(s => s.SetText(0, 0, "FIRST", Plain));
        var second = scene.Record(s => s.SetText(0, 1, "SECOND", Plain));
        scene.Record(s => s.SetText(0, 2, "THIRD", Plain));

        var replayed = new EngineCells.ScreenBuffer(10, 3);
        scene.ReplayTo(replayed, second);

        var direct = new EngineCells.ScreenBuffer(10, 3);
        direct.SetText(0, 1, "SECOND", Plain);
        await Assert.That(replayed.ContentEquals(direct)).IsTrue();
    }

    [Test]
    public async Task FlushScene_MatchesDirectFlush_ByteForByte()
    {
        const int cols = 12;
        const int rows = 3;
        var (sceneEngine, sceneBackend, sceneWriter) = Make(cols, rows);
        var (directEngine, directBackend, directWriter) = Make(cols, rows);

        var star = EngineCells.Cell.From(new Rune('*'), Plain);

        var frame1 = new EngineCells.Scene();
        frame1.SetText(0, 0, "ALPHA", Plain);
        frame1.Fill(new EngineCells.Rect(0, 2, 5, 1), star);
        var direct1 = new EngineCells.ScreenBuffer(cols, rows);
        direct1.SetText(0, 0, "ALPHA", Plain);
        direct1.Fill(new EngineCells.Rect(0, 2, 5, 1), star);

        sceneWriter.BeginFrame();
        sceneEngine.FlushScene(frame1, sceneWriter);
        await sceneWriter.EndFrameAsync();
        directWriter.BeginFrame();
        directEngine.Flush(direct1, directWriter);
        await directWriter.EndFrameAsync();

        await Assert.That(sceneBackend.Text).IsEqualTo(directBackend.Text);
        await Assert.That(sceneEngine.FrontMatches(direct1)).IsTrue();

        sceneBackend.ResetForTests();
        directBackend.ResetForTests();
        var frame2 = new EngineCells.Scene();
        frame2.SetText(0, 0, "BETA", Plain);
        frame2.Fill(new EngineCells.Rect(0, 2, 5, 1), star);
        var direct2 = new EngineCells.ScreenBuffer(cols, rows);
        direct2.SetText(0, 0, "BETA", Plain);
        direct2.Fill(new EngineCells.Rect(0, 2, 5, 1), star);

        sceneWriter.BeginFrame();
        sceneEngine.FlushScene(frame2, sceneWriter);
        await sceneWriter.EndFrameAsync();
        directWriter.BeginFrame();
        directEngine.Flush(direct2, directWriter);
        await directWriter.EndFrameAsync();

        await Assert.That(sceneBackend.Text).IsEqualTo(directBackend.Text);
        await Assert.That(sceneEngine.FrontMatches(direct2)).IsTrue();
    }
}
