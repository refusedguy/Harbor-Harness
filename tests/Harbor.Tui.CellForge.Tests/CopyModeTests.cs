using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Generic.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Primitive-level coverage for issue #2322: keyboard navigation, visible
/// selection and the OSC 52 clipboard sequence. The mouse path reuses the
/// SelectionEngine contract, so its tests here check only the OSC wrapping.
/// </summary>
public class CopyModeTests
{
    private static ScreenBuffer Buffer(string[] lines) => ScreenFixtures.BufferFromLines(lines);

    private static KeyEvent Char(char c, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Char(new Rune(c), mods);

    private static string ClipboardText(string sequence)
    {
        int payloadStart = sequence.LastIndexOf(';') + 1;
        string payload = sequence[payloadStart..^1];
        return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
    }

    private static Func<int, int, Cell> BlankCell => (_, _) => Cell.Blank;

    [Test]
    public async Task Inactive_LowerV_EntersCharacterSelection()
    {
        var mode = new CopyMode();
        var result = mode.OnKey(Char('v'), 80, 24, BlankCell);

        await Assert.That(result.Claimed).IsTrue();
        await Assert.That(mode.IsActive).IsTrue();
        await Assert.That(mode.Kind).IsEqualTo(CopySelectionKind.Character);
        await Assert.That(mode.HasSelection).IsTrue();
        await Assert.That(mode.Cursor).IsEqualTo((0, 0));
    }

    [Test]
    public async Task Inactive_UpperV_EntersLineSelection()
    {
        var mode = new CopyMode();
        var result = mode.OnKey(Char('V'), 80, 24, BlankCell);

        await Assert.That(result.Claimed).IsTrue();
        await Assert.That(mode.Kind).IsEqualTo(CopySelectionKind.Line);
    }

    [Test]
    public async Task Inactive_OtherKey_IsIgnored()
    {
        var mode = new CopyMode();
        await Assert.That(mode.OnKey(Char('x'), 80, 24, BlankCell).Claimed).IsFalse();
        await Assert.That(mode.IsActive).IsFalse();
    }

    [Test]
    public async Task Inactive_CtrlV_IsIgnored()
    {
        var mode = new CopyMode();
        var result = mode.OnKey(Char('v', KeyModifiers.Ctrl), 80, 24, BlankCell);

        await Assert.That(result.Claimed).IsFalse();
        await Assert.That(mode.IsActive).IsFalse();
    }

    [Test]
    public async Task Navigation_HjklAndArrows_MoveCursorAndClamp()
    {
        var mode = new CopyMode();
        mode.Enter(2, 2);
        mode.OnKey(Char('v'), 80, 24, BlankCell);

        mode.OnKey(Char('l'), 80, 24, BlankCell);
        mode.OnKey(KeyEvent.Simple(KeyCode.Down), 80, 24, BlankCell);
        await Assert.That(mode.Cursor).IsEqualTo((3, 3));

        mode.OnKey(KeyEvent.Simple(KeyCode.Left), 80, 24, BlankCell);
        mode.OnKey(KeyEvent.Simple(KeyCode.Up), 80, 24, BlankCell);
        await Assert.That(mode.Cursor).IsEqualTo((2, 2));
    }

    [Test]
    public async Task CharacterSelection_Copy_ProducesOsc52Sequence()
    {
        var buffer = Buffer(["hello world"]);
        var mode = new CopyMode();
        mode.Enter(0, 0);
        mode.OnKey(Char('v'), buffer.Cols, buffer.Rows, buffer.Get);
        for (int i = 0; i < 4; i++)
        {
            mode.OnKey(Char('l'), buffer.Cols, buffer.Rows, buffer.Get);
        }

        var result = mode.OnKey(KeyEvent.Simple(KeyCode.Enter), buffer.Cols, buffer.Rows, buffer.Get);

        await Assert.That(result.Claimed).IsTrue();
        await Assert.That(result.ClipboardSequence).IsEqualTo("\u001B]52;c;aGVsbG8=\u0007");
        await Assert.That(mode.IsActive).IsFalse();
    }

    [Test]
    public async Task LineSelection_Copy_IncludesWholeRows()
    {
        var buffer = Buffer(["alpha", "beta"]);
        var mode = new CopyMode();
        mode.Enter(0, 0);
        mode.OnKey(Char('V'), buffer.Cols, buffer.Rows, buffer.Get);
        mode.OnKey(Char('j'), buffer.Cols, buffer.Rows, buffer.Get);

        var result = mode.OnKey(Char('y'), buffer.Cols, buffer.Rows, buffer.Get);

        await Assert.That(result.ClipboardSequence).IsNotNull();
        string sequence = result.ClipboardSequence!;
        await Assert.That(sequence.StartsWith("\u001B]52;c;")).IsTrue();
        await Assert.That(sequence.EndsWith("\u0007")).IsTrue();
        await Assert.That(ClipboardText(sequence)).IsEqualTo("alpha\nbeta");
    }

    [Test]
    public async Task Copy_WithEmptySelection_ReturnsNull()
    {
        var mode = new CopyMode();
        mode.Enter(0, 0);
        var result = mode.OnKey(Char('y'), 80, 24, BlankCell);

        await Assert.That(result.Claimed).IsTrue();
        await Assert.That(result.ClipboardSequence).IsNull();
        await Assert.That(mode.IsActive).IsFalse();
    }

    [Test]
    public async Task Escape_AndQ_CancelWithoutCopying()
    {
        var escapeMode = new CopyMode();
        escapeMode.Enter(0, 0);
        escapeMode.OnKey(Char('v'), 80, 24, BlankCell);
        var escaped = escapeMode.OnKey(KeyEvent.Simple(KeyCode.Escape), 80, 24, BlankCell);
        await Assert.That(escaped.ClipboardSequence).IsNull();
        await Assert.That(escapeMode.IsActive).IsFalse();

        var quitMode = new CopyMode();
        quitMode.Enter(0, 0);
        quitMode.OnKey(Char('v'), 80, 24, BlankCell);
        var quit = quitMode.OnKey(Char('q'), 80, 24, BlankCell);
        await Assert.That(quit.ClipboardSequence).IsNull();
        await Assert.That(quitMode.IsActive).IsFalse();
    }

    [Test]
    public async Task HomeAndEnd_JumpWithinRow()
    {
        var mode = new CopyMode();
        mode.Enter(0, 0);
        mode.OnKey(Char('v'), 80, 24, BlankCell);
        mode.OnKey(Char('$'), 80, 24, BlankCell);
        await Assert.That(mode.Cursor).IsEqualTo((79, 0));
        mode.OnKey(Char('0'), 80, 24, BlankCell);
        await Assert.That(mode.Cursor).IsEqualTo((0, 0));
    }

    [Test]
    public async Task MousePath_WrapsExtractedTextInOsc52()
    {
        var buffer = Buffer(["hello world"]);
        var mode = new CopyMode();
        await Assert.That(mode.OnPress(0, 0, MouseButton.Left)).IsTrue();
        await Assert.That(mode.OnPress(0, 0, MouseButton.Right)).IsFalse();
        mode.OnDrag(4, 0);

        string? sequence = mode.OnRelease(4, 0, buffer.Cols, buffer.Rows, buffer.Get);

        await Assert.That(sequence).IsEqualTo("\u001B]52;c;aGVsbG8=\u0007");
    }

    [Test]
    public async Task SelectionRect_TracksKeyboardExtent()
    {
        var mode = new CopyMode();
        mode.Enter(0, 0);
        mode.OnKey(Char('v'), 80, 24, BlankCell);
        mode.OnKey(Char('l'), 80, 24, BlankCell);
        mode.OnKey(Char('l'), 80, 24, BlankCell);

        await Assert.That(mode.SelectionRect(80, 24)).IsEqualTo(new Rect(0, 0, 3, 1));
    }

    [Test]
    public async Task Paint_MarksSelectionAndBareCursorReverse()
    {
        var buffer = Buffer(["hello world"]);
        var mode = new CopyMode();
        mode.Enter(0, 0);
        mode.OnKey(Char('v'), buffer.Cols, buffer.Rows, buffer.Get);
        mode.OnKey(Char('l'), buffer.Cols, buffer.Rows, buffer.Get);
        mode.Paint(buffer);

        await Assert.That(buffer.Get(0, 0).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsTrue();
        await Assert.That(buffer.Get(1, 0).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsTrue();
        await Assert.That(buffer.Get(2, 0).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();

        var fresh = Buffer(["hello world"]);
        var cursorOnly = new CopyMode();
        cursorOnly.Enter(3, 0);
        cursorOnly.Paint(fresh);
        await Assert.That(fresh.Get(3, 0).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsTrue();
        await Assert.That(fresh.Get(0, 0).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();
    }

    [Test]
    public async Task WideRune_Copy_SkipsTailCells()
    {
        var buffer = TestRender.Buffer(4, 1);
        buffer.SetText(0, 0, "あb", CellStyle.Plain);
        var mode = new CopyMode();
        mode.Enter(0, 0);
        mode.OnKey(Char('v'), buffer.Cols, buffer.Rows, buffer.Get);
        mode.OnKey(KeyEvent.Simple(KeyCode.End), buffer.Cols, buffer.Rows, buffer.Get);

        var result = mode.OnKey(KeyEvent.Simple(KeyCode.Enter), buffer.Cols, buffer.Rows, buffer.Get);

        await Assert.That(result.ClipboardSequence).IsNotNull();
        await Assert.That(ClipboardText(result.ClipboardSequence!)).IsEqualTo("あb");
    }
}
