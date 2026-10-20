using Harbor.TestKit;
using Harbor.Tui.CellForge;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// R6 frame-contract audit (issue #1161, ratatui draw order): autoresize runs
/// first in every frame, the root is always the full-screen FrameArea, the
/// cursor ships after the diff cells inside the same single backend write,
/// and every shipped frame reports a CompletedFrame hook.
/// </summary>
public class FrameContractTests
{
    // ScreenSession.BeginFrame arms the thread-static frame palette pin —
    // never hand a pinned render thread to the next test.
    [After(Test)]
    public void UnpinFramePalette() => ChatPalette.UnpinFrame();

    [Test]
    public async Task BeginFrame_AppliesSizeSource_First()
    {
        var session = MakeSession(20, 5, out _, () => (30, 10));
        session.BeginFrame();
        session.FlushFrame();

        await Assert.That(session.CurrentCols).IsEqualTo(30);
        await Assert.That(session.CurrentRows).IsEqualTo(10);
    }

    [Test]
    public async Task FrameArea_IsAlwaysFullScreenRoot()
    {
        var session = MakeSession(20, 5, out _);
        await Assert.That(session.FrameArea.X).IsEqualTo(0);
        await Assert.That(session.FrameArea.Y).IsEqualTo(0);
        await Assert.That(session.FrameArea.Width).IsEqualTo(20);
        await Assert.That(session.FrameArea.Height).IsEqualTo(5);

        session.Resize(30, 10);
        await Assert.That(session.FrameArea.Width).IsEqualTo(30);
        await Assert.That(session.FrameArea.Height).IsEqualTo(10);
    }

    [Test]
    public async Task SetCursor_ShipsAfterDiffCells_InSingleWrite()
    {
        var session = MakeSession(20, 5, out var backend);
        session.BeginFrame();
        session.PaintBuffer.SetText(0, 0, "hi", CellStyle.Plain);
        session.SetCursor(5, 2);
        session.FlushFrame();

        // MoveTo(5, 2) serializes as CSI 3;6H (1-based).
        await Assert.That(backend.Text.Contains("\x1B[3;6H")).IsTrue();
        int cellsIdx = backend.Text.IndexOf("hi", StringComparison.Ordinal);
        int cursorIdx = backend.Text.IndexOf("\x1B[3;6H", StringComparison.Ordinal);
        await Assert.That(cursorIdx).IsGreaterThan(cellsIdx);
        await Assert.That(backend.Writes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AbortFrame_DropsPendingCursor()
    {
        var session = MakeSession(20, 5, out var backend);
        session.BeginFrame();
        session.PaintBuffer.SetText(0, 0, "hi", CellStyle.Plain);
        session.SetCursor(5, 2);
        session.AbortFrame();

        session.BeginFrame();
        session.PaintBuffer.SetText(0, 0, "hi", CellStyle.Plain);
        session.FlushFrame();

        await Assert.That(backend.Text.Contains("\x1B[3;6H")).IsFalse();
    }

    [Test]
    public async Task FlushFrame_ReportsCompletedFrame()
    {
        var session = MakeSession(20, 5, out _);
        session.BeginFrame();
        session.SetCursor(5, 2);
        session.FlushFrame();

        await Assert.That(session.LastFrame.Shipped).IsTrue();
        await Assert.That(session.LastFrame.Area).IsEqualTo(session.FrameArea);
        var cursor = session.LastFrame.Cursor;
        await Assert.That(cursor.HasValue).IsTrue();
        await Assert.That(cursor!.Value.X).IsEqualTo(5);
        await Assert.That(cursor!.Value.Y).IsEqualTo(2);
    }

    private static ScreenSession MakeSession(
        int cols, int rows, out RecordingBackend backend, Func<(int Cols, int Rows)>? sizeSource = null)
    {
        backend = new RecordingBackend();
        return new ScreenSession(new AnsiWriter(backend), cols, rows, sizeSource);
    }
}
