// #2675 item 041: tool disable with glob patterns and a source prefix.
//
// Amp's `amp.tools.disable` is an array of globs; `builtin:foo` disables the
// builtin tool while leaving an MCP tool of the same name alone. Harbor has no
// JSON setting for it yet, so the same decision is taken here from one process
// variable at the composition root. The policy is BCL-only, total (a malformed
// spec never throws) and a no-op when the variable is unset. The shipped tool
// set is byte-for-byte the one this code did not touch.

using Harbor.Abstractions.Tools;

namespace Harbor.Hosting;

/// <summary>Which family a registered tool belongs to, for source-scoped disable patterns.</summary>
internal enum ToolSourceKind
{
    /// <summary>A tool compiled into Harbor or contributed by a plugin.</summary>
    Builtin,

    /// <summary>A tool discovered from an MCP server (its name carries the <c>mcp_</c> prefix).</summary>
    Mcp,
}

/// <summary>One parsed disable pattern: an optional source scope plus a glob over the tool name.</summary>
/// <param name="Source">When set, the pattern applies only to that source family.</param>
/// <param name="Pattern">Glob over the tool name: <c>*</c> matches any run, <c>?</c> one character.</param>
internal sealed record ToolDisableRule(ToolSourceKind? Source, string Pattern)
{
    /// <summary>True when this rule is allowed to consider a tool from <paramref name="source"/>.</summary>
    public bool AppliesTo(ToolSourceKind source) => Source is null || Source == source;

    /// <summary>Case-insensitive glob match against a tool name.</summary>
    public bool MatchesName(string name) => ToolNameGlob.Matches(Pattern, name);
}

/// <summary>
///     Two-wildcard glob matcher (<c>*</c> and <c>?</c>), case-insensitive.
///     BCL-only and allocation-free: the composition root parses the spec once
///     and each tool is tested once, so a regex would buy nothing and add an
///     AOT-trimmed code path to the startup of every verb.
/// </summary>
internal static class ToolNameGlob
{
    /// <summary>Match <paramref name="pattern"/> against <paramref name="value"/>.</summary>
    public static bool Matches(string pattern, string value)
    {
        int p = 0;
        int v = 0;
        int star = -1;
        int starValue = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?'
                || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starValue = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++starValue;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

/// <summary>
///     Parsed set of tool-disable patterns and the one place they are applied.
/// </summary>
/// <remarks>
///     The variable is a comma-separated list. Each entry is an optional source
///     prefix (<c>builtin:</c> or <c>mcp:</c>) followed by a glob on the tool
///     name, e.g. <c>HARBOR_DISABLE_TOOLS=builtin:web_fetch,mcp:server_*</c>.
///     An unknown prefix is treated as part of the pattern rather than an error:
///     a typo must not take the whole host down, and a tool name is a free-form
///     string that may legitimately contain a colon.
/// </remarks>
internal sealed class ToolDisablePolicy
{
    /// <summary>Harbor spelling of the variable.</summary>
    internal const string EnvVar = "HARBOR_DISABLE_TOOLS";

    /// <summary>Amp-compatible alias, matching the other #2675 env-var slices.</summary>
    internal const string AmpEnvVar = "AMP_TOOLS_DISABLE";

    private static readonly ToolDisablePolicy NoRules = new([]);

    private readonly ToolDisableRule[] _rules;

    private ToolDisablePolicy(ToolDisableRule[] rules) => _rules = rules;

    /// <summary>Read the policy from the process environment.</summary>
    public static ToolDisablePolicy FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvVar) ?? Environment.GetEnvironmentVariable(AmpEnvVar));

    /// <summary>Parse a spec; null, blank or a spec of empty entries yields a no-op policy.</summary>
    public static ToolDisablePolicy Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return NoRules;
        }

        var rules = new List<ToolDisableRule>();
        foreach (string raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ToolSourceKind? source = null;
            string pattern = raw;
            int colon = raw.IndexOf(':');
            if (colon > 0)
            {
                switch (raw[..colon].ToLowerInvariant())
                {
                    case "builtin":
                        source = ToolSourceKind.Builtin;
                        break;
                    case "mcp":
                        source = ToolSourceKind.Mcp;
                        break;
                }

                if (source is not null)
                {
                    pattern = raw[(colon + 1)..];
                }
            }

            if (pattern.Length == 0)
            {
                continue;
            }

            rules.Add(new ToolDisableRule(source, pattern));
        }

        return rules.Count == 0 ? NoRules : new ToolDisablePolicy(rules.ToArray());
    }

    /// <summary>Number of parsed patterns, for diagnostics and tests.</summary>
    public int Count => _rules.Length;

    /// <summary>Classify a registered tool by its name, the only fact available at the registry.</summary>
    public static ToolSourceKind SourceOf(string toolName) =>
        toolName.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase)
            ? ToolSourceKind.Mcp
            : ToolSourceKind.Builtin;

    /// <summary>True when at least one rule disables this tool.</summary>
    public bool IsDisabled(string toolName, ToolSourceKind source)
    {
        foreach (ToolDisableRule rule in _rules)
        {
            if (rule.AppliesTo(source) && rule.MatchesName(toolName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Remove every disabled tool from <paramref name="registry"/> before it
    ///     is frozen. Returns the number removed; an empty policy never touches
    ///     the registry.
    /// </summary>
    public int Apply(IToolRegistry registry)
    {
        if (_rules.Length == 0)
        {
            return 0;
        }

        int removed = 0;
        foreach (ToolDescriptor descriptor in registry.GetAllTools())
        {
            if (!IsDisabled(descriptor.Name.Value, SourceOf(descriptor.Name.Value)))
            {
                continue;
            }

            if (registry.Unregister(descriptor.Name).IsSuccess)
            {
                removed++;
            }
        }

        return removed;
    }
}
