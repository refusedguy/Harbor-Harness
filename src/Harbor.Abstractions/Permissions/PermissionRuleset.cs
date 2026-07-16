using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Permissions;

/// <summary>
/// Permission ruleset for an agent or session.
/// Implements Specification pattern (GOF).
/// </summary>
/// <remarks>
/// <para>
/// A ruleset is an ordered list of <see cref="PermissionRule"/> entries. Evaluation walks
/// the rules from most-specific to least-specific pattern, returning the action of the
/// first matching rule. When no rule matches, the evaluator falls back to
/// <see cref="PermissionAction.Ask"/>.
/// </para>
/// <para>
/// Builtin rulesets: <see cref="Empty"/> (matches nothing, asks for everything) and
/// <see cref="Default"/> (safe defaults: read-only tools allow, writes outside <c>src/</c>
/// ask, <c>rm -rf /</c> and <c>sudo</c> always deny).
/// </para>
/// <para>
/// Instances are immutable; <see cref="Merge"/> returns a new ruleset. Thread-safe for
/// concurrent reads.
/// </para>
/// </remarks>
public sealed record PermissionRuleset
{
    private readonly List<PermissionRule> _rules;

    /// <summary>
    /// Construct a ruleset from an enumeration of rules.
    /// </summary>
    /// <param name="rules">The rules to include. Order is preserved; evaluation reorders by specificity.</param>
    public PermissionRuleset(IEnumerable<PermissionRule> rules)
    {
        _rules = rules.ToList();
    }

    /// <summary>
    /// The rules in this ruleset, in their original insertion order.
    /// </summary>
    public IReadOnlyList<PermissionRule> Rules => _rules;

    /// <summary>
    /// An empty ruleset — no rules, every action falls through to <see cref="PermissionAction.Ask"/>.
    /// </summary>
    public static PermissionRuleset Empty => new(Array.Empty<PermissionRule>());

    /// <summary>
    /// The default safe ruleset for the <c>code</c> agent.
    /// </summary>
    public static PermissionRuleset Default => new(new PermissionRule[]
    {
        new("read", "*", PermissionAction.Allow),
        new("glob", "*", PermissionAction.Allow),
        new("grep", "*", PermissionAction.Allow),
        new("ls", "*", PermissionAction.Allow),
        new("write", "src/*", PermissionAction.Allow),
        new("write", "*", PermissionAction.Ask),
        new("edit", "src/*", PermissionAction.Allow),
        new("edit", "*.env", PermissionAction.Deny),
        new("edit", "*.env.*", PermissionAction.Deny),
        new("edit", "*", PermissionAction.Ask),
        new("bash", "ls *", PermissionAction.Allow),
        new("bash", "cat *", PermissionAction.Allow),
        new("bash", "grep *", PermissionAction.Allow),
        new("bash", "rg *", PermissionAction.Allow),
        new("bash", "find *", PermissionAction.Allow),
        new("bash", "git status", PermissionAction.Allow),
        new("bash", "git diff *", PermissionAction.Allow),
        new("bash", "git log *", PermissionAction.Allow),
        new("bash", "rm -rf /", PermissionAction.Deny),
        new("bash", "sudo *", PermissionAction.Deny),
        new("bash", "*", PermissionAction.Ask),
        new("webfetch", "*", PermissionAction.Ask),
        new("task", "*", PermissionAction.Allow),
    });

    /// <summary>
    /// Returns a new ruleset that merges <paramref name="other"/> into this one. User-supplied
    /// rules take precedence (last wins on duplicate <c>Permission:Pattern</c> keys).
    /// </summary>
    /// <param name="other">The ruleset to merge in.</param>
    /// <returns>A new merged <see cref="PermissionRuleset"/>.</returns>
    public PermissionRuleset Merge(PermissionRuleset other)
    {
        // User-supplied rules take precedence (last wins)
        var merged = new Dictionary<string, PermissionRule>();
        foreach (var rule in _rules.Concat(other._rules))
        {
            merged[$"{rule.Permission}:{rule.Pattern}"] = rule;
        }

        return new PermissionRuleset(merged.Values);
    }

    /// <summary>
    /// Evaluate the ruleset for a given permission/tool and argument path.
    /// </summary>
    /// <param name="permission">The permission name (typically the tool name, e.g. <c>read</c>, <c>bash</c>).</param>
    /// <param name="argPath">The argument path (e.g. file path for <c>read</c>, command string for <c>bash</c>).</param>
    /// <returns>The action to take; <see cref="PermissionAction.Ask"/> if no rule matches.</returns>
    public PermissionAction Evaluate(string permission, string argPath)
    {
        // Most specific first
        foreach (var rule in _rules
                     .OrderByDescending(r => PatternSpecificity(r.Pattern))
                     .ThenByDescending(r => r.Action == PermissionAction.Deny ? 1 : 0))
        {
            if (!rule.MatchesPermission(permission)) continue;
            if (!rule.MatchesPattern(argPath)) continue;
            return rule.Action;
        }

        return PermissionAction.Ask;  // default: ask user
    }

    private static int PatternSpecificity(string pattern)
    {
        if (pattern == "*") return 0;
        var stars = pattern.Count(c => c == '*');
        return pattern.Length - stars * 2;
    }
}

/// <summary>
/// Single permission rule.
/// </summary>
/// <param name="Permission">The permission/tool name (or <c>"*"</c> to match all).</param>
/// <param name="Pattern">A glob pattern matching the argument path. <c>*</c> matches any sequence, <c>?</c> matches one char.</param>
/// <param name="Action">The action to take when this rule matches.</param>
public sealed record PermissionRule(
    string Permission,
    string Pattern,
    PermissionAction Action)
{
    /// <summary>
    /// Returns <see langword="true"/> if this rule applies to the given permission name
    /// (case-insensitive) or if <see cref="Permission"/> is <c>"*"</c>.
    /// </summary>
    /// <param name="permission">The permission name to test.</param>
    public bool MatchesPermission(string permission) =>
        Permission == "*" || Permission.Equals(permission, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> if <see cref="Pattern"/> matches the given argument path.
    /// </summary>
    /// <param name="argPath">The argument path (file path, command string, etc.).</param>
    public bool MatchesPattern(string argPath)
    {
        // Simple glob: * matches any sequence
        if (Pattern == "*") return true;
        return GlobMatch(Pattern, argPath);
    }

    private static bool GlobMatch(string pattern, string input)
    {
        // Convert glob to regex: * -> .*, ? -> .
        var regexPattern = "^" +
            System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// Permission action returned by <see cref="PermissionRuleset.Evaluate"/>.
/// </summary>
public enum PermissionAction
{
    /// <summary>Allow the action without prompting the user.</summary>
    Allow,

    /// <summary>Prompt the user for a one-off or persistent decision.</summary>
    Ask,

    /// <summary>Reject the action; the tool call returns an error.</summary>
    Deny,
}

/// <summary>
/// A request for a permission decision, surfaced to the user via <see cref="IPermissionService.AskUserAsync"/>.
/// </summary>
/// <param name="Permission">The permission/tool name being requested.</param>
/// <param name="Pattern">The matched pattern (for display only).</param>
/// <param name="Args">The raw JSON arguments of the tool call.</param>
/// <param name="AlwaysOptions">Persistent-decision options the user may pick (e.g. <c>"always-allow"</c>).</param>
public sealed record PermissionRequest(
    string Permission,
    string Pattern,
    JsonElement Args,
    IReadOnlyList<string> AlwaysOptions);

/// <summary>
/// The user's response to a <see cref="PermissionRequest"/>.
/// </summary>
/// <param name="Action">The action to apply.</param>
/// <param name="PersistDecision">When <see langword="true"/>, the decision is written back to the agent's ruleset.</param>
public sealed record PermissionResponse(
    PermissionAction Action,
    bool PersistDecision);

/// <summary>
/// Service for permission checks.
/// </summary>
/// <remarks>
/// <para>
/// The permission service is the single authority that decides whether a tool call may
/// proceed. It combines the agent's static <see cref="PermissionRuleset"/> with an optional
/// interactive <c>userAsker</c> callback for <see cref="PermissionAction.Ask"/> decisions.
/// </para>
/// <para>
/// Implementations MUST be thread-safe. The default <c>PermissionService</c> lives in
/// <c>Harbor.Core</c>.
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    /// Check whether a tool call should be allowed, asked, or denied.
    /// </summary>
    /// <param name="agentName">The agent requesting the action (used to look up its ruleset).</param>
    /// <param name="toolName">The tool name (e.g. <c>read</c>, <c>bash</c>).</param>
    /// <param name="args">The raw JSON arguments of the tool call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="PermissionResponse"/>, or failure if the agent is not registered.</returns>
    Task<Result<PermissionResponse>> CheckAsync(
        string agentName,
        string toolName,
        JsonElement args,
        CancellationToken ct = default);

    /// <summary>
    /// Prompt the user for a permission decision. Falls back to <see cref="PermissionAction.Deny"/>
    /// when no UI is configured.
    /// </summary>
    /// <param name="request">The permission request to surface.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user's <see cref="PermissionResponse"/>.</returns>
    Task<Result<PermissionResponse>> AskUserAsync(
        PermissionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Return the ruleset currently bound to the named agent, or <see cref="PermissionRuleset.Empty"/>
    /// if the agent is not registered.
    /// </summary>
    /// <param name="agentName">The agent name to look up.</param>
    /// <returns>The agent's ruleset.</returns>
    PermissionRuleset GetRuleset(string agentName);
}
