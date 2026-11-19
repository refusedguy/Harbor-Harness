// ExperimentalFlagPolicy.cs: #2680 F172. The pure policy behind the unified
// HARBOR_EXPERIMENTAL_* flag namespace. It owns the canonical variable prefix,
// flag-name validation and value resolution but never reads the process
// environment, so it stays testable, AOT-safe and golden-neutral. Callers pass
// in the raw value they obtained from configuration or the environment.
//
// #3397: the policy moved here from Harbor.Tools.Builtin so HarborConfig — the
// component that decides whether an experimental feature is on — can call it
// without an Application -> Infrastructure project reference (the layer matrix
// forbids that edge). Evaluate is the gate: the config resolves every flag
// through it BEFORE a feature is enabled, and an invalid flag fails closed.
namespace Harbor.Application.Configuration;

/// <summary>
///     Result of resolving an experimental-flag switch value.
/// </summary>
public enum ExperimentalFlagDecision
{
    /// <summary>The flag is explicitly enabled.</summary>
    Enabled,

    /// <summary>The flag is off, either explicitly or by default.</summary>
    Disabled,

    /// <summary>The value is not recognised; resolution fails closed to disabled.</summary>
    Invalid,
}

/// <summary>
///     Pure policy for the unified <c>HARBOR_EXPERIMENTAL_*</c> flag namespace
///     (#2680 F172). Experimental flags are opt-in: an unset or blank value
///     leaves the flag disabled, unlike an ordinary release switch.
/// </summary>
/// <remarks>
///     <para>
///         Flag names are uppercase identifiers made of letters, digits and
///         underscores. They must start with a letter and cannot end with an
///         underscore. <see cref="VariableName" /> returns
///         <see langword="null" /> for a name that does not follow this rule,
///         so a malformed flag never reaches the process environment.
///     </para>
///     <para>
///         The recognised on values are <c>1</c>, <c>true</c>, <c>yes</c> and
///         <c>on</c>; the recognised off values are <c>0</c>, <c>false</c>,
///         <c>no</c> and <c>off</c>. Comparison is case-insensitive and the
///         value is trimmed first. An unrecognised non-empty value is reported
///         as <see cref="ExperimentalFlagDecision.Invalid" /> and resolves to
///         disabled. Failing closed keeps a typo from silently turning on an
///         unfinished feature.
///     </para>
/// </remarks>
public static class ExperimentalFlagPolicy
{
    /// <summary>Prefix every experimental flag variable carries.</summary>
    public const string Prefix = "HARBOR_EXPERIMENTAL_";

    /// <summary>
    ///     Builds the canonical environment variable name for a flag.
    /// </summary>
    /// <param name="flag">
    ///     Flag name such as <c>AGENT_MANAGER</c>, validated by
    ///     <see cref="IsValidFlagName" />.
    /// </param>
    /// <returns>
    ///     The canonical variable name, or <see langword="null" /> when the
    ///     flag name is not a valid uppercase identifier.
    /// </returns>
    public static string? VariableName(string? flag) =>
        IsValidFlagName(flag) ? Prefix + flag : null;

    /// <summary>
    ///     Reports whether a flag name can be used to build a variable name.
    /// </summary>
    /// <param name="flag">The candidate name.</param>
    /// <returns><see langword="true" /> when the name is a valid uppercase identifier.</returns>
    public static bool IsValidFlagName(string? flag)
    {
        if (string.IsNullOrEmpty(flag)) return false;
        if (flag[0] is < 'A' or > 'Z') return false;
        if (flag[^1] == '_') return false;

        for (int i = 1; i < flag.Length; i++)
        {
            char c = flag[i];
            bool allowed = c is >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_';
            if (!allowed) return false;
        }

        return true;
    }

    /// <summary>
    ///     Classifies a raw flag value.
    /// </summary>
    /// <param name="raw">The value supplied by the caller, or <see langword="null" /> when unset.</param>
    /// <returns>The decision for that value.</returns>
    public static ExperimentalFlagDecision Classify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ExperimentalFlagDecision.Disabled;

        string value = raw.Trim();
        if (Matches(value, "1", "true", "yes", "on")) return ExperimentalFlagDecision.Enabled;
        if (Matches(value, "0", "false", "no", "off")) return ExperimentalFlagDecision.Disabled;

        return ExperimentalFlagDecision.Invalid;
    }

    /// <summary>
    ///     Resolves a raw value to a boolean, failing closed on an invalid
    ///     value.
    /// </summary>
    /// <param name="raw">The value to resolve.</param>
    /// <returns><see langword="true" /> only when the flag is explicitly enabled.</returns>
    public static bool IsEnabled(string? raw) => Classify(raw) is ExperimentalFlagDecision.Enabled;

    /// <summary>
    ///     Classifies and resolves a raw value in one call.
    /// </summary>
    /// <param name="raw">The value to resolve.</param>
    /// <param name="enabled">
    ///     The resolved boolean; <see langword="false" /> when the flag is off or
    ///     the value is invalid.
    /// </param>
    /// <returns>
    ///     <see langword="false" /> only for an unrecognised value, so a caller
    ///     can report a bad configuration without treating it as a valid deny.
    /// </returns>
    public static bool TryParse(string? raw, out bool enabled)
    {
        ExperimentalFlagDecision decision = Classify(raw);
        enabled = decision is ExperimentalFlagDecision.Enabled;
        return decision is not ExperimentalFlagDecision.Invalid;
    }

    /// <summary>
    ///     Evaluate a configured experimental flag before a feature is enabled
    ///     (#3397). This is the gate <see cref="HarborConfig" /> calls: a
    ///     malformed name or an unrecognised value is a failure, so a typo
    ///     cannot silently turn on an unfinished feature. A blank or absent
    ///     value is a valid <see cref="ExperimentalFlagDecision.Disabled" />.
    /// </summary>
    /// <param name="flag">The flag name, e.g. <c>AGENT_MANAGER</c>.</param>
    /// <param name="raw">The raw switch value from configuration, or null when unset.</param>
    /// <returns>The decision, or a failure describing why the flag cannot be evaluated.</returns>
    public static Result<ExperimentalFlagDecision> Evaluate(string? flag, string? raw)
    {
        if (!IsValidFlagName(flag))
        {
            return Result.Failure<ExperimentalFlagDecision>(
                $"experimental.{flag}: flag name must be an uppercase identifier "
                + "(letters, digits and underscores, starting with a letter)");
        }

        ExperimentalFlagDecision decision = Classify(raw);
        return decision is ExperimentalFlagDecision.Invalid
            ? Result.Failure<ExperimentalFlagDecision>(
                $"experimental.{flag}: unrecognised value '{raw}'; "
                + "expected 1/true/yes/on or 0/false/no/off")
            : Result.Success(decision);
    }

    private static bool Matches(string value, string first, string second, string third, string fourth) =>
        string.Equals(value, first, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, second, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, third, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, fourth, StringComparison.OrdinalIgnoreCase);
}
