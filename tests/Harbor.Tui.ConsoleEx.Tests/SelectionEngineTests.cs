using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class SelectionEngineTests
{
    private static ScreenBuffer Buffer(string[] lines)
    {
        int cols = lines.Max(l => l.Length);
        var buffer = new ScreenBuffer(cols, lines.Length);
        for (int y = 0; y < lines.Length; y++)
        {
            buffer.SetText(0, y, lines[y], CellStyle.Plain);
        }

        return buffer;
    }

    [Test]
    public async Task Press_RightButton_IsNotClaimed()
    {
        var engine = new SelectionEngine();
        await Assert.That(engine.OnPress(1, 1, MouseButton.Right)).IsFalse();
        await Assert.That(engine.IsActive).IsFalse();
    }

    [Test]
    public async Task Press_LeftButton_ClaimsAndActivates()
    {
        var engine = new SelectionEngine();
        await Assert.That(engine.OnPress(2, 3, MouseButton.Left)).IsTrue();
        await Assert.That(engine.IsActive).IsTrue();
    }

    [Test]
    public async Task PlainClick_Release_ReturnsNull()
    {
        var engine = new SelectionEngine();
        engine.OnPress(1, 1, MouseButton.Left);
        var text = engine.OnRelease(1, 1, 40, 10, (x, y) => Cell.Blank);
        await Assert.That(text).IsNull();
        await Assert.That(engine.IsActive).IsFalse();
    }

    [Test]
    public async Task SingleRow_Drag_ExtractsRangeWithTrailingTrim()
    {
        var buffer = Buffer(["hello world"]);
        var engine = new SelectionEngine();
        engine.OnPress(0, 0, MouseButton.Left);
        engine.OnDrag(5, 0);
        var text = engine.OnRelease(5, 0, buffer.Cols, buffer.Rows, buffer.Get);
        await Assert.That(text).IsEqualTo("hello");
    }

    [Test]
    public async Task MultiRow_Drag_JoinsRowsAndTrimsEach()
    {
        var buffer = Buffer(["alpha beta", "gamma   "]);
        var engine = new SelectionEngine();
        engine.OnPress(0, 0, MouseButton.Left);
        engine.OnDrag(9, 1);
        var text = engine.OnRelease(9, 1, buffer.Cols, buffer.Rows, buffer.Get);
        await Assert.That(text).IsEqualTo("alpha beta\ngamma");
    }

    [Test]
    public async Task Drag_Backwards_NormalizesRectangle()
    {
        var buffer = Buffer(["abcdef", "ghijkl"]);
        var engine = new SelectionEngine();
        engine.OnPress(5, 1, MouseButton.Left); // release end
        engine.OnDrag(3, 1);
        var text = engine.OnRelease(3, 1, buffer.Cols, buffer.Rows, buffer.Get);
        await Assert.That(text).IsEqualTo("jkl"); // cols 3..5 of row 1, reversed drag
    }

    [Test]
    public async Task Release_OutsideBuffer_ClampsToEdges()
    {
        var buffer = Buffer(["abcdef"]);
        var engine = new SelectionEngine();
        engine.OnPress(3, 0, MouseButton.Left);
        var text = engine.OnRelease(999, 999, buffer.Cols, buffer.Rows, buffer.Get);
        await Assert.That(text).IsEqualTo("def");
    }

    [Test]
    public async Task WideRune_Selection_SkipsTailCells()
    {
        // "あ" is wide: lead + WSkip tail. Build via SetText like the renderer does.
        var buffer = new ScreenBuffer(4, 1);
        buffer.SetText(0, 0, "あb", CellStyle.Plain);
        var engine = new SelectionEngine();
        engine.OnPress(0, 0, MouseButton.Left);
        var text = engine.OnRelease(3, 0, buffer.Cols, buffer.Rows, buffer.Get);
        await Assert.That(text).IsEqualTo("あb");
    }

    [Test]
    public async Task WhitespaceOnly_Region_ReturnsNull()
    {
        var buffer = Buffer(["     "]);
        var engine = new SelectionEngine();
        engine.OnPress(0, 0, MouseButton.Left);
        var text = engine.OnRelease(4, 0, buffer.Cols, buffer.Rows, buffer.Get);
        await Assert.That(text).IsNull();
    }

    [Test]
    public async Task NormalizedRect_ClampsAndSorts()
    {
        var engine = new SelectionEngine();
        await Assert.That(engine.NormalizedRect(80, 24)).IsNull();
        engine.OnPress(70, 20, MouseButton.Left);
        engine.OnDrag(2, 4);
        var rect = engine.NormalizedRect(80, 24);
        await Assert.That(rect).IsEqualTo(new Rect(2, 4, 69, 17));
    }

    [Test]
    public async Task Paint_MarksRegionReverse_NextBufferUntouched()
    {
        var buffer = Buffer(["abcd"]);
        var engine = new SelectionEngine();
        engine.OnPress(0, 0, MouseButton.Left);
        engine.OnDrag(2, 0);
        engine.Paint(buffer);
        await Assert.That(buffer.Get(0, 0).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsTrue();
        await Assert.That(buffer.Get(3, 0).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();

        var fresh = Buffer(["abcd"]);
        await Assert.That(fresh.Get(0, 0).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();
    }

    [Test]
    public async Task SecondPress_RestartsSelection()
    {
        var engine = new SelectionEngine();
        engine.OnPress(1, 1, MouseButton.Left);
        engine.OnPress(5, 5, MouseButton.Left);
        var text = engine.OnRelease(5, 5, 40, 10, (x, y) => Cell.Blank);
        await Assert.That(text).IsNull(); // second press reset the anchor
    }
}
