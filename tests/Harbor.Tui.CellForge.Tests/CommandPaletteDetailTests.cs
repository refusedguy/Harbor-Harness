using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Palette second plan: item <see cref="CommandItem.Detail" /> paints
///     dimmed after the title (short ids, auth states), so title-first rows
///     keep their metadata visible. Rows without detail paint exactly as
///     before.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class CommandPaletteDetailTests
{
    private static CommandPaletteView Open() =>
        OpenWith(
        [
            // No groups here: headers would shift row coordinates (covered
            // by the view's own group tests); this class pins the detail.
            new("s1", "Fix login bug", "abc12345 · code/kilo-auto · proj"),
            new("s2", "Write docs", string.Empty),
        ]);

    private static CommandPaletteView OpenWith(IReadOnlyList<CommandItem> items)
    {
        var palette = new CommandPaletteView();
        palette.Show(items);
        return palette;
    }

    [Test]
    public async Task Paint_RendersDetail_DimmedAfterTitle()
    {
        var palette = Open();
        var buffer = new ScreenBuffer(60, 10);
        palette.Paint(buffer, new Rect(2, 1, 56, 8));
        string art = GridDump.Art(buffer);

        await Assert.That(art).Contains("Fix login bug");
        await Assert.That(art).Contains("abc12345");

        // Detail starts after "  " past the title: dim style, not accent.
        int titleStartX = 3;
        int detailX = titleStartX + "Fix login bug".Length + 2;
        var detailCell = buffer.Get(detailX, 3);
        await Assert.That(detailCell.Style.Fg).IsEqualTo(ChatPalette.Dim.Fg);
    }

    [Test]
    public async Task Paint_EmptyDetail_UnchangedRow()
    {
        var palette = Open();
        var buffer = new ScreenBuffer(60, 10);
        palette.Paint(buffer, new Rect(2, 1, 56, 8));
        string art = GridDump.Art(buffer);

        await Assert.That(art).Contains("Write docs");

        // No trailing filler past the bare title.
        int titleStartX = 3;
        int pastTitleX = titleStartX + "Write docs".Length;
        var cell = buffer.Get(pastTitleX, 4);
        await Assert.That(cell.Rune).IsEqualTo((int)' ');
    }
}
