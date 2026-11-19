// CronSchedule.cs: #2680 F109. The parse-and-match half of periodic cron
// scheduling: a five-field cron expression (minute, hour, day of month,
// month, day of week) whose smallest interval is one minute. The helper is
// pure over its inputs. It starts no timer, reads no clock, writes no store
// and changes no product path. It is the data half of F109, the part that
// turns untrusted text into a validated schedule a future hosted service can
// act on.
using System.Globalization;

namespace Harbor.Tools.Builtin;

/// <summary>
///     A parsed five-field cron schedule (#2680 F109). Pure, immutable and
///     thread-safe. Fields are interpreted in the offset of the instant passed
///     to <see cref="Matches" /> or <see cref="NextOccurrence" />, so no local
///     time zone is consulted.
/// </summary>
/// <remarks>
///     <para>
///         Fields are minute (0-59), hour (0-23), day of month (1-31), month
///         (1-12 or JAN-DEC) and day of week (0-7 or SUN-SAT, where both 0 and
///         7 mean Sunday). Each field accepts a wildcard, a comma-separated
///         list, an inclusive range and a step, including combinations such
///         as <c>*/15</c> and <c>1-10/3</c>. Names are case-insensitive.
///     </para>
///     <para>
///         Day-of-month and day-of-week follow the usual cron rule: when both
///         are restricted, a day matches if either field matches; an
///         unrestricted field is neutral.
///     </para>
/// </remarks>
public sealed class CronSchedule
{
    /// <summary>Expected number of fields in a schedule.</summary>
    public const int FieldCount = 5;

    /// <summary>Smallest interval a five-field schedule can express, in seconds.</summary>
    public const int MinimumIntervalSeconds = 60;

    private const int SearchYears = 4;

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _days;
    private readonly bool[] _months;
    private readonly bool[] _weekdays;
    private readonly bool _allDays;
    private readonly bool _allWeekdays;

    internal CronSchedule(
        string expression,
        bool[] minutes,
        bool[] hours,
        bool[] days,
        bool[] months,
        bool[] weekdays,
        bool allDays,
        bool allWeekdays)
    {
        Expression = expression;
        _minutes = minutes;
        _hours = hours;
        _days = days;
        _months = months;
        _weekdays = weekdays;
        _allDays = allDays;
        _allWeekdays = allWeekdays;
    }

    /// <summary>The trimmed expression this schedule was parsed from.</summary>
    public string Expression { get; }

    /// <summary>True when <paramref name="instant" /> falls on the schedule.</summary>
    public bool Matches(DateTimeOffset instant)
    {
        if (!_minutes[instant.Minute] || !_hours[instant.Hour] || !_months[instant.Month]) return false;

        bool dayMatches = _days[instant.Day];
        bool weekdayMatches = _weekdays[(int)instant.DayOfWeek];

        if (_allDays) return weekdayMatches;
        if (_allWeekdays) return dayMatches;
        return dayMatches || weekdayMatches;
    }

    /// <summary>
    ///     Returns the first scheduled instant strictly after
    ///     <paramref name="after" />, or <see langword="null" /> when no
    ///     instant matches within the next four years. The search never
    ///     consults the local clock.
    /// </summary>
    public DateTimeOffset? NextOccurrence(DateTimeOffset after)
    {
        DateTimeOffset candidate;
        try
        {
            candidate = after.AddTicks(-(after.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        DateTimeOffset deadline;
        try
        {
            deadline = after.AddYears(SearchYears);
        }
        catch (ArgumentOutOfRangeException)
        {
            deadline = DateTimeOffset.MaxValue;
        }

        while (candidate < deadline)
        {
            if (Matches(candidate)) return candidate;
            if (candidate > deadline - TimeSpan.FromMinutes(1)) break;
            candidate = candidate.AddMinutes(1);
        }

        return null;
    }
}

/// <summary>
///     Parses five-field cron expressions into <see cref="CronSchedule" />
///     values (#2680 F109). Pure, stateless and thread-safe.
/// </summary>
/// <remarks>
///     Parsing never throws: any malformed or out-of-range input yields
///     <see langword="null" /> from <see cref="Parse" /> and
///     <see langword="false" /> from <see cref="TryParse" />.
/// </remarks>
public static class CronScheduleParser
{
    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] WeekdayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    /// <summary>
    ///     Parses <paramref name="expression" />, or returns
    ///     <see langword="null" /> when it is not a valid five-field cron
    ///     expression.
    /// </summary>
    public static CronSchedule? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;

        string trimmed = expression.Trim();
        string[] fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != CronSchedule.FieldCount) return null;

        var minutes = new bool[60];
        var hours = new bool[24];
        var days = new bool[32];
        var months = new bool[13];
        var weekdays = new bool[7];

        if (!TryParseField(fields[0], 0, 59, minutes, null, false, out _)) return null;
        if (!TryParseField(fields[1], 0, 23, hours, null, false, out _)) return null;
        if (!TryParseField(fields[2], 1, 31, days, null, false, out bool allDays)) return null;
        if (!TryParseField(fields[3], 1, 12, months, MonthNames, false, out _)) return null;
        if (!TryParseField(fields[4], 0, 7, weekdays, WeekdayNames, true, out bool allWeekdays)) return null;

        return new CronSchedule(trimmed, minutes, hours, days, months, weekdays, allDays, allWeekdays);
    }

    /// <summary>
    ///     Attempts to parse <paramref name="expression" />, writing
    ///     <see langword="null" /> on failure.
    /// </summary>
    /// <returns><see langword="true" /> when a schedule was produced.</returns>
    public static bool TryParse(string? expression, out CronSchedule? schedule)
    {
        schedule = Parse(expression);
        return schedule is not null;
    }

    private static bool TryParseField(
        string field,
        int min,
        int max,
        bool[] values,
        string[]? names,
        bool sundayAlias,
        out bool all)
    {
        all = false;
        if (field.Length == 0) return false;

        foreach (string piece in field.Split(','))
        {
            if (piece.Length == 0) return false;

            string[] rangeAndStep = piece.Split('/');
            if (rangeAndStep.Length > 2) return false;

            string range = rangeAndStep[0];
            if (range.Length == 0) return false;

            int step = 1;
            if (rangeAndStep.Length == 2)
            {
                if (!int.TryParse(rangeAndStep[1], NumberStyles.None, CultureInfo.InvariantCulture, out step))
                    return false;
                if (step <= 0) return false;
            }

            int from;
            int to;
            if (range == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                string[] bounds = range.Split('-');
                if (bounds.Length > 2) return false;
                if (!TryResolve(bounds[0], min, max, names, out from)) return false;

                to = from;
                if (bounds.Length == 2 && !TryResolve(bounds[1], min, max, names, out to)) return false;
                if (to < from) return false;
            }

            for (long value = from; value <= to; value += step)
            {
                int slot = sundayAlias && value == 7 ? 0 : (int)value;
                values[slot] = true;
            }
        }

        for (int value = min; value <= max; value++)
        {
            int slot = sundayAlias && value == 7 ? 0 : value;
            if (!values[slot]) return true;
        }

        all = true;
        return true;
    }

    private static bool TryResolve(string token, int min, int max, string[]? names, out int value)
    {
        value = 0;
        if (token.Length == 0) return false;

        if (names is not null)
        {
            for (int index = 0; index < names.Length; index++)
            {
                if (!string.Equals(token, names[index], StringComparison.OrdinalIgnoreCase)) continue;

                value = min + index;
                return value <= max;
            }
        }

        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
        return value >= min && value <= max;
    }
}
