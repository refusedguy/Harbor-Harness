using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class GenericWidgetsTests
{
    private static string PaintField(GenericTextField field, int width = 20)
    {
        var buffer = TestRender.Buffer(width, 1);
        field.Paint(buffer, new Rect(0, 0, width, 1));
        return ScreenFixtures.Art(buffer);
    }

    [Test]
    public async Task TextField_Insert_AppendsAndMovesCursor()
    {
        var field = new GenericTextField();
        await Assert.That(field.HandleKey(KeyEvent.Char(new Rune('a')))).IsTrue();
        await Assert.That(field.HandleKey(KeyEvent.Char(new Rune('b')))).IsTrue();
        await Assert.That(field.Text).IsEqualTo("ab");
        await Assert.That(field.Cursor).IsEqualTo(2);
        await Assert.That(PaintField(field).Contains("ab")).IsTrue();
    }

    [Test]
    public async Task TextField_Backspace_RemovesSurrogatePairAsOne()
    {
        var field = new GenericTextField();
        field.SetText("a\uD834\uDD1E"); // 'a' + musical symbol G clef (surrogate pair)
        field.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(field.HandleKey(KeyEvent.Simple(KeyCode.Backspace))).IsTrue();
        await Assert.That(field.Text).IsEqualTo("a");
        await Assert.That(field.Cursor).IsEqualTo(1);
    }

    [Test]
    public async Task TextField_Placeholder_ShowsWhenEmpty()
    {
        var field = new GenericTextField { Placeholder = "hint", IsFocused = false };
        await Assert.That(PaintField(field).Contains("hint")).IsTrue();
    }

    [Test]
    public async Task TextField_Mask_HidesText()
    {
        var field = new GenericTextField { Mask = new Rune('•'), IsFocused = false };
        field.SetText("secret");
        string art = PaintField(field);
        await Assert.That(art.Contains("secret")).IsFalse();
        await Assert.That(art.Contains("••••••")).IsTrue();
    }

    [Test]
    public async Task TextField_MaxLength_RefusesOverflow()
    {
        var field = new GenericTextField { MaxLength = 2 };
        field.SetText("ab");
        await Assert.That(field.HandleKey(KeyEvent.Char(new Rune('c')))).IsFalse();
        await Assert.That(field.Text).IsEqualTo("ab");
    }

    [Test]
    public async Task TextField_CommandModifier_IsNotText()
    {
        var field = new GenericTextField();
        await Assert.That(field.HandleKey(KeyEvent.Char(new Rune('c'), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(field.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TextField_Release_IsIgnored()
    {
        var field = new GenericTextField();
        var release = new KeyEvent(KeyCode.Char, new Rune('x'), KeyModifiers.None, KeyEventType.Release, false);
        await Assert.That(field.HandleKey(in release)).IsFalse();
        await Assert.That(field.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TextField_Required_EmptyFails()
    {
        var field = new GenericTextField { Required = true };
        await Assert.That(field.Validate()).IsEqualTo("Required.");
        field.SetText("x");
        await Assert.That(field.Validate()).IsNull();
    }

    [Test]
    public async Task SelectionList_Move_ClampsWithoutWrap()
    {
        var list = new GenericSelectionList();
        list.SetItems(["a", "b", "c"]);
        list.Move(-5);
        await Assert.That(list.SelectedIndex).IsEqualTo(0);
        list.Move(99);
        await Assert.That(list.SelectedIndex).IsEqualTo(2);
        await Assert.That(list.SelectedItem).IsEqualTo("c");
    }

    [Test]
    public async Task SelectionList_Keys_MoveSelection()
    {
        var list = new GenericSelectionList();
        list.SetItems(["a", "b", "c"]);
        await Assert.That(list.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(list.SelectedIndex).IsEqualTo(1);
        await Assert.That(list.HandleKey(KeyEvent.Simple(KeyCode.End))).IsTrue();
        await Assert.That(list.SelectedIndex).IsEqualTo(2);
        await Assert.That(list.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
    }

    [Test]
    public async Task SelectionList_Paint_MarksSelectedRow()
    {
        var list = new GenericSelectionList { MaxVisibleRows = 4 };
        list.SetItems(["apple", "banana"]);
        list.MoveTo(1);
        var buffer = TestRender.Buffer(20, 4);
        list.Paint(buffer, new Rect(0, 0, 20, 4));
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("apple")).IsTrue();
        await Assert.That(art.Contains("›") || art.Contains(">")).IsTrue();
    }

    [Test]
    public async Task SelectionList_Scroll_FollowsSelection()
    {
        var list = new GenericSelectionList { MaxVisibleRows = 2 };
        list.SetItems(["one", "two", "three", "four"]);
        list.MoveTo(3);
        await Assert.That(list.Offset).IsGreaterThan(0);
        var buffer = TestRender.Buffer(20, 2);
        list.Paint(buffer, new Rect(0, 0, 20, 2));
        await Assert.That(ScreenFixtures.Art(buffer).Contains("four")).IsTrue();
    }

    [Test]
    public async Task ProgressBar_Half_SplitsFillAndTrack()
    {
        var buffer = TestRender.Buffer(10, 1);
        GenericProgressBar.Paint(buffer, new Rect(0, 0, 10, 1), new GenericProgressState(0.5, ShowLabel: false));
        await Assert.That(ScreenFixtures.Art(buffer)).IsEqualTo(
            new string(GenericProgressBar.Filled, 5) + new string(GenericProgressBar.Track, 5));
    }

    [Test]
    public async Task ProgressBar_Clamp_PinsNaNAndOutOfRange()
    {
        await Assert.That(GenericProgressBar.ClampRatio(double.NaN)).IsEqualTo(0);
        await Assert.That(GenericProgressBar.ClampRatio(-1)).IsEqualTo(0);
        await Assert.That(GenericProgressBar.ClampRatio(9)).IsEqualTo(1);
        await Assert.That(GenericProgressBar.LabelText(new GenericProgressState(0.5))).IsEqualTo("50%");
    }

    [Test]
    public async Task Spinner_CyclesThroughFrames()
    {
        var spinner = new GenericSpinner();
        for (int i = 0; i < GenericSpinner.FrameCount; i++)
        {
            spinner.Advance();
        }

        await Assert.That(spinner.Frame).IsEqualTo(0);
        var buffer = TestRender.Buffer(4, 1);
        spinner.Paint(buffer, new Rect(0, 0, 4, 1));
        await Assert.That(ScreenFixtures.Art(buffer).Trim()).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task Table_PaintsFrameHeaderAndRows()
    {
        var table = new GenericTable();
        table.SetHeaders(["name", "ok"]);
        table.AddRow(["alpha", "yes"]);
        var buffer = TestRender.Buffer(30, 8);
        table.Paint(buffer, new Rect(0, 0, 30, 8));
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("name")).IsTrue();
        await Assert.That(art.Contains("alpha")).IsTrue();
        await Assert.That(art.Contains("┌")).IsTrue();
        await Assert.That(art.Contains("└")).IsTrue();
    }

    [Test]
    public async Task Table_LongCell_TruncatesWithEllipsis()
    {
        var table = new GenericTable();
        table.SetHeaders(["h"]);
        table.AddRow(["0123456789abcdef"]);
        var buffer = TestRender.Buffer(12, 8);
        table.Paint(buffer, new Rect(0, 0, 12, 8));
        await Assert.That(ScreenFixtures.Art(buffer).Contains("…")).IsTrue();
    }

    [Test]
    public async Task Table_MeasureWidths_ShrinksWidestFirst()
    {
        var table = new GenericTable();
        table.SetHeaders(["aaaa", "b"]);
        int[] widths = table.MeasureWidths(14);
        await Assert.That(widths.Length).IsEqualTo(2);
        // 14 cols: borders 3 + padding 4 = 7, avail 7 → both shrink toward MinCell.
        await Assert.That(widths[0] + widths[1]).IsEqualTo(7);
    }

    [Test]
    public async Task Form_Tab_CyclesFocusAcrossRows()
    {
        var form = new GenericForm();
        form.Add(new GenericTextField { Label = "name" });
        form.Add(new GenericTextField { Label = "city" });
        await Assert.That(form.FocusedIndex).IsEqualTo(0);
        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(form.FocusedIndex).IsEqualTo(1);
        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(form.FocusedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Form_Required_ReportsErrorAndPaintsIt()
    {
        var form = new GenericForm();
        form.Add(new GenericTextField { Label = "name", Required = true });
        await Assert.That(form.IsValid()).IsFalse();
        string?[] errors = form.ValidateAll();
        await Assert.That(errors.Length).IsEqualTo(1);
        await Assert.That(errors[0]).IsEqualTo("Required.");

        var buffer = TestRender.Buffer(30, 4);
        form.Paint(buffer, new Rect(0, 0, 30, 4));
        await Assert.That(ScreenFixtures.Art(buffer).Contains("Required.")).IsTrue();
    }

    [Test]
    public async Task Form_Typing_ReachesFocusedRow()
    {
        var form = new GenericForm();
        var name = new GenericTextField { Label = "name" };
        form.Add(name);
        await Assert.That(form.HandleKey(KeyEvent.Char(new Rune('z')))).IsTrue();
        await Assert.That(name.Text).IsEqualTo("z");
    }

    [Test]
    public async Task Demo_Build_ContainsEveryWidget()
    {
        var buffer = GenericWidgetsDemo.Build(60, 24);
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("Generic widgets")).IsTrue();
        await Assert.That(art.Contains("hello cells")).IsTrue();
        await Assert.That(art.Contains("beta")).IsTrue();
        await Assert.That(art.Contains("42%")).IsTrue();
        await Assert.That(art.Contains("alpha")).IsTrue();
    }
}
