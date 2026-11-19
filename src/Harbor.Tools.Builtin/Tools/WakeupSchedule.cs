// WakeupSchedule.cs: #2680 F108. Parses the schedule string an agent supplies
// when it asks to wake a session later — a relative delay such as "30s" or
// "2h", or an absolute ISO 8601 timestamp. The helper is pure over its inputs:
// it starts no timer, writes no store, reads no clock and changes no product
// path. It is the parse half of the wakeup feature, the part that turns agent
// text into a typed value a future scheduler can act on.
using System.Globalization;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Two shapes a wakeup request can take (#2680 F108).
/// </summary>
public enum WakeupScheduleKind
{
    /// <summary>A delay measured from the moment the request is handled.</summary>
    Relative,

    /// <summary>A fixed point in time, expressed as an ISO 8601 timestamp.</summary>
    Absolute
}

/// <summary>
///     A parsed wakeup schedule (#2680 F108). Pure, immutable and thread-safe.
/// </summary>
/// <param name="Kind">Whether the schedule is relative or absolute.</param>
/// <param name="Delay">The relative delay, or <see cref="TimeSpan.Zero" /> for an absolute schedule.</param>
/// <param name="At">The absolute instant, or <see langword="null" /> for a relative schedule.</param>
public readonly record struct WakeupSchedule(WakeupScheduleKind Kind, TimeSpan Delay, DateTimeOffset? At)
{
    /// <summary>True when this is a relative delay.</summary>
    public bool IsRelative => Kind == WakeupScheduleKind.Relative;

    /// <summary>True when this is an absolute timestamp.</summary>
    public bool IsAbsolute => Kind == WakeupScheduleKind.Absolute;

    /// <summary>
    ///     Resolves the schedule to an instant relative to <paramref name="now" />.
    /// </summary>
    public DateTimeOffset ComputeAt(DateTimeOffset now) => IsRelative ? now + Delay : At!.Value;

    /// <summary>
    ///     Resolves the schedule to a delay from <paramref name="now" />. An
    ///     absolute instant already in the past yields <see cref="TimeSpan.Zero" />.
    /// </summary>
    public TimeSpan ComputeDelay(DateTimeOffset now)
    {
        if (IsRelative) return Delay;
        TimeSpan remaining = At!.Value - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

/// <summary>
///     Parses agent-supplied wakeup schedules into <see cref="WakeupSchedule" />
///     values (#2680 F108). Pure, stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         Relative forms are a bare whole number of seconds, or a number with
///         an <c>s</c>/<c>sec</c>, <c>m</c>/<c>min</c>, <c>h</c>/<c>hr</c> or
///         <c>d</c> suffix (case-insensitive, optional surrounding whitespace).
///         The value must lie within
///         <see cref="MinDelaySeconds" /> and <see cref="MaxDelaySeconds" />;
///         out-of-range values are rejected rather than clamped.
///     </para>
///     <para>
///         Anything that is not a relative form is tried as an ISO 8601
///         timestamp with <see cref="CultureInfo.InvariantCulture" />. Parsing
///         never consults the local clock, so the result depends only on the
///         supplied string.
///     </para>
/// </remarks>
public static class WakeupScheduleParser
{
    /// <summary>Smallest accepted relative delay, in seconds.</summary>
    public const int MinDelaySeconds = 1;

    /// <summary>Largest accepted relative delay, in seconds (30 days).</summary>
    public const int MaxDelaySeconds = 30 * 24 * 60 * 60;

    /// <summary>
    ///     Parses <paramref name="raw" />, or returns <see langword="null" />
    ///     when it is neither a valid relative delay nor an ISO 8601 timestamp.
    /// </summary>
    public static WakeupSchedule? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (TryParseRelative(raw.Trim(), out TimeSpan delay))
            return new WakeupSchedule(WakeupScheduleKind.Relative, delay, null);

        if (TryParseAbsolute(raw.Trim(), out DateTimeOffset at))
            return new WakeupSchedule(WakeupScheduleKind.Absolute, TimeSpan.Zero, at);

        return null;
    }

    /// <summary>
    ///     Attempts to parse <paramref name="raw" />, writing
    ///     <see langword="default" /> on failure.
    /// </summary>
    /// <returns><see langword="true" /> when a schedule was produced.</returns>
    public static bool TryParse(string? raw, out WakeupSchedule schedule)
    {
        WakeupSchedule? parsed = Parse(raw);
        schedule = parsed ?? default;
        return parsed is not null;
    }

    private static bool TryParseRelative(string value, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        int multiplier = 1;

        if (EndsWithIgnoreCase(value, "sec"))
        {
            value = value[..^3];
        }
        else if (EndsWithIgnoreCase(value, "min"))
        {
            multiplier = 60;
            value = value[..^3];
        }
        else if (EndsWithIgnoreCase(value, "hr"))
        {
            multiplier = 60 * 60;
            value = value[..^2];
        }
        else if (EndsWithIgnoreCase(value, "d"))
        {
            multiplier = 24 * 60 * 60;
            value = value[..^1];
        }
        else if (EndsWithIgnoreCase(value, "h"))
        {
            multiplier = 60 * 60;
            value = value[..^1];
        }
        else if (EndsWithIgnoreCase(value, "m"))
        {
            multiplier = 60;
            value = value[..^1];
        }
        else if (EndsWithIgnoreCase(value, "s"))
        {
            value = value[..^1];
        }

        // No trim after suffix removal: NumberStyles.None then rejects an
        // embedded space such as "2 h" instead of silently accepting it.
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int amount)) return false;
        if (amount < MinDelaySeconds) return false;

        long total = (long)amount * multiplier;
        if (total < MinDelaySeconds || total > MaxDelaySeconds) return false;

        delay = TimeSpan.FromSeconds(total);
        return true;
    }

    private static bool EndsWithIgnoreCase(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseAbsolute(string value, out DateTimeOffset at)
    {
        // Require a date component so a time-only or word fragment cannot be
        // coerced into "today at ..." by the permissive parser, which would
        // silently consult the local clock and break purity.
        if (!value.Contains('-'))
        {
            at = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out at);
    }
}
