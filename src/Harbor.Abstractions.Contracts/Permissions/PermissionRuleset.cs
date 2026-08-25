using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
namespace Harbor.Abstractions.Permissions;
/// <summary>
///     Permission ruleset for an agent or session.
///     Implements Specification pattern (GOF).
/// </summary>
/// <remarks>
///     <para>
///         A ruleset is an ordered list of <see cref="PermissionRule" /> entries. Evaluation walks
///         the rules from most-specific to least-specific pattern, returning the action of the
///         first matching rule. When no rule matches, the evaluator falls back to
///         <see cref="PermissionAction.Ask" />.
///     </para>
///     <para>
///         Builtin rulesets: <see cref="Empty" /> (matches nothing, asks for everything) and
///         <see cref="Default" /> (safe defaults: read-only tools allow, writes outside <c>src/</c>
///         ask, <c>rm -rf /</c> and <c>sudo</c> always deny).
///     </para>
///     <para>
///         Instances are immutable; <see cref="Merge" /> returns a new ruleset. Thread-safe for
///         concurrent reads.
///     </para>
///     <para>
///         Performance: rules are pre-sorted by specificity (descending) and Deny-first at
///         construction time so <see cref="Evaluate" /> can iterate the array directly without
///         re-sorting on every call. Glob patterns are compiled to <see cref="Regex" /> once and
///         cached in a process-wide cache keyed by pattern string.
///     </para>
/// </remarks>
public sealed record PermissionRuleset
{
    /// <summary>
    ///     Pre-sorted array of rules: most-specific first, Deny before Allow before Ask on ties.
    ///     Materialized once in the constructor; <see cref="Evaluate" /> iterates this directly
    ///     (no per-call LINQ OrderByDescending allocation).
    /// </summary>
    private readonly PermissionRule[] _sortedRules;

    /// <summary>
    ///     Construct a ruleset from an enumeration of rules.
    /// </summary>
    /// <param name="rules">The rules to include. Order is preserved; evaluation reorders by specificity.</param>
    public PermissionRuleset(IEnumerable<PermissionRule> rules)
    {
        // Materialize once, then sort in-place by (specificity desc, Deny-first).
        // We deliberately avoid LINQ here so construction is allocation-light.
        var source = rules as ICollection<PermissionRule> ?? new List<PermissionRule>(rules);
        var arr = new PermissionRule[source.Count];
        int i = 0;
        foreach (var r in source)
        {
            arr[i++] = r;
        }

        Array.Sort(arr, static (a, b) =>
        {
            int sa = PatternSpecificity(a.Pattern);
            int sb = PatternSpecificity(b.Pattern);
            if (sa != sb) return sb.CompareTo(sa); // higher specificity first
            // On ties: Deny (1) before Ask (0) before Allow (-1)
            int da = DenyRank(a.Action);
            int db = DenyRank(b.Action);
            return db.CompareTo(da);
        });

        _sortedRules = arr;
    }

    /// <summary>
    ///     The rules in this ruleset, pre-sorted by specificity (most-specific first, Deny-first
    ///     on ties). The returned list is a defensive copy so callers cannot mutate the cached sort.
    /// </summary>
    public IReadOnlyList<PermissionRule> Rules
    {
        get
        {
            var copy = new PermissionRule[_sortedRules.Length];
            Array.Copy(_sortedRules, copy, _sortedRules.Length);
            return copy;
        }
    }

    /// <summary>
    ///     An empty ruleset — no rules, every action falls through to <see cref="PermissionAction.Ask" />.
    /// </summary>
    public static PermissionRuleset Empty => new(Array.Empty<PermissionRule>());

    /// <summary>
    ///     The default safe ruleset for the <c>code</c> agent.
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
        new("tree", "*", PermissionAction.Allow),
        new("ripgrep", "*", PermissionAction.Allow),
        new("notebook", "*", PermissionAction.Allow),
        new("mcp", "*", PermissionAction.Ask),
        new("patch", "src/*", PermissionAction.Allow),
        new("patch", "*", PermissionAction.Ask),
        new("task", "*", PermissionAction.Allow)
    });

    /// <summary>
    ///     Returns a new ruleset that merges <paramref name="other" /> into this one. User-supplied
    ///     rules take precedence (last wins on duplicate <c>Permission:Pattern</c> keys).
    /// </summary>
    /// <param name="other">The ruleset to merge in.</param>
    /// <returns>A new merged <see cref="PermissionRuleset" />.</returns>
    public PermissionRuleset Merge(PermissionRuleset other)
    {
        // Pre-size the dictionary for the upper bound (no resizes).
        int capacity = _sortedRules.Length + other._sortedRules.Length;
        if (capacity == 0) return Empty;

        var merged = new Dictionary<string, PermissionRule>(capacity, StringComparer.Ordinal);
        foreach (var rule in _sortedRules)
        {
            merged[KeyFor(rule)] = rule;
        }
        foreach (var rule in other._sortedRules)
        {
            merged[KeyFor(rule)] = rule;
        }

        var values = new PermissionRule[merged.Count];
        merged.Values.CopyTo(values, 0);
        return new PermissionRuleset(values);
    }

    /// <summary>
    ///     Evaluate the ruleset for a given permission/tool and argument path.
    ///     Hot path: iterates the pre-sorted rules array directly (no LINQ allocation).
    /// </summary>
    /// <param name="permission">The permission name (typically the tool name, e.g. <c>read</c>, <c>bash</c>).</param>
    /// <param name="argPath">The argument path (e.g. file path for <c>read</c>, command string for <c>bash</c>).</param>
    /// <returns>The action to take; <see cref="PermissionAction.Ask" /> if no rule matches.</returns>
    /// <remarks>
    ///     <para>
    ///         For the <c>bash</c> tool (A2): commands identified as destructive by
    ///         <see cref="BashArgMatcher.IsDestructiveCommand" /> are denied before any rule walk
    ///         — glob deny patterns cannot express flag-swapped or compound spellings such as
    ///         <c>rm -fr /</c> or <c>cd / &amp;&amp; rm -rf .</c>. Deny rules are then tested against
    ///         BOTH the raw command AND the argv-derived targets from
    ///         <see cref="BashArgMatcher.GetDenyMatchTargets" /> for every bash command, so an
    ///         absolute invocation like <c>/usr/bin/sudo ls</c> hits a <c>sudo *</c> deny even
    ///         without shell metacharacters. Commands containing shell metacharacters
    ///         (<c>; | &amp; ` $(&lt; &gt;</c>, newlines) outside quotes are never matched by Allow
    ///         rules — the decision escalates to Ask so glob patterns like <c>cat *</c> cannot
    ///         silently authorize <c>cat f; rm -rf ~</c>.
    ///     </para>
    ///     <para>
    ///         For path-like tools (<see cref="PathGuardTools" />) whose argument contains a
    ///         <c>..</c> segment or is rooted (A1/A2): glob Allow patterns must not silently
    ///         authorize traversal escapes (<c>src/*</c> matching <c>src/../../../etc/passwd</c>)
    ///         and absolute paths carry no workspace-relative meaning inside a ruleset, so Allow
    ///         rules are skipped and the decision falls through to Ask (or an earlier matching
    ///         Deny). Only an exact literal rule pattern (no glob metacharacters) may still
    ///         allow such an argument — it can only match that one string, so it cannot
    ///         over-match.
    ///     </para>
    /// </remarks>
    public PermissionAction Evaluate(string permission, string argPath)
    {
        var rules = _sortedRules;
        bool isBash = permission.Equals("bash", StringComparison.OrdinalIgnoreCase);

        // A2: recursive-force deletions of dangerous targets are always denied, regardless
        // of how the flags are spelled or whether the rm sits in a compound command tail.
        if (isBash && BashArgMatcher.IsDestructiveCommand(argPath))
            return PermissionAction.Deny;

        bool bashMetachars = isBash && BashArgMatcher.HasShellMetacharacters(argPath);
        // A2: computed for EVERY bash command, not just metacharacter-bearing ones, so
        // Deny rules see argv[0] / basename / normalized-command targets of all segments.
        IReadOnlyList<string>? denyTargets = isBash ? BashArgMatcher.GetDenyMatchTargets(argPath) : null;
        bool pathGuard = PathGuardTools.Contains(permission) && HasUnsafePathShape(argPath);

        for (int i = 0; i < rules.Length; i++)
        {
            ref readonly var rule = ref rules[i];
            if (!rule.MatchesPermission(permission)) continue;

            if (denyTargets is not null)
            {
                if (rule.Action == PermissionAction.Deny
                    && (rule.MatchesPattern(argPath) || MatchesAnyTarget(denyTargets, rule)))
                {
                    return PermissionAction.Deny;
                }
            }

            if (rule.Action == PermissionAction.Allow)
            {
                if (bashMetachars) continue; // Allow must not match; anything else falls through to Ask.
                if (pathGuard && !IsLiteralPattern(rule.Pattern)) continue;
                // C1 (sprint 6): bash Allow rules match TOKEN-wise, not
                // string-glob-wise — "git *" allows real git invocations only
                // (exact argv[0], case-sensitive), never "gitk" or "GIT push".
                if (isBash && !BashArgMatcher.IsAllowedByPrefixRule(rule.Pattern, argPath)) continue;
            }

            if (!rule.MatchesPattern(argPath)) continue;
            return rule.Action;
        }

        return PermissionAction.Ask; // default: ask user
    }

    /// <summary>
    ///     Tools whose primary argument is a workspace-relative file path and therefore get the
    ///     unsafe-path guard applied in <see cref="Evaluate" /> (A2).
    /// </summary>
    private static readonly FrozenSet<string> PathGuardTools = new[]
    {
        "read", "write", "edit", "ls", "glob", "grep", "tree", "ripgrep", "notebook", "patch", "mcp"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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

    private static bool MatchesAnyTarget(IReadOnlyList<string> targets, PermissionRule rule)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (rule.MatchesPattern(targets[i])) return true;
        }
        return false;
    }

    private static string KeyFor(PermissionRule rule) => rule.Permission + ":" + rule.Pattern;

    private static int PatternSpecificity(string pattern)
    {
        if (pattern == "*") return 0;
        // Manual star counter — avoids the LINQ `.Count(c => c == '*')` closure allocation.
        int stars = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*') stars++;
        }
        return pattern.Length - stars * 2;
    }

    private static int DenyRank(PermissionAction action) => action switch
    {
        PermissionAction.Deny => 2,
        PermissionAction.Ask => 1,
        _ => 0
    };
}

/// <summary>
///     Single permission rule.
/// </summary>
/// <param name="Permission">The permission/tool name (or <c>"*"</c> to match all).</param>
/// <param name="Pattern">
///     A glob pattern matching the argument path. <c>*</c> matches any sequence, <c>?</c> matches one
///     char.
/// </param>
/// <param name="Action">The action to take when this rule matches.</param>
public sealed record PermissionRule(
    string Permission,
    string Pattern,
    PermissionAction Action)
{
    /// <summary>
    ///     Process-wide cache of compiled glob regexes. Patterns are highly repeated across
    ///     rulesets (most agents share the same builtin patterns), so caching avoids re-compiling
    ///     the same regex on every <see cref="MatchesPattern" /> call.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    /// <summary>
    ///     Returns <see langword="true" /> if this rule applies to the given permission name
    ///     (case-insensitive), if <see cref="Permission" /> is <c>"*"</c>, or — since sprint 6 C2 —
    ///     if <see cref="Permission"/> names a <see cref="ToolCategory"/> that contains the tool
    ///     (e.g. permission <c>"exec"</c> matches the <c>bash</c> tool).
    /// </summary>
    /// <param name="permission">The permission name to test.</param>
    public bool MatchesPermission(string permission) =>
        Permission == "*"
        || Permission.Equals(permission, StringComparison.OrdinalIgnoreCase)
        || ToolCategories.CategoryMatches(Permission, permission);

    /// <summary>
    ///     Returns <see langword="true" /> if <see cref="Pattern" /> matches the given argument path.
    /// </summary>
    /// <param name="argPath">The argument path (file path, command string, etc.).</param>
    public bool MatchesPattern(string argPath)
    {
        // Fast path: wildcard matches everything.
        if (Pattern == "*") return true;
        return GetOrCompileRegex(Pattern).IsMatch(argPath);
    }

    private static Regex GetOrCompileRegex(string pattern)
    {
        // GetOrAdd factory is invoked only on cache miss; the closure allocation is
        // amortized across all subsequent matches of the same pattern.
        return RegexCache.GetOrAdd(pattern, static p =>
        {
            string regexPattern = "^" +
                                  Regex.Escape(p)
                                      .Replace("\\*", ".*")
                                      .Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        });
    }
}

/// <summary>
///     Permission action returned by <see cref="PermissionRuleset.Evaluate" />.
/// </summary>
public enum PermissionAction
{
    /// <summary>Allow the action without prompting the user.</summary>
    Allow,

    /// <summary>Prompt the user for a one-off or persistent decision.</summary>
    Ask,

    /// <summary>Reject the action; the tool call returns an error.</summary>
    Deny
}

/// <summary>
///     A request for a permission decision, surfaced to the user via <see cref="IPermissionService.AskUserAsync" />.
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
///     The user's response to a <see cref="PermissionRequest" />.
/// </summary>
/// <param name="Action">The action to apply.</param>
/// <param name="PersistDecision">When <see langword="true" />, the decision is written back to the agent's ruleset.</param>
public sealed record PermissionResponse(
    PermissionAction Action,
    bool PersistDecision);

/// <summary>
///     Service for permission checks.
/// </summary>
/// <remarks>
///     <para>
///         The permission service is the single authority that decides whether a tool call may
///         proceed. It combines the agent's static <see cref="PermissionRuleset" /> with an optional
///         interactive <c>userAsker</c> callback for <see cref="PermissionAction.Ask" /> decisions.
///     </para>
///     <para>
///         Implementations MUST be thread-safe. The default <c>PermissionService</c> lives in
///         <c>Harbor.Core</c>.
///     </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    ///     Check whether a tool call should be allowed, asked, or denied.
    /// </summary>
    /// <param name="agentName">The agent requesting the action (used to look up its ruleset).</param>
    /// <param name="toolName">The tool name (e.g. <c>read</c>, <c>bash</c>).</param>
    /// <param name="args">The raw JSON arguments of the tool call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="PermissionResponse" />, or failure if the agent is not registered.</returns>
    public Task<Result<PermissionResponse>> CheckAsync(
        string agentName,
        string toolName,
        JsonElement args,
        CancellationToken ct = default);

    /// <summary>
    ///     Prompt the user for a permission decision. Falls back to <see cref="PermissionAction.Deny" />
    ///     when no UI is configured.
    /// </summary>
    /// <param name="request">The permission request to surface.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user's <see cref="PermissionResponse" />.</returns>
    public Task<Result<PermissionResponse>> AskUserAsync(
        PermissionRequest request,
        CancellationToken ct = default);

    /// <summary>
    ///     Return the ruleset currently bound to the named agent, or <see cref="PermissionRuleset.Empty" />
    ///     if the agent is not registered.
    /// </summary>
    /// <param name="agentName">The agent name to look up.</param>
    /// <returns>The agent's ruleset.</returns>
    public PermissionRuleset GetRuleset(string agentName);
}
