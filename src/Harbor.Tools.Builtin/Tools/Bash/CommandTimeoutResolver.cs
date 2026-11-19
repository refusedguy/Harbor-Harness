// CommandTimeoutResolver.cs: #2680 F177. The parsing and bounds half of a
// centralised command timeout. It turns the value a config or env source
// supplies into the whole-second timeout a CancellationTokenSource receives.
// The helper is pure over its inputs: it starts no process, reads no file and
// environment, resolves no config store and changes no product path.
using System.Globalization;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Parses and validates a command timeout (#2680 F177). Pure, stateless
///     and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         The bounds match the ones <c>BashTool</c> already applies inline:
///         a request below <see cref="MinSeconds" /> or above
///         <see cref="MaxSeconds" /> falls back to <see cref="DefaultSeconds" />
///         rather than being clamped. That keeps this primitive a faithful
///         extraction of the existing behaviour.
///     </para>
///     <para>
///         <see cref="TryParse" /> handles syntax only: a bare integer is a
///         number of seconds; <c>s</c>/<c>sec</c> and <c>m</c>/<c>min</c>
///         suffixes are accepted case-insensitively. Policy lives in
///         <see cref="Resolve" />, so a syntactically valid but out of
///         range value is still rejected there.
///     </para>
/// </remarks>
public static class CommandTimeoutResolver
{
    /// <summary>Timeout used when no valid request is supplied.</summary>
    public const int DefaultSeconds = 30;

    /// <summary>Smallest accepted timeout, in seconds.</summary>
    public const int MinSeconds = 1;

    /// <summary>Largest accepted timeout, in seconds.</summary>
    public const int MaxSeconds = 600;

    /// <summary>Conventional env var a future wiring can read.</summary>
    public const string EnvironmentVariableName = "HARBOR_COMMAND_TIMEOUT";

    /// <summary>
    ///     Returns the requested timeout when it is inside the allowed range,
    ///     otherwise <see cref="DefaultSeconds" />.
    /// </summary>
    /// <param name="requestedSeconds">Requested timeout, or <see langword="null" />.</param>
    /// <returns>The effective timeout in whole seconds.</returns>
    public static int Resolve(int? requestedSeconds) =>
        requestedSeconds is >= MinSeconds and <= MaxSeconds
            ? requestedSeconds.Value
            : DefaultSeconds;

    /// <summary>
    ///     Parses a configured timeout string into whole seconds. No range
    ///     policy is applied here; use <see cref="Resolve" /> for that.
    /// </summary>
    /// <param name="raw">Configured value, e.g. <c>30</c>, <c>15s</c> or <c>2m</c>.</param>
    /// <param name="seconds">Parsed seconds, or zero on failure.</param>
    /// <returns><see langword="true" /> when the value was parsed.</returns>
    public static bool TryParse(string? raw, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string value = raw.Trim();
        int multiplier = 1;

        if (value.EndsWith("min", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 60;
            value = value[..^3];
        }
        else if (value.EndsWith("sec", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^3];
        }
        else if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 60;
            value = value[..^1];
        }
        else if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^1];
        }

        value = value.Trim();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int amount)) return false;
        if (amount < 1) return false;

        long total = (long)amount * multiplier;
        if (total > int.MaxValue) return false;

        seconds = (int)total;
        return true;
    }

    /// <summary>
    ///     Parses a configured value and applies the range policy in one step.
    ///     Any invalid or out of range value yields <see cref="DefaultSeconds" />.
    /// </summary>
    /// <param name="configured">Configured value, possibly <see langword="null" />.</param>
    /// <returns>The effective timeout in whole seconds.</returns>
    public static int ResolveConfigured(string? configured) =>
        TryParse(configured, out int seconds) ? Resolve(seconds) : DefaultSeconds;

    /// <summary>
    ///     Converts a requested timeout to a <see cref="TimeSpan" />, applying
    ///     the same range policy.
    /// </summary>
    /// <param name="requestedSeconds">Requested timeout, or <see langword="null" />.</param>
    /// <returns>The effective timeout as a <see cref="TimeSpan" />.</returns>
    public static TimeSpan ToTimeSpan(int? requestedSeconds) =>
        TimeSpan.FromSeconds(Resolve(requestedSeconds));
}
