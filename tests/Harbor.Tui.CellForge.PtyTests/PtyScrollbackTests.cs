// R2 steal (epic #1155): in-process proofs for the PTY-side idiom —
// AnsiTerminalBuffer scrollback capture plus PtyBufferAsserts per-line
// asserts. No PTY is spawned here (pure emulation, milliseconds); live
// scenarios keep using CellForgePtyScenarioBase, which now exposes the same
// idiom over the live grid (ScrollbackLines / AssertScreenLinesAsync).

using Harbor.E2E.Framework;

namespace Harbor.Tui.CellForge.PtyTests;

public class PtyScrollbackTests
{
    [Test]
    [Timeout(30_000)]
    public async Task ScrollUp_PushesTopLineIntoScrollback()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("aaa\nbbb\nccc\nddd\n");

        string[] visible = CellForgePtyScenarioBase.NormalizeLines(buffer.GetVisibleText());
        await PtyBufferAsserts.AssertBufferLinesAsync(visible, ["ccc", "ddd"]);
        await PtyBufferAsserts.AssertScrollbackLinesAsync(buffer.GetScrollbackLines(), ["aaa", "bbb"]);
    }

    [Test]
    [Timeout(30_000)]
    public async Task ClearScreen_PreservesScrollback()
    {
        // ratatui clear() parity: ED2 blanks the viewport, scrollback survives.
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("aaa\nbbb\nccc\nddd\n");
        buffer.Write("\u001b[2J");

        string[] visible = CellForgePtyScenarioBase.NormalizeLines(buffer.GetVisibleText());
        await PtyBufferAsserts.AssertBufferLinesAsync(visible, []);
        await PtyBufferAsserts.AssertScrollbackLinesAsync(buffer.GetScrollbackLines(), ["aaa", "bbb"]);
    }

    [Test]
    [Timeout(30_000)]
    public async Task NoScroll_ScrollbackStaysEmpty()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("aaa\nbbb\n");

        string[] visible = CellForgePtyScenarioBase.NormalizeLines(buffer.GetVisibleText());
        await PtyBufferAsserts.AssertBufferLinesAsync(visible, ["aaa", "bbb"]);
        await PtyBufferAsserts.AssertScrollbackLinesAsync(buffer.GetScrollbackLines(), []);
    }
}
