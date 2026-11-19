namespace Harbor.Abstractions.Permissions;

/// <summary>
///     Session-wide permission mode. The mode contributes an automatic decision
///     only where the static ruleset has asked; it never weakens an explicit
///     <see cref="PermissionAction.Deny" /> from the ruleset or from a session
///     decision (issue #2665, item 1).
/// </summary>
/// <remarks>
///     The four values are ordered from least to most permissive so a mode can
///     be compared and persisted as its integer value. The mode is deliberately
///     a contract type with no dependency on configuration, storage or TUI:
///     each of those consumers binds it in its own slice.
/// </remarks>
public enum PermissionMode
{
    /// <summary>
    ///     No automatic allowance: every action the ruleset asks about stays a
    ///     question for the user.
    /// </summary>
    Supervised = 0,

    /// <summary>
    ///     Automatically allow write-class tools only. Command/execution tools
    ///     are never auto-allowed by this mode.
    /// </summary>
    AutoAcceptEdits = 1,

    /// <summary>
    ///     Automatically allow inspection, write, network, MCP and interaction
    ///     tools. Command/execution tools are still never auto-allowed.
    /// </summary>
    Auto = 2,

    /// <summary>
    ///     Automatically allow every tool, including command/execution tools.
    ///     This is an explicit opt-in; the ruleset's Deny rules still win.
    /// </summary>
    FullAccess = 3
}

/// <summary>
///     In-memory permission overlay that lives until the end of a session
///     (issue #2665, item 2). It records the user's "always for this session"
///     decisions and applies the session's <see cref="PermissionMode" /> on top
///     of a static <see cref="PermissionRuleset" />.
/// </summary>
/// <remarks>
///     <para>
///         The overlay is process-local and is never written to disk. Ending the
///         session calls <see cref="EndSession" />, which drops every recorded
///         decision and restores <see cref="PermissionMode.Supervised" />. A
///         session that dies without that call simply loses the overlay with
///         the process, so no allowance outlives its session.
///     </para>
///     <para>
///         Decision order in <see cref="Evaluate" /> is fail-closed: a session
///         Deny and a ruleset Deny both win over any Allow, a ruleset Allow is
///         honoured, a session Allow is honoured only after that, and the mode's
///         automatic allowance is the last resort. Execution-class tools
///         (<see cref="ToolCategory.Exec" />) are never auto-allowed by
///         <see cref="PermissionMode.AutoAcceptEdits" /> or
///         <see cref="PermissionMode.Auto" />; an explicit session decision or
///         <see cref="PermissionMode.FullAccess" /> is required for them.
///     </para>
///     <para>
///         Instances are thread-safe. Reads snapshot the decision list under a
///         short lock so evaluation does not hold the lock while matching globs.
///     </para>
/// </remarks>
public sealed class SessionPermissionOverlay
{
    private readonly object _gate = new();
    private readonly List<PermissionRule> _sessionRules = new();
    private PermissionMode _mode;

    /// <summary>
    ///     Create an overlay with the given mode. Defaults to
    ///     <see cref="PermissionMode.Supervised" />, which adds no allowance.
    /// </summary>
    public SessionPermissionOverlay(PermissionMode mode = PermissionMode.Supervised)
    {
        _mode = mode;
    }

    /// <summary>
    ///     The mode applied on top of the ruleset. Changing it does not discard
    ///     decisions already recorded for this session.
    /// </summary>
    public PermissionMode Mode
    {
        get { lock (_gate) return _mode; }
        set { lock (_gate) _mode = value; }
    }

    /// <summary>
    ///     Allow a tool/argument pattern for the rest of this session. The
    ///     decision is an explicit user choice and may authorise a command
    ///     tool; it never overrides a Deny.
    /// </summary>
    public void AllowForSession(string permission, string pattern)
        => Record(PermissionAction.Allow, permission, pattern);

    /// <summary>
    ///     Deny a tool/argument pattern for the rest of this session. A session
    ///     Deny wins over a ruleset Allow.
    /// </summary>
    public void DenyForSession(string permission, string pattern)
        => Record(PermissionAction.Deny, permission, pattern);

    /// <summary>
    ///     End the session: forget every recorded decision and reset the mode
    ///     to <see cref="PermissionMode.Supervised" />. Idempotent.
    /// </summary>
    public void EndSession()
    {
        lock (_gate)
        {
            _sessionRules.Clear();
            _mode = PermissionMode.Supervised;
        }
    }

    /// <summary>
    ///     Resolve the effective action for a tool call by combining the static
    ///     ruleset, the session decisions and the mode.
    /// </summary>
    /// <param name="baseRuleset">The agent's static ruleset. Never null.</param>
    /// <param name="permission">The tool name (or a category name).</param>
    /// <param name="argPath">The argument path or command string.</param>
    /// <returns>The action to take.</returns>
    public PermissionAction Evaluate(PermissionRuleset baseRuleset, string permission, string argPath)
    {
        ArgumentNullException.ThrowIfNull(baseRuleset);
        ArgumentException.ThrowIfNullOrEmpty(permission);
        ArgumentException.ThrowIfNullOrEmpty(argPath);

        PermissionRule[] snapshot;
        PermissionMode mode;
        lock (_gate)
        {
            snapshot = _sessionRules.ToArray();
            mode = _mode;
        }

        // A session Deny is absolute.
        for (int i = 0; i < snapshot.Length; i++)
        {
            ref readonly var rule = ref snapshot[i];
            if (rule.Action == PermissionAction.Deny
                && rule.MatchesPermission(permission)
                && rule.MatchesPattern(argPath))
            {
                return PermissionAction.Deny;
            }
        }

        // The static ruleset's decision is authoritative when it is not Ask:
        // a ruleset Deny cannot be undone by the mode or a session Allow.
        PermissionAction baseline = baseRuleset.Evaluate(permission, argPath);
        if (baseline != PermissionAction.Ask) return baseline;

        // An explicit session Allow comes next.
        for (int i = 0; i < snapshot.Length; i++)
        {
            ref readonly var rule = ref snapshot[i];
            if (rule.Action == PermissionAction.Allow
                && rule.MatchesPermission(permission)
                && rule.MatchesPattern(argPath))
            {
                return PermissionAction.Allow;
            }
        }

        return AutoAllow(mode, permission);
    }

    private void Record(PermissionAction action, string permission, string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(permission);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        lock (_gate)
        {
            _sessionRules.Add(new PermissionRule(permission, pattern, action));
        }
    }

    /// <summary>
    ///     The mode's automatic allowance for a call the ruleset left at Ask.
    ///     Execution tools are excluded from every mode except
    ///     <see cref="PermissionMode.FullAccess" />, which is an explicit opt-in.
    ///     Unknown (plugin) tools belong to no category and are never
    ///     auto-allowed, so a new tool cannot gain authority by being unnamed.
    /// </summary>
    private static PermissionAction AutoAllow(PermissionMode mode, string permission)
    {
        switch (mode)
        {
            case PermissionMode.FullAccess:
                return PermissionAction.Allow;
            case PermissionMode.Supervised:
                return PermissionAction.Ask;
            case PermissionMode.AutoAcceptEdits:
            case PermissionMode.Auto:
                if (!ToolCategories.TryClassify(permission, out ToolCategory category)) return PermissionAction.Ask;
                if (category == ToolCategory.Exec) return PermissionAction.Ask;
                if (mode == PermissionMode.AutoAcceptEdits)
                    return category == ToolCategory.Write ? PermissionAction.Allow : PermissionAction.Ask;
                return PermissionAction.Allow;
            default:
                return PermissionAction.Ask;
        }
    }
}
