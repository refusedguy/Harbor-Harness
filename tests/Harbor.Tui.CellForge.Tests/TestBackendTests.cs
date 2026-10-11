// R2 steal (epic #1155): self-tests for the TestBackend harness — a C# port
// of the test module inside ratatui-core src/backend/test.rs (with_lines,
// assert_buffer_lines, scrollback, append_lines matrix). If the harness drifts
// from ratatui semantics, these fail first. The two *Rule* tests at the end
// are the executable form of the units-vs-Buffer rule (see TestBackend docs).

namespace Harbor.Tui.CellForge.Tests;

public class TestBackendHarnessTests
{
    [Test]
    public async Task WithLines_DerivesGeometryFromContent()
    {
        var backend = TestBackend.WithLines(["aaa", "bbbbb", "cc"]);
        await Assert.That(backend.Buffer.Cols).IsEqualTo(5);
        await Assert.That(backend.Buffer.Rows).IsEqualTo(3);
        await backend.AssertBufferLinesAsync(["aaa", "bbbbb", "cc"]);
    }

    [Test]
    public async Task WithLines_WideRune_WidthCountsTwoCells()
    {
        var backend = TestBackend.WithLines(["a中"]);
        await Assert.That(backend.Buffer.Cols).IsEqualTo(3);
        await backend.AssertBufferLinesAsync(["a中"]);
    }

    [Test]
    public async Task New_BlankBuffer_AssertsEmptyLines()
    {
        var backend = new TestBackend(10, 2);
        await backend.AssertBufferLinesAsync(["", ""]);
        await backend.AssertScrollbackEmptyAsync();
        await backend.AssertCursorPositionAsync(0, 0);
    }

    [Test]
    public async Task AssertBufferLines_ShortStringsNeedNoPadding()
    {
        // The idiom's selling point: expected lines are short strings, the
        // harness pads by rendering (ratatui with_lines semantics).
        var backend = new TestBackend(10, 2);
        backend.Buffer.SetText(0, 0, "a", CellStyle.Plain);
        await backend.AssertBufferLinesAsync(["a", ""]);
    }

    [Test]
    public async Task Resize_BlanksAndPads()
    {
        var backend = new TestBackend(10, 2);
        backend.Resize(5, 5);
        await backend.AssertBufferLinesAsync(["", "", "", "", ""]);
    }

    [Test]
    public async Task Clear_BlanksViewport_PreservesScrollback()
    {
        var backend = TestBackend.WithLines(["aaaaa", "bbbbb"]);
        backend.SetCursor(0, 1);
        backend.AppendLines(1);
        await backend.AssertScrollbackLinesAsync(["aaaaa"]);

        backend.Clear();
        await backend.AssertBufferLinesAsync(["", ""]);
        await backend.AssertScrollbackLinesAsync(["aaaaa"]);
    }

    [Test]
    public async Task SetCursor_ClampsToViewport()
    {
        var backend = new TestBackend(4, 2);
        backend.SetCursor(99, 99);
        await backend.AssertCursorPositionAsync(3, 1);
    }

    [Test]
    public async Task AppendLines_NotAtLastLine_MovesCursorKeepsBuffer()
    {
        var backend = TestBackend.WithLines(["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee"]);
        backend.SetCursor(0, 0);

        backend.AppendLines(1);
        await backend.AssertCursorPositionAsync(1, 1);
        backend.AppendLines(1);
        await backend.AssertCursorPositionAsync(2, 2);

        await backend.AssertBufferLinesAsync(["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee"]);
        await backend.AssertScrollbackEmptyAsync();
    }

    [Test]
    public async Task AppendLines_AtLastLine_ScrollsTopIntoScrollback()
    {
        var backend = TestBackend.WithLines(["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee"]);
        backend.SetCursor(0, 4);

        backend.AppendLines(1);

        await backend.AssertBufferLinesAsync(["bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee", ""]);
        await backend.AssertScrollbackLinesAsync(["aaaaaaaaaa"]);
        await backend.AssertCursorPositionAsync(1, 4);
    }

    [Test]
    public async Task AppendLines_MultiplePastLastLine()
    {
        var backend = TestBackend.WithLines(["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee"]);
        backend.SetCursor(0, 3);

        backend.AppendLines(3);
        await backend.AssertCursorPositionAsync(1, 4);

        await backend.AssertBufferLinesAsync(["cccccccccc", "dddddddddd", "eeeeeeeeee", "", ""]);
        await backend.AssertScrollbackLinesAsync(["aaaaaaaaaa", "bbbbbbbbbb"]);
    }

    [Test]
    public async Task AppendLines_MoreThanHeight_BlanksAllScrollsAll()
    {
        var backend = TestBackend.WithLines(["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee"]);
        backend.SetCursor(0, 4);

        backend.AppendLines(5);

        await backend.AssertBufferLinesAsync(["", "", "", "", ""]);
        await backend.AssertScrollbackLinesAsync(["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee"]);
    }

    [Test]
    public async Task AppendLines_PastHeight_PadsScrollbackWithBlanks()
    {
        var backend = TestBackend.WithLines(["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee"]);
        backend.SetCursor(0, 4);

        backend.AppendLines(8);

        await backend.AssertBufferLinesAsync(["", "", "", "", ""]);
        await backend.AssertScrollbackLinesAsync(
            ["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee", "", "", ""]);
    }

    [Test]
    public async Task ScrollbackWidth_SurvivesResize()
    {
        // Documented divergence from ratatui (which rewidths the scrollback
        // area): captured lines keep the width they were captured with.
        var backend = TestBackend.WithLines(["aaaaaaaaaa", "bbbbbbbbbb"]);
        backend.SetCursor(0, 1);
        backend.AppendLines(1);
        backend.Resize(4, 2);

        await backend.AssertBufferLinesAsync(["", ""]);
        await backend.AssertScrollbackLinesAsync(["aaaaaaaaaa"]);
    }

    [Test]
    public async Task AppendLines_Negative_Throws()
    {
        var backend = new TestBackend(4, 2);
        try
        {
            backend.AppendLines(-1);
            await Assert.That(true).IsFalse();
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }
}

/// <summary>
/// Executable form of the units-vs-Buffer rule: unit tests paint a raw
/// <see cref="ScreenBuffer"/> (no backend anywhere); integration tests seed
/// via <see cref="TestBackend.WithLines"/> and assert per line.
/// </summary>
public class TestBackendRuleTests
{
    // Stands in for a real widget Paint(ScreenBuffer, Rect, ...) method: the
    // rule is about what the paint target is, not about any specific widget.
    private static void PaintGreeting(ScreenBuffer buffer)
    {
        buffer.SetText(0, 0, "hi", CellStyle.Plain);
    }

    [Test]
    public async Task WidgetUnits_PaintDirectlyAgainstBuffer_NoBackend()
    {
        // UNIT side: raw buffer, GridDump.Art assert — TestBackend must not appear.
        var buffer = new ScreenBuffer(10, 2);
        PaintGreeting(buffer);

        string art = GridDump.Art(buffer);
        await Assert.That(art).IsEqualTo("hi        \n          \n");
    }

    [Test]
    public async Task WidgetIntegration_SeededBackend_PerLineAsserts()
    {
        // INTEGRATION side: seeded backend, per-line asserts, scrollback.
        var backend = TestBackend.WithLines(["hi", "tail"]);
        backend.SetCursor(0, 1);
        backend.AppendLines(1);

        await backend.AssertBufferLinesAsync(["tail", ""]);
        await backend.AssertScrollbackLinesAsync(["hi"]);
    }
}
