using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Command palette (ctrl+p pattern): open/close, fuzzy filtering, keyboard
/// navigation, commit contract, and painted content. Deterministic — pure
/// state transitions plus one-off buffer paints.
/// </summary>
public class CommandPaletteViewTests
{
    private static readonly CommandItem[] Commands =
    [
        new("clear", "Clear session", "wipe the visible transcript"),
        new("fork", "Fork session", "branch from a message"),
        new("model", "Switch model", "pick another provider model", "ctrl+m"),
        new("quit", "Quit", "leave harbor"),
        new("help", "Help", "list commands"),
    ];

    private static CommandPaletteView Open() 
    {
        var palette = new CommandPaletteView();
        palette.Show(Commands);
        return palette;
    }

    private static KeyEvent Key(KeyCode code, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Simple(code, mods);

    [Test]
    public async Task Show_EmptyQuery_ListsAllSuggestions()
    {
        var palette = Open();

        await Assert.That(palette.Visible).IsTrue();
        await Assert.That(palette.Results).Count().IsEqualTo(Commands.Length);
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task HandleKey_QueryFilters_AndResetsSelection()
    {
        var palette = Open();
        _ = palette.HandleKey(KeyEvent.Char(new Rune('f')));
        _ = palette.HandleKey(KeyEvent.Char(new Rune('o')));
        _ = palette.HandleKey(KeyEvent.Char(new Rune('r')));

        await Assert.That(palette.Query).IsEqualTo("for");
        await Assert.That(palette.Results).Count().IsEqualTo(1);
        await Assert.That(palette.Results[0].Id).IsEqualTo("fork");
    }

    [Test]
    public async Task HandleKey_Escape_Hides()
    {
        var palette = Open();
        _ = palette.HandleKey(Key(KeyCode.Escape));

        await Assert.That(palette.Visible).IsFalse();
        await Assert.That(palette.Results).Count().IsEqualTo(0);
    }

    [Test]
    public async Task HandleKey_Arrows_MoveSelection_WithClamp()
    {
        var palette = Open();
        _ = palette.HandleKey(Key(KeyCode.Up)); // clamped at 0
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);

        _ = palette.HandleKey(Key(KeyCode.Down));
        _ = palette.HandleKey(Key(KeyCode.Down));
        await Assert.That(palette.SelectedIndex).IsEqualTo(2);

        _ = palette.HandleKey(Key(KeyCode.PageUp));
        await Assert.That(palette.SelectedIndex).IsEqualTo(0); // clamped again
    }

    [Test]
    public async Task HandleKey_Enter_CommitsSelected_AndHides()
    {
        var palette = Open();
        CommandItem? committed = null;
        palette.OnCommit = item => committed = item;

        _ = palette.HandleKey(Key(KeyCode.Down));
        _ = palette.HandleKey(Key(KeyCode.Enter));

        await Assert.That(committed).IsNotNull();
        await Assert.That(committed!.Id).IsEqualTo("fork");
        await Assert.That(palette.Visible).IsFalse();
    }

    [Test]
    public async Task HandleKey_Enter_OnEmptyResults_StaysOpenWithoutCommit()
    {
        var palette = Open();
        bool committed = false;
        palette.OnCommit = _ => committed = true;
        _ = palette.HandleKey(KeyEvent.Char(new Rune('ж'))); // no match
        _ = palette.HandleKey(Key(KeyCode.Enter));

        await Assert.That(committed).IsFalse();
        await Assert.That(palette.Visible).IsTrue(); // nothing to run — stay open until Esc
    }

    [Test]
    public async Task HandleKey_Invisible_IsNotConsumed()
    {
        var palette = new CommandPaletteView();

        await Assert.That(palette.HandleKey(Key(KeyCode.Escape))).IsFalse();
        await Assert.That(palette.HandleKey(Key(KeyCode.Enter))).IsFalse();
    }

    [Test]
    public async Task HandleKey_Backspace_TrimsQuery()
    {
        var palette = Open();
        _ = palette.HandleKey(KeyEvent.Char(new Rune('m')));
        _ = palette.HandleKey(KeyEvent.Char(new Rune('o')));
        _ = palette.HandleKey(Key(KeyCode.Backspace));

        await Assert.That(palette.Query).IsEqualTo("m");
    }

    [Test]
    public async Task Paint_DrawsQueryListAndHints()
    {
        var palette = Open();
        _ = palette.HandleKey(KeyEvent.Char(new Rune('m')));

        var buffer = new ScreenBuffer(50, 10);
        palette.Paint(buffer, new Rect(2, 1, 46, 8));
        string art = GridDump.Art(buffer);

        await Assert.That(art).Contains("m");
        await Assert.That(art).Contains("Switch model");
        await Assert.That(art).Contains("esc close");
    }

    [Test]
    public async Task Paint_SelectedRow_IsHighlighted()
    {
        var palette = Open();
        var buffer = new ScreenBuffer(50, 10);
        palette.Paint(buffer, new Rect(2, 1, 46, 8));

        // Row 0 selected → accent bold title style.
        var cell = buffer.Get(3, 3); // first list row, first text column
        await Assert.That(cell.Style.Fg).IsEqualTo(ChatPalette.Accent);
    }

    [Test]
    public async Task Paint_Invisible_NothingDrawn()
    {
        var palette = new CommandPaletteView();
        var buffer = new ScreenBuffer(20, 6);
        palette.Paint(buffer, new Rect(0, 0, 20, 6));
        string art = GridDump.Art(buffer);

        await Assert.That(art.Trim()).IsEqualTo(string.Empty);
    }
}
