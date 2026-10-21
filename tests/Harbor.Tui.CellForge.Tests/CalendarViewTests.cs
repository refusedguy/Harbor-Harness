using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class CalendarViewTests
{
    private static string Art(CalendarView cal, int width = 24)
    {
        var buffer = TestRender.Buffer(width, cal.RowCount);
        cal.Paint(buffer, new Rect(0, 0, width, cal.RowCount));
        return TestRender.Art(buffer);
    }

    [Test]
    public async Task Title_MonthAndYear()
    {
        var cal = new CalendarView(2026, 10);
        await Assert.That(cal.Title).IsEqualTo("October 2026");
    }

    [Test]
    public async Task RowCount_BetweenFourAndEight()
    {
        var cal = new CalendarView(2026, 10);
        await Assert.That(cal.RowCount).IsGreaterThanOrEqualTo(4);
        await Assert.That(cal.RowCount).IsLessThanOrEqualTo(8);
    }

    [Test]
    public async Task Paint_RendersTitleAndWeekdays()
    {
        string art = Art(new CalendarView(2026, 10));
        await Assert.That(art.Contains("October 2026")).IsTrue();
        await Assert.That(art.Contains("Mo")).IsTrue();
    }

    [Test]
    public async Task Paint_SelectedDay_MarksRow()
    {
        string art = Art(new CalendarView(2026, 10, selectedDay: 13));
        await Assert.That(art.Contains("13")).IsTrue();
    }

    [Test]
    public async Task Paint_EventDay_MarkerPresent()
    {
        string art = Art(new CalendarView(2026, 10, eventDays: new HashSet<int> { 13 }));
        await Assert.That(art.Contains('·')).IsTrue();
    }

    [Test]
    public async Task Paint_NoEvents_NoMarker()
    {
        // Title rule rows also paint '─', never '·' — the marker glyph is
        // event-only, so a plain month must not contain it.
        string art = Art(new CalendarView(2026, 2));
        await Assert.That(art.Contains('·')).IsFalse();
    }

    [Test]
    public async Task SelectedDay_OutOfRange_Clears()
    {
        var cal = new CalendarView(2026, 2, selectedDay: 31);
        await Assert.That(cal.SelectedDay).IsEqualTo(0);
    }

    [Test]
    public async Task NextPrev_MoveMonth()
    {
        var dec = new CalendarView(2026, 12);
        await Assert.That(dec.NextMonth().Month).IsEqualTo(1);
        await Assert.That(dec.NextMonth().Year).IsEqualTo(2027);
        var jan = new CalendarView(2026, 1);
        await Assert.That(jan.PrevMonth().Month).IsEqualTo(12);
        await Assert.That(jan.PrevMonth().Year).IsEqualTo(2025);
    }

    [Test]
    public async Task Paint_Degenerate_NoThrow()
    {
        var buffer = TestRender.Buffer(8, 2);
        new CalendarView(2026, 10).Paint(buffer, new Rect(0, 0, 0, 0));
        await Assert.That(TestRender.Art(buffer).Length).IsGreaterThan(0);
    }
}
