using Harbor.E2E.Framework;

namespace Harbor.E2E.Framework.Tests;

/// <summary>
///     Unit tests for <c>AnsiTerminalBuffer</c> — the 2D terminal emulator the
///     E2E drivers read the screen out of.
/// </summary>
/// <remarks>
///     <para>
///         Every PTY scenario asserts on what this emulator shows, not on the
///         bytes the app wrote: <c>Harbor.Tui.CellForge.PtyTests</c> compares
///         the buffer's rows, and <c>TuiDriver</c> searches the same buffer for
///         the text a test is waiting for. A bug in the emulator therefore
///         shows up as a scenario that cannot find text the app really
///         printed, and that reads like a flake.
///     </para>
///     <para>
///         The cases below are the emulator's contract: where the cursor goes,
///         what overwrites what, what scrolls into the scrollback, and what
///         the HTML renderer sees.
///     </para>
/// </remarks>
public class AnsiTerminalBufferTests
{
    /// <summary>The grid's rows, trailing blanks trimmed, without the final empty line.</summary>
    private static string[] Rows(AnsiTerminalBuffer buffer)
    {
        string text = buffer.GetVisibleText().Replace("\r\n", "\n");
        string[] rows = text.Split('\n');
        return rows.Length > 0 && rows[^1].Length == 0 ? rows[..^1] : rows;
    }

    /// <summary>The cursor position, as "row,column".</summary>
    private static string Cursor(AnsiTerminalBuffer buffer) => $"{buffer.CursorRow},{buffer.CursorCol}";

    [Test]
    public async Task NewBuffer_HasTheGeometryItWasBuiltWith()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        await Assert.That(buffer.Width).IsEqualTo(10);
        await Assert.That(buffer.Height).IsEqualTo(3);
    }

    [Test]
    public async Task NewBuffer_LeavesTheCursorAtTheOrigin()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        await Assert.That(Cursor(buffer)).IsEqualTo("0,0");
    }

    [Test]
    public async Task Write_PlainText_LandsOnTheFirstRow()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("hello");
        await Assert.That(Rows(buffer)[0]).IsEqualTo("hello");
    }

    [Test]
    public async Task Write_PlainText_AdvancesTheCursor()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("hello");
        await Assert.That(Cursor(buffer)).IsEqualTo("0,5");
    }

    [Test]
    public async Task Write_CarriageReturnAndLineFeed_MovesToTheNextRow()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("a\r\nb");
        await Assert.That(string.Join("|", Rows(buffer))).IsEqualTo("a|b|");
    }

    [Test]
    public async Task Write_CursorPositionSequence_OverwritesTheCellItPointsAt()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("abc\u001b[1;1Hxy");
        await Assert.That(Rows(buffer)[0]).IsEqualTo("xyc");
    }

    [Test]
    public async Task Write_CursorMovementSequences_MoveTheCursorInsideTheGrid()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("\u001b[3;5H\u001b[1A\u001b[2C\u001b[1D\u001b[1B");
        await Assert.That(Cursor(buffer)).IsEqualTo("2,5");
    }

    [Test]
    public async Task Write_Backspace_MovesTheCursorBackOverTheLastCharacter()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("ab\bX");
        await Assert.That(Rows(buffer)[0]).IsEqualTo("aX");
    }

    [Test]
    public async Task Write_Tab_MovesTheCursorToTheNextTabStop()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("ab\tc");
        await Assert.That(Rows(buffer)[0]).IsEqualTo("ab      c");
    }

    [Test]
    public async Task Write_BeyondTheBottom_ScrollsTheTopRowIntoTheScrollback()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("one\ntwo\nthree\nfour");
        await Assert.That(string.Join("|", buffer.GetScrollbackLines())).IsEqualTo("one");
    }

    [Test]
    public async Task Write_BeyondTheBottom_KeepsTheNewRowsOnTheGrid()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("one\ntwo\nthree\nfour");
        await Assert.That(string.Join("|", Rows(buffer))).IsEqualTo("two|three|four");
    }

    [Test]
    public async Task Write_AlternateScreenSequence_ClearsTheGrid()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("abc\u001b[?1049h");
        await Assert.That(string.Join("|", Rows(buffer))).IsEqualTo("||");
    }

    [Test]
    public async Task Write_EraseInLine_ClearsTheRowFromTheCursor()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("abcdef\u001b[1;3H\u001b[K");
        await Assert.That(Rows(buffer)[0]).IsEqualTo("ab");
    }

    [Test]
    public async Task Write_SgrColour_ReachesTheHtmlRenderer()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("\u001b[1;31mX");
        await Assert.That(buffer.ToHtml()).Contains("color:#cd3131");
    }

    [Test]
    public async Task Write_SgrBold_ReachesTheHtmlRenderer()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("\u001b[1;31mX");
        await Assert.That(buffer.ToHtml()).Contains("font-weight:bold");
    }

    [Test]
    public async Task Write_TrueColorSequence_UsesTheRgbHex()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("\u001b[38;2;255;128;0mX");
        await Assert.That(buffer.ToHtml()).Contains("color:#FF8000");
    }

    [Test]
    public async Task Write_TwoHundredFiftySixColour_UsesTheCubeEntry()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("\u001b[38;5;196mX");
        await Assert.That(buffer.ToHtml()).Contains("color:#FF0000");
    }

    [Test]
    public async Task ContainsText_FindsWhatIsOnTheGrid()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("hello");
        await Assert.That(buffer.ContainsText("hello")).IsTrue();
    }

    [Test]
    public async Task ContainsText_MissesWhatIsNotOnTheGrid()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("hello");
        await Assert.That(buffer.ContainsText("goodbye")).IsFalse();
    }

    [Test]
    public async Task ContainsText_AnEmptyPatternMatchesNothing()
    {
        var buffer = new AnsiTerminalBuffer(10, 3);
        buffer.Write("hello");
        await Assert.That(buffer.ContainsText(string.Empty)).IsFalse();
    }
}
