using System.Collections.Frozen;
namespace Harbor.Abstractions.Permissions;
/// <summary>
///     Strategy consulted by <see cref="PermissionRuleset.Evaluate" /> for tool-specific
///     argument-shape safety (OCP: a new path-like or exec-style tool adds an implementation
///     instead of editing the contract).
/// </summary>
/// <remarks>
///     <para>
///         The builtin strategies are <see cref="BashSafetyPolicy" /> (destructive-command
///         deny, argv-derived deny targets, token-wise allow) and
///         <see cref="PathGuardSafetyPolicy" /> (traversal/absolute-path guard for
///         path-like tools). A ruleset carries its list like
///         <c>ProviderConfig.Quirks</c> — see
///         <see cref="PermissionRuleset.DefaultSafetyPolicies" />.
///     </para>
///     <para>
///         All members must be pure and thread-safe. <see cref="PermissionRuleset.Evaluate" />
///         is a hot path, so keep implementations allocation-light; per-call argv parsing
///         is hoisted by the caller (pre-walk + deny targets run once, only
///         <see cref="SuppressAllow" /> runs per Allow rule).
///     </para>
/// </remarks>
public interface IArgSafetyPolicy
{
    /// <summary>
    ///     Returns <see langword="true" /> when this policy applies to the given
    ///     permission/tool name (case-insensitive).
    /// </summary>
    /// <param name="permission">The permission name (typically the tool name).</param>
    bool AppliesTo(string permission);

    /// <summary>
    ///     Pre-walk short-circuit evaluated before any rule walk (e.g. destructive bash
    ///     commands deny regardless of rule spelling). Returns <see langword="null" />
    ///     to continue with rule evaluation.
    /// </summary>
    /// <param name="argPath">The raw argument (file path, command string, etc.).</param>
    PermissionAction? PreEvaluate(string argPath);

    /// <summary>
    ///     Extra argument strings a <see cref="PermissionAction.Deny" /> rule is tested
    ///     against in addition to the raw argument (e.g. argv[0] / basename /
    ///     normalized-command forms of a bash command). Returns <see langword="null" />
    ///     or an empty list when there are none.
    /// </summary>
    /// <param name="argPath">The raw argument (file path, command string, etc.).</param>
    IReadOnlyList<string>? GetExtraDenyTargets(string argPath);

    /// <summary>
    ///     Returns <see langword="true" /> when an <see cref="PermissionAction.Allow" />
    ///     rule with the given pattern must be skipped for this argument (the decision
    ///     then falls through to Ask or a matching Deny).
    /// </summary>
    /// <param name="rulePattern">The Allow rule's glob pattern.</param>
    /// <param name="argPath">The raw argument (file path, command string, etc.).</param>
    bool SuppressAllow(string rulePattern, string argPath);
}

/// <summary>
///     Builtin <see cref="IArgSafetyPolicy" /> for the <c>bash</c> tool.
/// </summary>
/// <remarks>
///     Stateless singleton — safe to share across rulesets and threads.
/// </remarks>
public sealed class BashSafetyPolicy : IArgSafetyPolicy
{
    /// <summary>Shared stateless instance used by <see cref="PermissionRuleset.DefaultSafetyPolicies" />.</summary>
    public static readonly BashSafetyPolicy Instance = new();

    /// <inheritdoc />
    public bool AppliesTo(string permission) =>
        permission.Equals("bash", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public PermissionAction? PreEvaluate(string argPath) =>
        BashArgMatcher.IsDestructiveCommand(argPath) ? PermissionAction.Deny : null;

    /// <inheritdoc />
    public IReadOnlyList<string>? GetExtraDenyTargets(string argPath) =>
        BashArgMatcher.GetDenyMatchTargets(argPath);

    /// <inheritdoc />
    public bool SuppressAllow(string rulePattern, string argPath) =>
        // IsAllowedByPrefixRule already returns false for metacharacter-bearing
        // commands, so a single call covers both the metacharacter escalation
        // and the token-wise prefix check.
        !BashArgMatcher.IsAllowedByPrefixRule(rulePattern, argPath);
}

/// <summary>
///     Builtin <see cref="IArgSafetyPolicy" /> for tools whose primary argument is a
///     workspace-relative file path: glob Allow patterns must not silently authorize
///     traversal escapes (<c>src/*</c> matching <c>src/../../../etc/passwd</c>), and
///     absolute paths carry no workspace-relative meaning, so non-literal Allow rules
///     are skipped and the decision falls through to Ask (or an earlier matching Deny).
///     Only an exact literal rule pattern (no glob metacharacters) may still allow such
///     an argument — it can only match that one string.
/// </summary>
/// <remarks>
///     Stateless (the tool set is fixed at construction) — safe to share across
///     rulesets and threads. Construct with a custom tool set to guard a new
///     path-like tool without editing the contract.
/// </remarks>
public sealed class PathGuardSafetyPolicy : IArgSafetyPolicy
{
    private static readonly FrozenSet<string> DefaultTools = new[]
    {
        "read", "write", "edit", "ls", "glob", "grep", "tree", "ripgrep", "notebook", "patch", "mcp"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shared stateless instance (default tool set) used by <see cref="PermissionRuleset.DefaultSafetyPolicies" />.</summary>
    public static readonly PathGuardSafetyPolicy Instance = new();

    private readonly FrozenSet<string> _tools;

    /// <summary>Construct a guard for the default path-like tool set.</summary>
    public PathGuardSafetyPolicy()
        : this(DefaultTools)
    {
    }

    /// <summary>Construct a guard for a custom path-like tool set (case-insensitive).</summary>
    /// <param name="tools">Tool names to guard.</param>
    public PathGuardSafetyPolicy(IEnumerable<string> tools)
    {
        _tools = tools.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool AppliesTo(string permission) => _tools.Contains(permission);

    /// <inheritdoc />
    public PermissionAction? PreEvaluate(string argPath) => null;

    /// <inheritdoc />
    public IReadOnlyList<string>? GetExtraDenyTargets(string argPath) => null;

    /// <inheritdoc />
    public bool SuppressAllow(string rulePattern, string argPath) =>
        HasUnsafePathShape(argPath) && !IsLiteralPattern(rulePattern);

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="argPath" /> either contains a
    ///     <c>..</c> segment (traversal escape, on <c>/</c> or <c>\</c> separators) or is rooted
    ///     (absolute), i.e. has no safe workspace-relative meaning for glob rule matching.
    /// </summary>
    private static bool HasUnsafePathShape(string argPath)
    {
        if (argPath.Length == 0) return false;
        if (argPath[0] == '/' || argPath[0] == '\\' || Path.IsPathRooted(argPath)) return true;

        int segmentStart = 0;
        for (int i = 0; i <= argPath.Length; i++)
        {
            if (i < argPath.Length && argPath[i] != '/' && argPath[i] != '\\') continue;
            int len = i - segmentStart;
            if (len == 2 && argPath[segmentStart] == '.' && argPath[segmentStart + 1] == '.') return true;
            segmentStart = i + 1;
        }

        return false;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when the glob pattern contains no <c>*</c>/<c>?</c>
    ///     metacharacters, meaning it can only ever match its literal subject and therefore
    ///     cannot over-match an unsafe path shape.
    /// </summary>
    private static bool IsLiteralPattern(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' || pattern[i] == '?') return false;
        }
        return true;
    }
}
