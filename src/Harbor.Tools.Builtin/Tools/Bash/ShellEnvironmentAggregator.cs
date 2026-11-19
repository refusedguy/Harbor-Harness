// ShellEnvironmentAggregator.cs: #2680 F092. The pure decision layer for the
// shell.env hook: fold ordered environment contributions plus the explicit
// per-call overrides into one bounded map that BashTool can hand to
// ProcessSpec. No process launch, no I/O, no clock and no product wiring
// lives here, so the slice stays AOT-safe and golden-neutral.

namespace Harbor.Tools.Builtin;

/// <summary>
///     One ordered contribution to a shell invocation's environment (#2680 F092).
///     Contributions are folded in list order, so a later source wins over an
///     earlier one without either source having to know about the other.
/// </summary>
/// <param name="Source">Diagnostic name of the contributor (a plugin, a scope, a config layer).</param>
/// <param name="Variables">Variable name/value pairs this contributor wants to inject.</param>
public sealed record ShellEnvironmentContribution(
    string Source,
    IReadOnlyDictionary<string, string> Variables);

/// <summary>
///     Pure, AOT-safe fold of shell environment contributions (#2680 F092).
///     Invalid input fails closed instead of leaking a malformed name into a
///     child process. Stateless and thread-safe.
/// </summary>
public static class ShellEnvironmentAggregator
{
    /// <summary>Maximum number of ordered contributions in one fold.</summary>
    public const int MaxSources = 64;

    /// <summary>Maximum number of variables a single source may contribute.</summary>
    public const int MaxVariablesPerSource = 256;

    /// <summary>Maximum length of a variable name.</summary>
    public const int MaxNameLength = 256;

    /// <summary>Maximum length of a variable value.</summary>
    public const int MaxValueLength = 8192;

    /// <summary>Source name used when validating the explicit per-call overrides.</summary>
    public const string CallSource = "call";

    /// <summary>
    ///     Fold <paramref name="contributions" /> in order, then apply
    ///     <paramref name="callOverrides" /> on top. The result is a detached
    ///     dictionary with ordinal key comparison, ready to be assigned to
    ///     <c>ProcessSpec.Environment</c>; the caller still owns the process.
    /// </summary>
    /// <param name="contributions">Ordered sources; <see langword="null" /> means no injected environment.</param>
    /// <param name="callOverrides">Explicit per-call variables; these win over every contribution.</param>
    /// <returns>The merged map, or a failure describing the first invalid source, name or value.</returns>
    public static Result<IReadOnlyDictionary<string, string>> Aggregate(
        IReadOnlyList<ShellEnvironmentContribution>? contributions,
        IReadOnlyDictionary<string, string>? callOverrides = null)
    {
        if (contributions is { Count: > MaxSources })
            return Failure($"Too many shell environment contributions (max {MaxSources}).");

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (contributions is not null)
        {
            for (int i = 0; i < contributions.Count; i++)
            {
                ShellEnvironmentContribution? contribution = contributions[i];
                if (contribution is null || string.IsNullOrWhiteSpace(contribution.Source))
                    return Failure($"Contribution at index {i} has no source name.");
                if (contribution.Variables is null)
                    return Failure($"Contribution '{contribution.Source}' has no variables.");
                if (contribution.Variables.Count > MaxVariablesPerSource)
                    return Failure($"Contribution '{contribution.Source}' exceeds {MaxVariablesPerSource} variables.");

                string? error = Fold(contribution.Source, contribution.Variables, merged);
                if (error is not null)
                    return Failure(error);
            }
        }

        if (callOverrides is not null)
        {
            if (callOverrides.Count > MaxVariablesPerSource)
                return Failure($"Contribution '{CallSource}' exceeds {MaxVariablesPerSource} variables.");
            string? error = Fold(CallSource, callOverrides, merged);
            if (error is not null)
                return Failure(error);
        }

        return Result.Success<IReadOnlyDictionary<string, string>>(merged);
    }

    /// <summary>
    ///     Whether a name is a POSIX shell identifier: an ASCII letter or
    ///     underscore first, then ASCII letters, digits or underscores. Culture
    ///     independent, so the same name is accepted under every locale.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return false;

        char first = name[0];
        if (first != '_' && !char.IsAsciiLetter(first))
            return false;

        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (c != '_' && !char.IsAsciiLetterOrDigit(c))
                return false;
        }

        return true;
    }

    private static string? Fold(
        string source,
        IReadOnlyDictionary<string, string> variables,
        Dictionary<string, string> merged)
    {
        foreach ((string name, string value) in variables)
        {
            if (!IsValidName(name))
                return $"Contribution '{source}' has an invalid variable name.";

            if (value is null || value.Length > MaxValueLength)
                return $"Contribution '{source}' has an invalid value for '{name}'.";

            merged[name] = value;
        }

        return null;
    }

    private static Result<IReadOnlyDictionary<string, string>> Failure(string message) =>
        Result.Failure<IReadOnlyDictionary<string, string>>(message);
}
