// SessionExportEligibility.cs: #2680 F173. The pure policy behind the runtime
// session-export kill switch. It classifies a supplied value for
// HARBOR_EXPORT_ENABLED and never reads the environment itself, so it stays
// testable, AOT-safe and golden-neutral. Callers pass in the raw value they
// obtained from configuration or the process environment.
namespace Harbor.Tools.Builtin;

/// <summary>
///     Result of classifying a session-export kill-switch value.
/// </summary>
public enum ExportSwitchDecision
{
    /// <summary>Export is explicitly or implicitly allowed.</summary>
    Enabled,

    /// <summary>Export is explicitly denied.</summary>
    Disabled,

    /// <summary>The supplied value is not a recognised switch value; treated as denied.</summary>
    Invalid,
}

/// <summary>
///     Pure policy for the <c>HARBOR_EXPORT_ENABLED</c> runtime kill switch
///     (#2680 F173). Stateless and thread-safe; holds no process or registry
///     state and performs no I/O.
/// </summary>
/// <remarks>
///     <para>
///         Absent, empty or whitespace-only values mean enabled, matching a
///         default install where export is available. The recognised on values
///         are <c>1</c>, <c>true</c>, <c>yes</c> and <c>on</c>; the recognised
///         off values are <c>0</c>, <c>false</c>, <c>no</c> and <c>off</c>.
///         Comparison is case-insensitive and the value is trimmed first.
///     </para>
///     <para>
///         An unrecognised non-empty value is reported as
///         <see cref="ExportSwitchDecision.Invalid" /> and resolves to disabled.
///         Failing closed keeps an unknown switch from silently enabling an
///         export path that an operator meant to restrict.
///     </para>
/// </remarks>
public static class SessionExportEligibility
{
    /// <summary>Name of the environment variable this policy interprets.</summary>
    public const string EnvironmentVariableName = "HARBOR_EXPORT_ENABLED";

    /// <summary>
    ///     Classifies a raw switch value.
    /// </summary>
    /// <param name="raw">
    ///     The value from <see cref="EnvironmentVariableName" />, or
    ///     <see langword="null" /> when the variable is not set.
    /// </param>
    /// <returns>The decision for that value.</returns>
    public static ExportSwitchDecision Classify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ExportSwitchDecision.Enabled;

        string value = raw.Trim();

        if (IsOneOf(value, "1", "true", "yes", "on")) return ExportSwitchDecision.Enabled;
        if (IsOneOf(value, "0", "false", "no", "off")) return ExportSwitchDecision.Disabled;

        return ExportSwitchDecision.Invalid;
    }

    /// <summary>
    ///     Resolves a raw switch value to a boolean, failing closed on an
    ///     unrecognised value.
    /// </summary>
    /// <param name="raw">The value to classify.</param>
    /// <returns><see langword="true" /> only when export is allowed.</returns>
    public static bool IsEnabled(string? raw) => Classify(raw) is ExportSwitchDecision.Enabled;

    /// <summary>
    ///     Classifies and resolves a raw switch value in one call.
    /// </summary>
    /// <param name="raw">The value to classify.</param>
    /// <param name="enabled">The resolved boolean; <see langword="false" /> for an invalid value.</param>
    /// <returns>
    ///     <see langword="false" /> only when the value is unrecognised, so a
    ///     caller can log a bad configuration without treating it as a valid deny.
    /// </returns>
    public static bool TryParse(string? raw, out bool enabled)
    {
        ExportSwitchDecision decision = Classify(raw);
        enabled = decision is ExportSwitchDecision.Enabled;
        return decision is not ExportSwitchDecision.Invalid;
    }

    private static bool IsOneOf(string value, string first, string second, string third, string fourth) =>
        string.Equals(value, first, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, second, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, third, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, fourth, StringComparison.OrdinalIgnoreCase);
}
