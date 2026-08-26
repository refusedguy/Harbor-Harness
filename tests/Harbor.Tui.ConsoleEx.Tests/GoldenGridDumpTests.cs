using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Golden grid-dump suite (celldiff §8): every scenario renders through the
/// real DiffEngine + AnsiWriter into a recording backend, then compares the
/// three-layer dump (row art, exact cell map, escaped ANSI frames) against
/// <c>tests/fixtures/celldiff/*.golden.txt</c>. Regenerate with
/// HARBOR_UPDATE_GOLDENS=1; companion SVGs are written for human review.
/// </summary>
public class GoldenGridDumpTests
{
    [Test]
    public async Task EmptyScreen_IdleFrameCostsNothing()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(20, 6);
        var back = new ScreenBuffer(20, 6);

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        string doc = GoldenDoc.Build("empty-screen", back, backend);
        string expected = Golden.Verify("empty-screen", doc, GridDump.ToSvg(back));
        await Assert.That(doc).IsEqualTo(expected);
        // The dropped empty frame is the point of this scenario: zero bytes out.
        await Assert.That(backend.Writes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BorderPanel_LayoutSolve_PaintsGoldenFrame()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var session = new ScreenSession(writer, 24, 8);

        var tree = new LayoutTree();
        var chat = new BorderPanel("chat", 4, 3, priority: 5, title: "Chat");
        var status = new BorderPanel("status", 4, 1, priority: int.MaxValue, title: "ready");
        tree.AddRoot(chat);
        tree.Split("chat", SplitDir.Vertical, 0.7f, status, gap: 1);
        tree.Solve(session.CurrentCols, session.CurrentRows);
        chat.Focused = true;

        session.BeginFrame();
        foreach (var panel in tree.Panels)
        {
            panel.Paint(session.Back);
        }

        await session.FlushFrameAsync();

        string doc = GoldenDoc.Build("border-panel", session.Back, backend);
        string expected = Golden.Verify("border-panel", doc, GridDump.ToSvg(session.Back));
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();
    }

    [Test]
    public async Task WideCharBoundary_AndOverwriteRepaint_Golden()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(10, 3);
        var back = new ScreenBuffer(10, 3);

        // Frame 1: CJK lead+tail pair, emoji past the BMP, wide rune at the
        // last fitting column, a wide rune that does not fit before the right
        // edge (ratatui skip policy), and a mixed narrow/wide text run.
        back.SetRune(0, 0, new Rune(0x4E2D), CellStyle.Plain);          // 中 at 0..1
        back.SetRune(3, 0, new Rune(0x1F44D), CellStyle.Plain);         // 👍 at 3..4
        back.SetRune(7, 0, new Rune(0x3042), CellStyle.Plain);          // あ at 7..8
        back.SetRune(9, 1, new Rune(0x4E2D), CellStyle.Plain);          // tail would fall off → skip
        back.SetText(2, 2, "ok中!", CellStyle.Plain);                    // mixed run on row 2

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        // Frame 2 (delta): clobber the LEFT half of 中 with a styled narrow
        // char — both halves must repaint; then place あ at the very last
        // fitting column of row 0 (lead 8, tail 9).
        back.SetRune(0, 0, new Rune('X'), new CellStyle(attrs: StyleAttr.Reverse));
        back.SetRune(8, 0, new Rune(0x3042), CellStyle.Plain);

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        string doc = GoldenDoc.Build("wide-char-boundary", back, backend);
        string expected = Golden.Verify("wide-char-boundary", doc, GridDump.ToSvg(back));
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(engine.FrontMatches(back)).IsTrue();

        // Ghost check: the surviving half of the clobbered pair was repainted
        // as part of the forced redraw — FRONT mirrors it exactly.
        await Assert.That(engine.Front.Get(1, 0)).IsEqualTo(back.Get(1, 0));
    }

    [Test]
    public async Task ResizeShrink_EmitsEd2InsideSyncWrapper_Golden()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var session = new ScreenSession(writer, 40, 8);

        session.Back.FillAll(Cell.From(new Rune('#'), CellStyle.Plain));
        session.Back.SetText(1, 0, "harbor", CellStyle.Plain);
        session.BeginFrame();
        await session.FlushFrameAsync();

        session.Resize(15, 8); // horizontal shrink ⇒ erase-in-display first
        session.BeginFrame();
        session.Back.Fill(new Rect(0, 0, 15, 8), Cell.From(new Rune('+'), CellStyle.Plain));
        session.Back.SetText(1, 0, "harbor15", CellStyle.Plain);
        await session.FlushFrameAsync();

        string doc = GoldenDoc.Build("resize-shrink", session.Back, backend);
        string expected = Golden.Verify("resize-shrink", doc, GridDump.ToSvg(session.Back));
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();

        string second = GridDump.Escape(Encoding.UTF8.GetString(backend.Writes[^1]));
        int syncOn = second.IndexOf("\\e[?2026h", StringComparison.Ordinal);
        int ed2 = second.IndexOf("\\e[2J", StringComparison.Ordinal);
        await Assert.That(syncOn >= 0 && ed2 > syncOn).IsTrue(); // ED2 inside the wrapper, before content
    }

    [Test]
    public async Task SyncWrapper_OnVersusOff_Golden()
    {
        var bareBackend = new RecordingBackend();
        var syncBackend = new RecordingBackend();

        await PaintOneCellAsync(bareBackend, syncUpdates: false);
        await PaintOneCellAsync(syncBackend, syncUpdates: true);

        string doc = "# golden: sync-wrapper\n"
            + "## sync off\n" + GridDump.Frames(bareBackend)
            + "## sync on\n" + GridDump.Frames(syncBackend);
        string expected = Golden.Verify("sync-wrapper", doc);
        await Assert.That(doc).IsEqualTo(expected);
    }

    private static async Task PaintOneCellAsync(RecordingBackend backend, bool syncUpdates)
    {
        var writer = new AnsiWriter(backend, syncUpdates);
        var engine = new DiffEngine(6, 1);
        var back = new ScreenBuffer(6, 1);
        back.SetRune(1, 0, new Rune('X'), new CellStyle(PackedColor.Indexed(4)));
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
    }

    [Test]
    public async Task SgrMinimization_SecondFrameCarriesOnlyDelta()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(20, 2);
        var back = new ScreenBuffer(20, 2);

        back.SetText(0, 1, "ERR ", new CellStyle(PackedColor.Rgb(220, 60, 60)));
        back.SetText(4, 1, "WARN", new CellStyle(attrs: StyleAttr.Bold));
        back.SetText(9, 1, "ok", CellStyle.Plain);
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        long firstBytes = backend.TotalBytes;

        back.SetStyleAt(5, 1, new CellStyle(attrs: StyleAttr.Underline)); // bold→underline, one cell
        back.SetRune(9, 1, new Rune('!'), CellStyle.Plain);               // one glyph swap
        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();
        long secondBytes = backend.TotalBytes - firstBytes;

        string doc = GoldenDoc.Build("sgr-minimization", back, backend);
        string expected = Golden.Verify("sgr-minimization", doc, GridDump.ToSvg(back));
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(engine.FrontMatches(back)).IsTrue();

        string delta = Encoding.UTF8.GetString(backend.Writes[^1]);
        await Assert.That(delta.Contains('E')).IsFalse();         // ERR untouched
        await Assert.That(delta.Contains('W')).IsFalse();         // WARN untouched
        await Assert.That(delta.Contains('k')).IsFalse();         // "ok" untouched
        await Assert.That(secondBytes < firstBytes).IsTrue();     // delta smaller than the initial frame
    }
}
