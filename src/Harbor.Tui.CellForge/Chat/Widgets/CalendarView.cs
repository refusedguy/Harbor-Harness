using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Cell-native month calendar (Spectre <c>Calendar</c> pattern): a compact
/// month grid — title row, weekday header, up to six week rows — with an
/// optional selected day and event-day markers. The grid is built lazily:
/// day numbers are computed once per (year, month) and cached until either
/// changes, so steady-state paints slice spans instead of re-deriving the
/// month. Events are a small day set owned by the host (deadlines, session
/// activity); event days paint in the warning tone with a <c>·</c> marker,
/// the selected day rides the active surface. Pure paint over immutable
/// month identity — navigation returns a new instance. Bounds-safe and
/// allocation-free on steady-state paint.
/// </summary>
public sealed class CalendarView
{
    private const int Columns = 7;
    private const int MaxWeeks = 6;

    private static readonly string[] Weekdays = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    private int _cachedYear = -1;
    private int _cachedMonth = -1;
    private int _startColumn;
    private int _daysInMonth;

    /// <summary>Calendar year (1..9999).</summary>
    public int Year { get; }

    /// <summary>Calendar month (1..12).</summary>
    public int Month { get; }

    /// <summary>Selected day of month (0 = none).</summary>
    public int SelectedDay { get; }

    /// <summary>Days carrying an event marker (1-based, out-of-range ignored).</summary>
    public IReadOnlySet<int> EventDays { get; }

    public CalendarView(int year, int month, int selectedDay = 0, IReadOnlySet<int>? eventDays = null)
    {
        Year = Math.Clamp(year, 1, 9999);
        Month = Math.Clamp(month, 1, 12);
        EventDays = eventDays ?? new HashSet<int>();
        SelectedDay = selectedDay >= 1 && selectedDay <= DaysIn(Month, Year) ? selectedDay : 0;
    }

    /// <summary>Row count for the current month: title + weekdays + week rows.</summary>
    public int RowCount
    {
        get
        {
            EnsureGrid();
            int weeks = (_startColumn + _daysInMonth + Columns - 1) / Columns;
            return 2 + Math.Max(1, weeks);
        }
    }

    /// <summary>Month title: <c>"October 2026"</c> (invariant).</summary>
    public string Title =>
        System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(Month)
        + " " + Year.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Same month next year/month (selection cleared).</summary>
    public CalendarView NextMonth() =>
        Month == 12 ? new CalendarView(Year + 1, 1) : new CalendarView(Year, Month + 1);

    /// <summary>Same month previous year/month (selection cleared).</summary>
    public CalendarView PrevMonth() =>
        Month == 1 ? new CalendarView(Year - 1, 12) : new CalendarView(Year, Month - 1);

    /// <summary>Paints the month grid into <paramref name="rect" /> (top-aligned, clipped).</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        EnsureGrid();
        var titleStyle = PanelChrome.TitleStyle;
        int y = rect.Y;
        int bottom = rect.Bottom;

        // Title row via the shared rule painter (centered title + rules).
        if (y < bottom)
        {
            RuleView.Paint(buffer, rect.X, y, rect.Width, Title, RuleJustify.Center);
            y++;
        }

        // Weekday header.
        if (y < bottom)
        {
            int x = rect.X;
            for (int c = 0; c < Columns && x < rect.Right; c++)
            {
                buffer.SetText(x, y, Weekdays[c], ChatPalette.Dim);
                x += 3;
            }

            y++;
        }

        var selectedBg = new CellStyle(bg: ChatPalette.Surface2);
        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var eventStyle = new CellStyle(ChatPalette.Warning);
        var dayStyle = ChatPalette.ToolArgs;

        for (int week = 0; y < bottom && week < MaxWeeks; week++)
        {
            bool any = false;
            for (int col = 0; col < Columns; col++)
            {
                int day = (week * Columns) + col - _startColumn + 1;
                int x = rect.X + (col * 3);
                if (x + 2 > rect.Right)
                {
                    break;
                }

                if (day < 1 || day > _daysInMonth)
                {
                    buffer.SetText(x, y, "   ", ChatPalette.Dim);
                    continue;
                }

                any = true;
                bool selected = day == SelectedDay;
                bool evt = EventDays.Contains(day);
                string text = day < 10 ? " " + day.ToString() : day.ToString();
                if (selected)
                {
                    var fill = Cell.From(new Rune(' '), selectedBg);
                    buffer.Fill(new Rect(x, y, Math.Min(3, rect.Right - x), 1), in fill);
                    buffer.SetText(x, y, text, selectedStyle);
                }
                else
                {
                    buffer.SetText(x, y, text, evt ? eventStyle : dayStyle);
                }

                // Event marker in the third cell of the day slot.
                var marker = new Rune(evt ? '·' : ' ');
                buffer.SetRune(x + 2, y, marker, evt ? eventStyle : ChatPalette.Dim);
            }

            if (!any && week > 0)
            {
                break;
            }

            y++;
            if ((_startColumn + _daysInMonth) <= (week + 1) * Columns)
            {
                break;
            }
        }
    }

    private void EnsureGrid()
    {
        if (_cachedYear == Year && _cachedMonth == Month)
        {
            return;
        }

        _cachedYear = Year;
        _cachedMonth = Month;
        _daysInMonth = DaysIn(Month, Year);
        // Monday-first column: .NET DayOfWeek is Sunday-first.
        // Unspecified kind: a calendar grid is a civil date, not an instant (S6562).
        int dow = (int)new DateTime(Year, Month, 1, 0, 0, 0, DateTimeKind.Unspecified).DayOfWeek;
        _startColumn = ((dow + 6) % Columns);
    }

    private static int DaysIn(int month, int year) =>
        DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), Math.Clamp(month, 1, 12));
}
