using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class DatePickerTests
{
    private const string KeyHint = "Left/Right: day  Up/Down: week  PgUp/PgDn: month  Enter: select";

    private static string Date(DateOnly date) => DatePicker.FormatDate(date);

    private static string[] Lines(DatePicker picker, int width = 32)
    {
        var buffer = TestRender.Buffer(width, picker.RowCount);
        picker.Paint(buffer, new Rect(0, 0, width, picker.RowCount));
        return TestRender.Art(buffer).Split('\n').Select(line => line.TrimEnd()).ToArray();
    }

    private static DatePicker Move(DatePicker picker, KeyCode key) =>
        picker.HandleKey(KeyEvent.Simple(key), out var next) ? next : throw new InvalidOperationException($"key {key} not consumed");

    [Test]
    public async Task FormatDate_InvariantIso()
    {
        await Assert.That(Date(new DateOnly(2026, 10, 13))).IsEqualTo("2026-10-13");
        await Assert.That(Date(new DateOnly(1, 1, 1))).IsEqualTo("0001-01-01");
    }

    [Test]
    public async Task ExternalSingleSelection_Formatted()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 15), selectionStart: new DateOnly(2026, 10, 13));

        await Assert.That(picker.Mode).IsEqualTo(DatePickerMode.Single);
        await Assert.That(picker.SelectionRange!.Value).IsEqualTo(new DateRange(new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 13)));
        await Assert.That(picker.FormatSelection()).IsEqualTo("Selected: 2026-10-13");
    }

    [Test]
    public async Task ExternalRangeSelection_Formatted()
    {
        var picker = new DatePicker(
            new DateOnly(2026, 10, 13),
            DatePickerMode.Range,
            new DateOnly(2026, 10, 13),
            new DateOnly(2026, 10, 20));

        await Assert.That(picker.FormatSelection()).IsEqualTo("Range: 2026-10-13 .. 2026-10-20");
        await Assert.That(picker.SelectionRange!.Value.IsSingle).IsFalse();
    }

    [Test]
    public async Task LeftRight_MoveOneDay()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 15));

        await Assert.That(Date(Move(picker, KeyCode.Right).Focus)).IsEqualTo("2026-10-16");
        await Assert.That(Date(Move(picker, KeyCode.Left).Focus)).IsEqualTo("2026-10-14");
    }

    [Test]
    public async Task UpDown_MoveOneWeek()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 15));

        await Assert.That(Date(Move(picker, KeyCode.Down).Focus)).IsEqualTo("2026-10-22");
        await Assert.That(Date(Move(picker, KeyCode.Up).Focus)).IsEqualTo("2026-10-08");
    }

    [Test]
    public async Task LeftRight_CrossMonthBoundary()
    {
        var last = new DatePicker(new DateOnly(2026, 10, 31));
        var first = new DatePicker(new DateOnly(2026, 11, 1));

        await Assert.That(Date(Move(last, KeyCode.Right).Focus)).IsEqualTo("2026-11-01");
        await Assert.That(Date(Move(first, KeyCode.Left).Focus)).IsEqualTo("2026-10-31");
    }

    [Test]
    public async Task PageUpDown_MoveMonthAndClampDay()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 31));

        await Assert.That(Date(Move(picker, KeyCode.PageDown).Focus)).IsEqualTo("2026-11-30");

        var january = new DatePicker(new DateOnly(2026, 1, 31));
        await Assert.That(Date(Move(january, KeyCode.PageUp).Focus)).IsEqualTo("2025-12-31");
    }

    [Test]
    public async Task HomeEnd_MonthBounds()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 15));

        await Assert.That(Date(Move(picker, KeyCode.Home).Focus)).IsEqualTo("2026-10-01");
        await Assert.That(Date(Move(picker, KeyCode.End).Focus)).IsEqualTo("2026-10-31");
    }

    [Test]
    public async Task DaysAndMonths_ClampAtDomainEdges()
    {
        var min = new DatePicker(DateOnly.MinValue);
        var max = new DatePicker(DateOnly.MaxValue);

        await Assert.That(Date(Move(min, KeyCode.Left).Focus)).IsEqualTo("0001-01-01");
        await Assert.That(Date(Move(max, KeyCode.PageDown).Focus)).IsEqualTo("9999-12-31");
    }

    [Test]
    public async Task Enter_Single_CommitsFocus()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 13));

        DatePicker committed = Move(picker, KeyCode.Enter);

        await Assert.That(committed.SelectionRange!.Value).IsEqualTo(new DateRange(new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 13)));
        await Assert.That(committed.FormatSelection()).IsEqualTo("Selected: 2026-10-13");
    }

    [Test]
    public async Task Enter_Range_TwoPresses_BuildOrderedRange()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 20), DatePickerMode.Range);

        DatePicker anchored = Move(picker, KeyCode.Enter);
        await Assert.That(anchored.PendingAnchor!.Value).IsEqualTo(new DateOnly(2026, 10, 20));
        await Assert.That(anchored.FormatSelection()).IsEqualTo("Range: 2026-10-20 .. (choose end)");

        DatePicker committed = Move(Move(anchored, KeyCode.Up), KeyCode.Enter);
        await Assert.That(committed.SelectionRange!.Value).IsEqualTo(new DateRange(new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 20)));
        await Assert.That(committed.PendingAnchor).IsNull();
        await Assert.That(committed.FormatSelection()).IsEqualTo("Range: 2026-10-13 .. 2026-10-20");
    }

    [Test]
    public async Task HandleKey_RefusesChordsAndRelease()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 13));
        var chord = KeyEvent.Simple(KeyCode.Right, KeyModifiers.Ctrl);
        var release = new KeyEvent(KeyCode.Right, default, KeyModifiers.None, KeyEventType.Release, false);

        await Assert.That(picker.HandleKey(chord, out var afterChord)).IsFalse();
        await Assert.That(afterChord).IsSameReferenceAs(picker);
        await Assert.That(picker.HandleKey(release, out var afterRelease)).IsFalse();
        await Assert.That(afterRelease).IsSameReferenceAs(picker);
        await Assert.That(picker.HandleKey(KeyEvent.Simple(KeyCode.F5), out var afterUnknown)).IsFalse();
        await Assert.That(afterUnknown).IsSameReferenceAs(picker);
    }

    [Test]
    public async Task ReusesCalendarView_ForGridAndRowCount()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 13));

        await Assert.That(picker.Calendar.Title).IsEqualTo("October 2026");
        await Assert.That(picker.RowCount).IsEqualTo(picker.Calendar.RowCount + 2);
    }

    [Test]
    public async Task Paint_Golden_SingleSelection()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 13), selectionStart: new DateOnly(2026, 10, 13));
        string[] lines = Lines(picker);

        await Assert.That(lines.Length).IsEqualTo(picker.RowCount);
        await Assert.That(lines[0].Contains("October 2026")).IsTrue();
        await Assert.That(lines[1].Contains("Mo Tu We")).IsTrue();
        await Assert.That(lines[^2]).IsEqualTo("Selected: 2026-10-13");
        await Assert.That(lines[^1]).IsEqualTo(KeyHint);
    }

    [Test]
    public async Task Paint_Golden_RangeSelectionMarksGrid()
    {
        var picker = new DatePicker(
            new DateOnly(2026, 10, 13),
            DatePickerMode.Range,
            new DateOnly(2026, 10, 13),
            new DateOnly(2026, 10, 20));
        string[] lines = Lines(picker);
        string art = string.Join("\n", lines);

        await Assert.That(art.Contains('·')).IsTrue();
        await Assert.That(lines[^2]).IsEqualTo("Range: 2026-10-13 .. 2026-10-20");
        await Assert.That(lines[^1]).IsEqualTo(KeyHint);
    }

    [Test]
    public async Task Paint_Golden_PendingRangeHint()
    {
        var picker = new DatePicker(new DateOnly(2026, 10, 20), DatePickerMode.Range, pendingAnchor: new DateOnly(2026, 10, 20));

        await Assert.That(Lines(picker)[^2]).IsEqualTo("Range: 2026-10-20 .. (choose end)");
    }

    [Test]
    public async Task Paint_Degenerate_NoThrow()
    {
        var buffer = TestRender.Buffer(8, 2);
        new DatePicker(new DateOnly(2026, 10, 13)).Paint(buffer, new Rect(0, 0, 0, 0));

        await Assert.That(TestRender.Art(buffer).Length).IsGreaterThan(0);
    }
}
