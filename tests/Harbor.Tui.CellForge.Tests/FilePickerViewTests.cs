using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// File picker (bubbles filepicker pattern): open/close, fuzzy filtering,
/// keyboard navigation, commit contract, preview pane, and painted content.
/// Deterministic — pure state transitions plus one-off buffer paints.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class FilePickerViewTests
{
    private static readonly FilePickerItem[] Files =
    [
        new("src/Harbor.Core/AgentLoop.cs", "agent loop", PreviewLines: ["public sealed class AgentLoop", "{", "}"]),
        new("src/Harbor.Core/Config.cs", "config", PreviewLines: ["public sealed record Config(", "    string Model);"]),
        new("README.md", "docs", PreviewLines: ["# Harbor", "", "> Modular harness"]),
        new("docs/ROADMAP.md", "docs"),
        new("src/tools", "folder", IsDirectory: true),
    ];

    private static FilePickerView Open()
    {
        var picker = new FilePickerView();
        picker.Show(Files);
        return picker;
    }

    private static KeyEvent Key(KeyCode code, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Simple(code, mods);

    [Test]
    public async Task Show_EmptyQuery_ListsAllFiles()
    {
        var picker = Open();

        await Assert.That(picker.Visible).IsTrue();
        await Assert.That(picker.Results).Count().IsEqualTo(Files.Length);
        await Assert.That(picker.SelectedIndex).IsEqualTo(0);
        await Assert.That(picker.SelectedItem.HasValue).IsTrue();
    }

    [Test]
    public async Task HandleKey_QueryFilters_AndResetsSelection()
    {
        var picker = Open();
        _ = picker.HandleKey(KeyEvent.Char(new Rune('r')));
        _ = picker.HandleKey(KeyEvent.Char(new Rune('o')));
        _ = picker.HandleKey(KeyEvent.Char(new Rune('a')));
        _ = picker.HandleKey(KeyEvent.Char(new Rune('d')));

        await Assert.That(picker.Query).IsEqualTo("road");
        await Assert.That(picker.Results).Count().IsEqualTo(1);
        await Assert.That(picker.Results[0].Path).IsEqualTo("docs/ROADMAP.md");
        await Assert.That(picker.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task HandleKey_Escape_Hides()
    {
        var picker = Open();
        _ = picker.HandleKey(Key(KeyCode.Escape));

        await Assert.That(picker.Visible).IsFalse();
        await Assert.That(picker.Results).Count().IsEqualTo(0);
    }

    [Test]
    public async Task HandleKey_Arrows_MoveSelection_WithClamp()
    {
        var picker = Open();
        _ = picker.HandleKey(Key(KeyCode.Up)); // clamped at 0
        await Assert.That(picker.SelectedIndex).IsEqualTo(0);

        _ = picker.HandleKey(Key(KeyCode.Down));
        _ = picker.HandleKey(Key(KeyCode.Down));
        await Assert.That(picker.SelectedIndex).IsEqualTo(2);

        _ = picker.HandleKey(Key(KeyCode.Home));
        await Assert.That(picker.SelectedIndex).IsEqualTo(0);

        _ = picker.HandleKey(Key(KeyCode.End));
        await Assert.That(picker.SelectedIndex).IsEqualTo(picker.Results.Count - 1);
    }

    [Test]
    public async Task HandleKey_Enter_CommitsSelected_AndHides()
    {
        var picker = Open();
        FilePickerItem? committed = null;
        picker.OnCommit = item => committed = item;

        _ = picker.HandleKey(Key(KeyCode.Down));
        _ = picker.HandleKey(Key(KeyCode.Enter));

        await Assert.That(committed).IsNotNull();
        await Assert.That(committed!.Path).IsEqualTo(Files[1].Path);
        await Assert.That(picker.Visible).IsFalse();
    }

    [Test]
    public async Task HandleKey_Enter_OnEmptyResults_StaysOpenWithoutCommit()
    {
        var picker = Open();
        bool committed = false;
        picker.OnCommit = _ => committed = true;
        _ = picker.HandleKey(KeyEvent.Char(new Rune('ж')));
        _ = picker.HandleKey(KeyEvent.Char(new Rune('щ')));
        _ = picker.HandleKey(Key(KeyCode.Enter));

        await Assert.That(committed).IsFalse();
        await Assert.That(picker.Visible).IsTrue(); // nothing to pick — stay open until Esc
    }

    [Test]
    public async Task HandleKey_Invisible_IsNotConsumed()
    {
        var picker = new FilePickerView();

        await Assert.That(picker.HandleKey(Key(KeyCode.Escape))).IsFalse();
        await Assert.That(picker.HandleKey(Key(KeyCode.Enter))).IsFalse();
    }

    [Test]
    public async Task HandleKey_Backspace_TrimsQuery()
    {
        var picker = Open();
        _ = picker.HandleKey(KeyEvent.Char(new Rune('r')));
        _ = picker.HandleKey(KeyEvent.Char(new Rune('m')));
        _ = picker.HandleKey(Key(KeyCode.Backspace));

        await Assert.That(picker.Query).IsEqualTo("r");
    }

    [Test]
    public async Task SelectedPreview_FollowsSelection_AndProviderFallback()
    {
        var picker = Open();
        await Assert.That(picker.SelectedPreview.Count).IsGreaterThan(0);
        await Assert.That(picker.SelectedPreview[0]).Contains("AgentLoop");

        _ = picker.HandleKey(Key(KeyCode.Down));
        _ = picker.HandleKey(Key(KeyCode.Down));
        await Assert.That(picker.SelectedItem.Value.Path).IsEqualTo("README.md");
        await Assert.That(picker.SelectedPreview[0]).IsEqualTo("# Harbor");

        // docs/ROADMAP.md has no inline preview → provider fallback.
        picker.PreviewProvider = static item => item.Path.EndsWith("ROADMAP.md") ? ["# Roadmap"] : null;
        _ = picker.HandleKey(Key(KeyCode.Down));
        await Assert.That(picker.SelectedItem.Value.Path).IsEqualTo("docs/ROADMAP.md");
        await Assert.That(picker.SelectedPreview[0]).IsEqualTo("# Roadmap");

        // Directory without preview lines reports itself.
        _ = picker.HandleKey(Key(KeyCode.Down));
        await Assert.That(picker.SelectedItem.Value.IsDirectory).IsTrue();
        await Assert.That(picker.SelectedPreview[0]).IsEqualTo("(directory)");
    }

    [Test]
    public async Task Paint_DrawsQueryListPreviewAndHints()
    {
        var picker = Open();

        var buffer = new ScreenBuffer(70, 10);
        picker.Paint(buffer, new Rect(2, 1, 66, 8));
        string art = GridDump.Art(buffer);

        await Assert.That(art).Contains("AgentLoop");
        await Assert.That(art).Contains("README.md");
        await Assert.That(art).Contains("esc close");
        await Assert.That(art).Contains("public sealed class"); // preview pane (width-clipped)
    }

    [Test]
    public async Task Paint_SelectedRow_IsHighlighted()
    {
        var picker = Open();
        var buffer = new ScreenBuffer(70, 10);
        picker.Paint(buffer, new Rect(2, 1, 66, 8));

        // Row 0 selected → accent bold title style.
        var cell = buffer.Get(3, 3); // first list row, first text column
        await Assert.That(cell.Style.Fg).IsEqualTo(ChatPalette.Accent);
    }

    [Test]
    public async Task Paint_Invisible_NothingDrawn()
    {
        var picker = new FilePickerView();
        var buffer = new ScreenBuffer(20, 6);
        picker.Paint(buffer, new Rect(0, 0, 20, 6));
        string art = GridDump.Art(buffer);

        await Assert.That(art.Trim()).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// #482: the preview header shortened the path with a from-end slice length of
    /// <c>width - file.Length - 4</c>, which reaches <c>-1</c> once a long file name
    /// fills the pane — <see cref="Index.FromEnd" /> rejects negative values with
    /// <see cref="ArgumentOutOfRangeException" />. A 42-wide rect yields a 17-column
    /// preview pane, the narrowest that still splits into list | preview, so
    /// "srcd/abcdefghijklm" (file part 14) is the exact off-by-one input.
    /// </summary>
    [Test]
    public async Task Paint_PreviewHeader_ZeroDirectoryBudget_DoesNotThrow()
    {
        var picker = new FilePickerView();
        picker.Show([new FilePickerItem("srcd/abcdefghijklm", "deep", PreviewLines: ["class AgentLoop"])]);

        var buffer = new ScreenBuffer(50, 10);
        picker.Paint(buffer, new Rect(0, 0, 42, 8));
        string art = GridDump.Art(buffer);

        await Assert.That(art).Contains("…/abcdefghijklm");
    }
}
