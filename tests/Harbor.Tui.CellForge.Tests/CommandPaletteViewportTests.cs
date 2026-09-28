using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [PRIM3c] palette scroll migration: the list scrolls through
/// <see cref="ScrollableViewport" /> and paints a <see cref="Scrollbar" />
/// overlay only while overflowing — fitting palettes stay byte-identical.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class CommandPaletteViewportTests
{
    private static CommandPaletteView OpenMany(int count)
    {
        var items = new CommandItem[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new CommandItem("cmd" + i, "Command " + i.ToString("00"));
        }

        var palette = new CommandPaletteView();
        palette.Show(items);
        return palette;
    }

    private static KeyEvent Key(KeyCode code) => KeyEvent.Simple(code, KeyModifiers.None);

    private static void Down(CommandPaletteView palette, int times)
    {
        for (int i = 0; i < times; i++)
        {
            _ = palette.HandleKey(Key(KeyCode.Down));
        }
    }

    [Test]
    public async Task Scroll_MoveDown_KeepsSelectedVisible_AndPaintsScrollbar()
    {
        var palette = OpenMany(20);
        var rect = new Rect(0, 0, 30, 8); // 4 list rows
        var buffer = new ScreenBuffer(30, 8);
        palette.Paint(buffer, rect);

        Down(palette, 10);
        palette.Paint(buffer, rect);
        string art = GridDump.Art(buffer);

        await Assert.That(palette.SelectedIndex).IsEqualTo(10);
        await Assert.That(art).Contains("Command 10");
        await Assert.That(art.Contains("█")).IsTrue(); // scrollbar thumb while overflowing
        await Assert.That(palette.Viewport.Offset == 7).IsTrue();
    }

    [Test]
    public async Task Scrollbar_Hidden_WhenContentFits()
    {
        var palette = OpenMany(3);
        var buffer = new ScreenBuffer(30, 8);
        palette.Paint(buffer, new Rect(0, 0, 30, 8));
        string art = GridDump.Art(buffer);

        await Assert.That(palette.Viewport.MaxOffset == 0).IsTrue();
        await Assert.That(art.Contains("█")).IsFalse();
    }

    [Test]
    public async Task Refilter_ResetsScrollToTop()
    {
        var palette = OpenMany(20);
        var rect = new Rect(0, 0, 30, 8);
        var buffer = new ScreenBuffer(30, 8);
        palette.Paint(buffer, rect);
        Down(palette, 12);
        palette.Paint(buffer, rect);
        await Assert.That(palette.Viewport.Offset > 0).IsTrue();

        _ = palette.HandleKey(KeyEvent.Char(new Rune('9')));
        palette.Paint(buffer, rect);

        await Assert.That(palette.SelectedIndex).IsEqualTo(0);
        await Assert.That(palette.Viewport.Offset == 0).IsTrue();
        await Assert.That(GridDump.Art(buffer)).Contains("Command 09");
    }

    [Test]
    public async Task PageDown_MovesByPage_AndKeepsSelectionVisible()
    {
        var palette = OpenMany(20);
        var rect = new Rect(0, 0, 30, 8); // 4 list rows
        var buffer = new ScreenBuffer(30, 8);
        palette.Paint(buffer, rect);

        _ = palette.HandleKey(Key(KeyCode.PageDown));
        palette.Paint(buffer, rect);

        await Assert.That(palette.SelectedIndex).IsEqualTo(5);
        await Assert.That(GridDump.Art(buffer)).Contains("Command 05");
    }
}
