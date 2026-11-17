using System.Globalization;
using Harbor.Tui.CellForge.Widgets;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>What a <see cref="DatePicker"/> commits on Enter.</summary>
public enum DatePickerMode
{
    /// <summary>Enter stores one date (start and end are the same).</summary>
    Single,

    /// <summary>The first Enter anchors a start, the second commits an ordered range.</summary>
    Range,
}

/// <summary>An inclusive civil-date range. The picker never stores an inverted pair.</summary>
/// <param name="Start">Earlier date.</param>
/// <param name="End">Later date (equal to <paramref name="Start"/> for a single day).</param>
public readonly record struct DateRange(DateOnly Start, DateOnly End)
{
    /// <summary>True when the range covers one day.</summary>
    public bool IsSingle => Start == End;
}

/// <summary>
/// Host-neutral date picker built on top of <see cref="CalendarView"/>. The
/// calendar still owns every grid cell: this type supplies the month, the
/// keyboard cursor and a day set for the committed selection, then delegates
/// the entire grid paint to the calendar. No month geometry is duplicated.
/// </summary>
/// <remarks>
/// <para>
///     The selection is external data supplied by the host through the
///     constructor and read back through <see cref="SelectionRange"/>. Nothing
///     here knows about Harbor sessions, messages or panels. Navigation and
///     selection are pure transitions: every accepted key returns a new picker,
///     so a host can keep the old value for undo or comparison.
/// </para>
/// <para>
///     Keys are named motions only (no typed runes): Left/Right move a day,
///     Up/Down a week, PageUp/PageDown a month, Home/End the month bounds, and
///     Enter commits. Chords and release events are refused by the same command
///     gate the other neutral widgets use.
/// </para>
/// </remarks>
public sealed class DatePicker
{
    private const int FooterRows = 2;
    private const string KeyHint = "Left/Right: day  Up/Down: week  PgUp/PgDn: month  Enter: select";

    private readonly CalendarView _calendar;

    /// <summary>Keyboard cursor; it also anchors the visible month.</summary>
    public DateOnly Focus { get; }

    /// <summary>Selection kind.</summary>
    public DatePickerMode Mode { get; }

    /// <summary>Committed start date, or null before the first commit.</summary>
    public DateOnly? SelectionStart { get; }

    /// <summary>Committed end date (equal to start in single mode).</summary>
    public DateOnly? SelectionEnd { get; }

    /// <summary>First endpoint of an in-progress range, or null when none is pending.</summary>
    public DateOnly? PendingAnchor { get; }

    /// <summary>Committed selection as one value, or null when nothing is selected.</summary>
    public DateRange? SelectionRange =>
        SelectionStart is { } start ? new DateRange(start, SelectionEnd ?? start) : null;

    /// <summary>The reused calendar grid for the focused month.</summary>
    public CalendarView Calendar => _calendar;

    /// <summary>Grid rows plus the selection line and the key-hint line.</summary>
    public int RowCount => _calendar.RowCount + FooterRows;

    /// <param name="focus">Initial keyboard cursor and visible month.</param>
    /// <param name="mode">Single-date or range selection.</param>
    /// <param name="selectionStart">Host-supplied committed start, if any.</param>
    /// <param name="selectionEnd">Host-supplied committed end; defaults to start.</param>
    /// <param name="pendingAnchor">Host-supplied in-progress range anchor (range mode only).</param>
    public DatePicker(
        DateOnly focus,
        DatePickerMode mode = DatePickerMode.Single,
        DateOnly? selectionStart = null,
        DateOnly? selectionEnd = null,
        DateOnly? pendingAnchor = null)
    {
        Focus = focus;
        Mode = mode;
        if (mode == DatePickerMode.Single)
        {
            pendingAnchor = null;
            if (selectionStart is not null)
            {
                selectionEnd = selectionStart;
            }
        }

        SelectionStart = selectionStart;
        SelectionEnd = selectionStart is null ? null : selectionEnd ?? selectionStart;
        PendingAnchor = pendingAnchor;
        _calendar = BuildCalendar();
    }

    /// <summary>Moves the cursor by a signed day count, clamped to the DateOnly domain.</summary>
    public DatePicker MoveDays(int delta)
    {
        long raw = Focus.DayNumber + (long)delta;
        int day = (int)Math.Clamp(raw, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber);
        return WithFocus(DateOnly.FromDayNumber(day));
    }

    /// <summary>Moves the cursor by whole months, clamping the day to the target month.</summary>
    public DatePicker MoveMonths(int delta)
    {
        long raw = ((long)Focus.Year * 12) + Focus.Month - 1 + delta;
        long packed = Math.Clamp(raw, 12L, (9999L * 12) + 11);
        int year = (int)(packed / 12);
        int month = (int)(packed % 12) + 1;
        int day = Math.Min(Focus.Day, DateTime.DaysInMonth(year, month));
        return WithFocus(new DateOnly(year, month, day));
    }

    /// <summary>Moves the cursor to the first day of the focused month.</summary>
    public DatePicker FirstOfMonth() => WithFocus(new DateOnly(Focus.Year, Focus.Month, 1));

    /// <summary>Moves the cursor to the last day of the focused month.</summary>
    public DatePicker LastOfMonth() =>
        WithFocus(new DateOnly(Focus.Year, Focus.Month, DateTime.DaysInMonth(Focus.Year, Focus.Month)));

    /// <summary>
    /// Commits the focused date. Single mode stores it outright; range mode
    /// anchors on the first press and commits an ordered range on the second.
    /// </summary>
    public DatePicker Select()
    {
        if (Mode == DatePickerMode.Single)
        {
            return new DatePicker(Focus, Mode, Focus, Focus);
        }

        if (PendingAnchor is not { } anchor)
        {
            return new DatePicker(Focus, Mode, null, null, Focus);
        }

        DateOnly start = anchor <= Focus ? anchor : Focus;
        DateOnly end = anchor >= Focus ? anchor : Focus;
        return new DatePicker(Focus, Mode, start, end);
    }

    /// <summary>
    /// Applies a navigation or selection key. Unhandled keys return
    /// <paramref name="next"/> = this and the method returns false.
    /// </summary>
    public bool HandleKey(in KeyEvent key, out DatePicker next)
    {
        next = this;
        if ((key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat)
            || !key.Modifiers.IsUnmodified())
        {
            return false;
        }

        next = key.Key switch
        {
            KeyCode.Left => MoveDays(-1),
            KeyCode.Right => MoveDays(1),
            KeyCode.Up => MoveDays(-7),
            KeyCode.Down => MoveDays(7),
            KeyCode.PageUp => MoveMonths(-1),
            KeyCode.PageDown => MoveMonths(1),
            KeyCode.Home => FirstOfMonth(),
            KeyCode.End => LastOfMonth(),
            KeyCode.Enter => Select(),
            _ => this,
        };
        return !ReferenceEquals(next, this);
    }

    /// <summary>Culture-invariant ISO date, the one formatter used by the footer.</summary>
    public static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>One-line description of the current (or pending) selection.</summary>
    public string FormatSelection()
    {
        if (Mode == DatePickerMode.Single)
        {
            return SelectionStart is { } single
                ? "Selected: " + FormatDate(single)
                : "Selected: (none)";
        }

        if (PendingAnchor is { } anchor && SelectionStart is null)
        {
            return "Range: " + FormatDate(anchor) + " .. (choose end)";
        }

        if (SelectionStart is { } start && SelectionEnd is { } end)
        {
            return start == end
                ? "Range: " + FormatDate(start)
                : "Range: " + FormatDate(start) + " .. " + FormatDate(end);
        }

        return "Range: (none)";
    }

    /// <summary>Paints the reused month grid, the selection line and the key hints.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        _calendar.Paint(buffer, rect);
        int y = rect.Y + _calendar.RowCount;
        if (y < rect.Bottom)
        {
            buffer.SetText(rect.X, y++, FormatSelection(), ChatPalette.Accent);
        }

        if (y < rect.Bottom)
        {
            buffer.SetText(rect.X, y, KeyHint, ChatPalette.Dim);
        }
    }

    private DatePicker WithFocus(DateOnly focus) =>
        new(focus, Mode, SelectionStart, SelectionEnd, PendingAnchor);

    private CalendarView BuildCalendar()
    {
        HashSet<int>? marks = null;
        if (SelectionStart is { } start)
        {
            DateOnly end = SelectionEnd ?? start;
            var monthStart = new DateOnly(Focus.Year, Focus.Month, 1);
            var monthEnd = new DateOnly(Focus.Year, Focus.Month, DateTime.DaysInMonth(Focus.Year, Focus.Month));
            DateOnly from = start < monthStart ? monthStart : start;
            DateOnly to = end > monthEnd ? monthEnd : end;
            if (from <= to)
            {
                marks = new HashSet<int>();
                for (DateOnly day = from; day <= to; day = day.AddDays(1))
                {
                    marks.Add(day.Day);
                }
            }
        }

        if (PendingAnchor is { } anchor
            && anchor.Year == Focus.Year
            && anchor.Month == Focus.Month)
        {
            marks ??= new HashSet<int>();
            marks.Add(anchor.Day);
        }

        return new CalendarView(Focus.Year, Focus.Month, Focus.Day, marks);
    }
}
