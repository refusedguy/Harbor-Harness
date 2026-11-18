// ShellIntegrationScript.cs — #2680 F157: the shell-integration init script a
// user sources from bash or zsh so a Harbor session can export its HARBOR_*
// environment into that shell. This is a pure generator: it never starts a
// process, never reads the ambient environment and never touches a user's
// shell startup files. It is the primitive the CLI can use when it grows an
// `init` subcommand.
using System.Text;

namespace Harbor.Tools.Builtin;

/// <summary>Shell dialect the init script is generated for (#2680 F157).</summary>
public enum ShellIntegrationShell
{
    /// <summary>POSIX bash.</summary>
    Bash,

    /// <summary>Z shell.</summary>
    Zsh
}

/// <summary>
///     Generates the shell snippet that exports Harbor's environment variables
///     (#2680 F157). Pure, stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         Only names with the <see cref="VariablePrefix" /> prefix and a valid
///         shell-identifier suffix are accepted. Rejecting anything else keeps
///         a generated line from escaping the <c>export</c> statement: a name
///         such as <c>HARBOR_X=1; rm -rf /</c> is not a variable and is refused.
///     </para>
///     <para>
///         Values are single-quoted with the <c>'\''</c> escape, which is
///         understood by both bash and zsh and preserves spaces, quotes,
///         backslashes, dollar signs and newlines literally. Export lines are
///         sorted by name and duplicates use the last supplied value, so the
///         same inputs always produce the same script.
///     </para>
/// </remarks>
public static class ShellIntegrationScript
{
    /// <summary>The only variable prefix the generator will export.</summary>
    public const string VariablePrefix = "HARBOR_";

    /// <summary>
    ///     Builds a shell init script for <paramref name="shell" /> from the
    ///     supplied variables.
    /// </summary>
    /// <param name="shell">Target shell dialect.</param>
    /// <param name="variables">Variable name/value pairs to export.</param>
    /// <returns>A snippet the user can source from their shell.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variables" /> is null.</exception>
    /// <exception cref="ArgumentException">A variable name is not a valid HARBOR_* identifier.</exception>
    public static string Generate(
        ShellIntegrationShell shell,
        IReadOnlyList<KeyValuePair<string, string>> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < variables.Count; i++)
        {
            KeyValuePair<string, string> variable = variables[i];
            if (!IsValidVariableName(variable.Key))
            {
                throw new ArgumentException(
                    $"'{variable.Key}' is not a valid {VariablePrefix}* shell variable name.",
                    nameof(variables));
            }

            values[variable.Key] = variable.Value ?? string.Empty;
        }

        var names = new List<string>(values.Keys);
        names.Sort(StringComparer.Ordinal);

        var script = new StringBuilder();
        script.Append("# Harbor shell integration (")
            .Append(shell == ShellIntegrationShell.Zsh ? "zsh" : "bash")
            .Append(")\n");

        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            script.Append("export ").Append(name).Append('=').Append(Quote(values[name])).Append(";\n");
        }

        return script.ToString();
    }

    /// <summary>
    ///     Tests whether <paramref name="name" /> is a HARBOR_* shell identifier
    ///     the generator may export.
    /// </summary>
    public static bool IsValidVariableName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith(VariablePrefix, StringComparison.Ordinal)) return false;

        int suffixStart = VariablePrefix.Length;
        if (name.Length == suffixStart) return false;
        if (!char.IsAsciiLetter(name[suffixStart]) && name[suffixStart] != '_') return false;

        for (int i = suffixStart + 1; i < name.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(name[i]) && name[i] != '_') return false;
        }

        return true;
    }

    /// <summary>
    ///     Wraps <paramref name="value" /> in POSIX single quotes, escaping any
    ///     embedded single quote.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
