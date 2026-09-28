using System.Text;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class TabsTests
{
    private static Tabs Sample() => new(["Chat", "Files", "Logs"]);

    private static KeyEvent Release(KeyCode key) =>
        new(key, default, KeyModifiers.None, KeyEventType.Release, false);

    [Test]
    public async Task Measure_Empty_IsOneRow()
    {
        var tabs = new Tabs();
        await Assert.That(tabs.Count).IsEqualTo(0);
        await Assert.That(tabs.Measure(40).MinLines).IsEqualTo(1);
        await Assert.That(tabs.Measure(40).IsExact).IsTrue();
        await Assert.That(tabs.CheapEstimate(40)).IsEqualTo(1);
    }

    [Test]
    public async Task Measure_NonEmpty_IsTwoRowsExact()
    {
        var tabs = Sample();
        await Assert.That(tabs.Measure(40).MinLines).IsEqualTo(2);
        await Assert.That(tabs.Measure(40).IsExact).IsTrue();
        await Assert.That(tabs.CheapEstimate(40)).IsEqualTo(2);
    }

    [Test]
    public async Task Titles_Sanitized_NeverEmpty()
    {
        var tabs = new Tabs(["  Chat  ", null!, "   ", "a\nb"]);
        await Assert.That(tabs.Count).IsEqualTo(4);
        await Assert.That(tabs.Titles[0]).IsEqualTo("Chat");
        await Assert.That(tabs.Titles[1]).IsEqualTo("?");
        await Assert.That(tabs.Titles[2]).IsEqualTo("?");
        await Assert.That(tabs.Titles[3]).IsEqualTo("a b");
        await Assert.That(tabs.SelectedTitle).IsEqualTo("Chat");
    }

    [Test]
    public async Task Select_Clamps_And_RaisesChangedOnce()
    {
        var tabs = Sample();
        int raised = 0;
        tabs.Changed += (_, _) => raised++;

        await Assert.That(tabs.Select(1)).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(1);
        await Assert.That(tabs.SelectedTitle).IsEqualTo("Files");

        await Assert.That(tabs.Select(1)).IsFalse();
        await Assert.That(tabs.Select(99)).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(2);
        await Assert.That(tabs.Select(-99)).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(0);
        await Assert.That(raised).IsEqualTo(3);
    }

    [Test]
    public async Task Select_Empty_ReturnsFalse()
    {
        var tabs = new Tabs();
        await Assert.That(tabs.Select(0)).IsFalse();
        await Assert.That(tabs.SelectedTitle).IsEqualTo("?");
    }

    [Test]
    public async Task Next_Prev_Wrap()
    {
        var tabs = Sample();
        await Assert.That(tabs.Prev()).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(2);
        await Assert.That(tabs.Next()).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(0);
    }

    [Test]
    public async Task HandleKey_Navigation_Consumed()
    {
        var tabs = Sample();
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(1);
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(0);
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.End))).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(2);
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.Home))).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(0);
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(1);
    }

    [Test]
    public async Task HandleKey_VimAndDigits_Jump()
    {
        var tabs = Sample();
        await Assert.That(tabs.HandleKey(KeyEvent.Char(new Rune('l')))).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(1);
        await Assert.That(tabs.HandleKey(KeyEvent.Char(new Rune('H')))).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(0);
        await Assert.That(tabs.HandleKey(KeyEvent.Char(new Rune('3')))).IsTrue();
        await Assert.That(tabs.Selected).IsEqualTo(2);
        await Assert.That(tabs.HandleKey(KeyEvent.Char(new Rune('9')))).IsFalse();
        await Assert.That(tabs.Selected).IsEqualTo(2);
    }

    [Test]
    public async Task HandleKey_NotConsumed()
    {
        var tabs = Sample();
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(tabs.HandleKey(KeyEvent.Char(new Rune(' ')))).IsFalse();
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.Up))).IsFalse();
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsFalse();
        await Assert.That(tabs.HandleKey(Release(KeyCode.Right))).IsFalse();
        await Assert.That(tabs.HandleKey(KeyEvent.Simple(KeyCode.Right, KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(tabs.HandleKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        await Assert.That(tabs.Selected).IsEqualTo(0);
    }

    [Test]
    public async Task Divider_Blank_FallsBackToDefault()
    {
        var tabs = Sample();
        tabs.Divider = "   ";
        await Assert.That(tabs.Divider).IsEqualTo("│");
        tabs.Divider = "|";
        await Assert.That(tabs.Divider).IsEqualTo("|");
    }

    [Test]
    public async Task RawText_BracketsSelected()
    {
        var tabs = Sample();
        _ = tabs.Select(1);
        await Assert.That(tabs.RawText()).IsEqualTo("Chat │ [Files] │ Logs");
        await Assert.That(new Tabs().RawText()).IsEqualTo("(empty)");
    }

    [Test]
    public async Task Paint_TitlesRow_ListsTitles()
    {
        var buffer = new ScreenBuffer(40, 2);
        var tabs = Sample();
        _ = tabs.Select(1);
        tabs.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 2), 0));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("Chat");
        await Assert.That(art).Contains("Files");
        await Assert.That(art).Contains("Logs");
        await Assert.That(art).Contains("─");
    }

    [Test]
    public async Task Paint_Underline_SitsUnderSelectedTitle()
    {
        var buffer = new ScreenBuffer(40, 2);
        var tabs = Sample();
        _ = tabs.Select(1);
        tabs.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 2), 0));

        // " Chat │ Files │ Logs": selected "Files" starts past " Chat │ " (8 cells).
        for (int x = 8; x < 13; x++)
        {
            await Assert.That((char)buffer.Get(x, 1).Rune).IsEqualTo('─');
        }

        await Assert.That((char)buffer.Get(1, 1).Rune).IsNotEqualTo('─');
    }

    [Test]
    public async Task Paint_Focus_BrightensUnderline()
    {
        var dim = new ScreenBuffer(40, 2);
        Sample().Paint(new BlockPaintContext(dim, new Rect(0, 0, 40, 2), 0));

        var focused = new ScreenBuffer(40, 2);
        var tabs = Sample();
        tabs.OnFocusChanged(true);
        tabs.Paint(new BlockPaintContext(focused, new Rect(0, 0, 40, 2), 0));

        // Compared against ChatPalette semantics, not raw palette indices.
        await Assert.That(dim.Get(1, 1).Style.Fg).IsEqualTo(ChatPalette.Dim.Fg);
        await Assert.That(focused.Get(1, 1).Style.Fg).IsEqualTo(ChatPalette.Accent);
    }

    [Test]
    public async Task Paint_Empty_ShowsPlaceholder()
    {
        var buffer = new ScreenBuffer(40, 2);
        new Tabs().Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 2), 0));
        await Assert.That(GridDump.Art(buffer)).Contains("(empty)");
    }

    [Test]
    public async Task Paint_Narrow_ScrollsToSelected()
    {
        var tabs = new Tabs(["Alpha", "Beta", "Gamma", "Delta"]);
        _ = tabs.Select(3);
        var buffer = new ScreenBuffer(12, 2);
        tabs.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 12, 2), 0));

        await Assert.That(tabs.FirstVisible).IsGreaterThan(0);
        await Assert.That(GridDump.Art(buffer)).Contains("Delta");
    }
}
