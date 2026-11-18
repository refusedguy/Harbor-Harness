using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;

namespace Harbor.Abstractions.Agents;

/// <summary>
///     The one place where the builtin agents' permission rules are assembled.
/// </summary>
/// <remarks>
///     <para>
///         Before #2552 each read-only factory wrote out its own
///         <c>PermissionRule[]</c>. Adding one read tool meant editing six
///         copies, and a missed copy was a permission hole (#798) rather than a
///         compile error. The factories now declare only the axes on which they
///         differ; every repeated row lives here.
///     </para>
///     <para>
///         <see cref="PermissionRuleset.Default" /> is deliberately not built
///         here. It is the full-access <c>code</c> policy and the baseline the
///         read-only invariants compare against, so it stays a separate
///         declaration.
///     </para>
///     <para>
///         Internal on purpose: the public surface is still the seven
///         <see cref="AgentDefinition" /> factories, and their rulesets keep the
///         same rows in the same order.
///     </para>
/// </remarks>
internal sealed class PermissionRulesetBuilder
{
    private readonly List<PermissionRule> _rules = [];

    /// <summary>
    ///     Adds the five read rows every constrained agent carries.
    /// </summary>
    /// <param name="withGoal">
    ///     Whether to include <c>goal</c>. The goal verdict is a report, not a
    ///     workspace mutation, so only agents that can report on a run carry it
    ///     (#1761).
    /// </param>
    internal PermissionRulesetBuilder WithReadOnlyTools(bool withGoal = false)
    {
        Add(WellKnownTools.Read, PermissionAction.Allow);
        // #1748: spill_read reads the agent's own tool output back. It writes
        // nothing, and without it a spilled result is a dead end for every
        // agent but `code`.
        Add(WellKnownTools.SpillRead, PermissionAction.Allow);
        if (withGoal)
        {
            Add(WellKnownTools.Goal, PermissionAction.Allow);
        }
        Add(WellKnownTools.Glob, PermissionAction.Allow);
        Add(WellKnownTools.Grep, PermissionAction.Allow);
        Add(WellKnownTools.Ls, PermissionAction.Allow);
        return this;
    }

    /// <summary>
    ///     Allows extra read-class tools, for example <c>tree</c> or <c>webfetch</c>.
    /// </summary>
    internal PermissionRulesetBuilder WithAllowedTools(params string[] tools)
    {
        foreach (string tool in tools)
        {
            Add(tool, PermissionAction.Allow);
        }
        return this;
    }

    /// <summary>
    ///     Denies a tool explicitly, for example <c>session_steer</c> on the
    ///     orchestrator, whose direct-write posture is already Deny.
    /// </summary>
    internal PermissionRulesetBuilder WithDeniedTools(params string[] tools)
    {
        foreach (string tool in tools)
        {
            Add(tool, PermissionAction.Deny);
        }
        return this;
    }

    /// <summary>
    ///     Adds the shell verbs shared by every constrained agent.
    /// </summary>
    internal PermissionRulesetBuilder WithBasicBash() =>
        WithBashVerbs("ls *", "cat *");

    /// <summary>
    ///     Adds the three read-only git verbs <see cref="PermissionRuleset.Default" />
    ///     itself allows. The open-ended <c>git *</c> is deliberately absent:
    ///     it authorized every mutating git subcommand in #798.
    /// </summary>
    internal PermissionRulesetBuilder WithGitBash() =>
        WithBashVerbs("git status", "git diff *", "git log *");

    /// <summary>
    ///     Adds the three content-search verbs used by the read modes.
    /// </summary>
    internal PermissionRulesetBuilder WithSearchBash() =>
        WithBashVerbs("grep *", "rg *", "find *");

    /// <summary>
    ///     The explorer's narrower search set. It has no <c>grep</c> verb.
    /// </summary>
    internal PermissionRulesetBuilder WithExplorerBash() =>
        WithBashVerbs("find *", "rg *");

    /// <summary>
    ///     Adds the build and test verbs the debug agent runs to confirm a fix.
    /// </summary>
    internal PermissionRulesetBuilder WithDiagnosticBash() =>
        WithBashVerbs("dotnet test *", "dotnet build *", "npm test *", "npm run build *");

    /// <summary>
    ///     Allows the given literal shell patterns. A trailing <c>*</c> is
    ///     open-ended, so patterns are written out here rather than derived.
    /// </summary>
    internal PermissionRulesetBuilder WithBashVerbs(params string[] patterns)
    {
        foreach (string pattern in patterns)
        {
            Add(WellKnownTools.Bash, pattern, PermissionAction.Allow);
        }
        return this;
    }

    /// <summary>
    ///     Adds the explicit destructive-command denies that precede the
    ///     catch-all shell rule.
    /// </summary>
    internal PermissionRulesetBuilder WithBashDenies(params string[] patterns)
    {
        foreach (string pattern in patterns)
        {
            Add(WellKnownTools.Bash, pattern, PermissionAction.Deny);
        }
        return this;
    }

    /// <summary>
    ///     Sets the catch-all verdict for shell commands that matched no
    ///     allow-row: Deny for the read-only agents, Ask for debug.
    /// </summary>
    internal PermissionRulesetBuilder WithBashFallback(PermissionAction action)
    {
        Add(WellKnownTools.Bash, "*", action);
        return this;
    }

    /// <summary>
    ///     Sets the posture for both write-class tools, <c>edit</c> and
    ///     <c>write</c>. The rule names both the tool and its category, so it
    ///     also covers <c>patch</c>, <c>notebook</c> and the other write tools.
    /// </summary>
    internal PermissionRulesetBuilder WithWritePosture(PermissionAction action)
    {
        Add(WellKnownTools.Edit, "*", action);
        Add(WellKnownTools.Write, "*", action);
        return this;
    }

    /// <summary>
    ///     Sets the posture for <c>plugin_manager</c>. It is separate from the
    ///     write posture because debug allows it: the tool asks the user about
    ///     each change it proposes, which matches debug's approval-gated writes.
    ///     The read-only agents deny it (#1738), since allowing it would hand a
    ///     design-only agent a way to start a change.
    /// </summary>
    internal PermissionRulesetBuilder WithPluginManager(PermissionAction action)
    {
        Add(WellKnownTools.PluginManager, "*", action);
        return this;
    }

    /// <summary>
    ///     Materializes the ruleset. Order is preserved; the ruleset sorts by
    ///     its own specificity rules in the constructor.
    /// </summary>
    internal PermissionRuleset Build() => new(_rules);

    private PermissionRulesetBuilder Add(string tool, PermissionAction action) =>
        Add(tool, "*", action);

    private PermissionRulesetBuilder Add(string tool, string pattern, PermissionAction action)
    {
        _rules.Add(new PermissionRule(tool, pattern, action));
        return this;
    }
}
