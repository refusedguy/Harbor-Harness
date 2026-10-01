// ReadOnlyAgentShellAllowRule.cs — the guard for the hole #798 did not report.
//
// WHAT #798 REPORTED, AND WHAT IS ACTUALLY THERE
// ----------------------------------------------
// #798 filed the read-only agents' rulesets as a policy decision and named one
// hole: "`patch` under `plan` resolves to `Ask`, not `Deny`." PR #817 verified
// that claim false — `patch` declares `ToolCategory.Write`, `PlanDefault`
// carries `new("write", "*", Deny)`, and the third disjunct of
// `PermissionRule.MatchesPermission` (`ToolCategories.CategoryMatches`) makes
// that single rule cover the whole Write class. Measured: Deny. The guard for
// that landed as `ReadOnlyAgentPermissionRule` and is green.
//
// The hole that was NOT reported is one row above it. `PlanDefault` carried
//
//     new("bash", "git *", PermissionAction.Allow)
//
// and a trailing `*` in a bash rule pattern is OPEN-ENDED.
// `BashArgMatcher.IsAllowedByPrefixRule` reads a final `*` as "allow any
// further arguments", so `git *` matches `git push --force`. Measured against
// the evaluator, that one rule resolved 16 of 16 mutating git subcommands to
// `Allow` with no prompt — commit, push, push --force, reset --hard,
// checkout ., checkout -b, clean -fdx, merge, rebase, stash drop, apply,
// config, remote add, submodule deinit, update-index.
//
// WHY THE EXISTING GUARD COULD NOT SEE IT
// ----------------------------------------
// `ReadOnlyAgentPermissionRule` grades the TOOL axis: every tool declaring
// `ToolCategory.Write` must be denied. `bash` declares `ToolCategory.Exec`, so
// it is correctly outside that set — and the guard is right to leave it there.
// The write capability does not arrive through a tool name; it arrives through
// a tool ARGUMENT. Any per-tool rule is blind to it by construction, and that
// is the gap this file covers: the SHELL axis, where one tool name carries many
// verbs and the verbs differ in whether they mutate.
//
// WHY NOT A LIST OF MUTATING GIT SUBCOMMANDS
// ------------------------------------------
// The tempting rule is "deny `git commit`, `git push`, `git reset --hard`, ...".
// That is a hand-maintained list, and it is wrong twice over. It rots — git
// gains verbs, and a list nobody updates is the defect #595 and #557 are about.
// And it is not derivable: nothing in the product says which git verbs mutate,
// so the list would encode an author's opinion while looking like a fact.
//
// So the invariant is stated as a RELATION between two rulesets that already
// exist. `PermissionRuleset.Default` is the full-access `code` agent's policy,
// and it is the repository's own answer to "which shell commands may run
// unprompted": it enumerates three git verbs (`git status`, `git diff *`,
// `git log *`) and opens nothing else. So:
//
//     A read-only agent may not Allow a bash pattern that `Default` does not
//     also Allow.
//
// This needs no written roster of verbs and no new vocabulary. It says the
// thing that was actually violated — the read-only agent was allowed strictly
// MORE shell than the agent that can already write files — and it stays honest
// as either side changes: a new shell verb added to `Default` is automatically
// permitted in the read-only agents, and one added to a read-only agent alone is
// reported. Both sets are read out of the product.
//
// It fired on exactly one rule, `("bash", "git *")`, and it is green after the
// fix, which inherited `Default`'s three git verbs rather than inventing any.
//
// NON-VACUITY
// -----------
// A subset rule that compares two sets is trivially satisfiable by two empty
// sets, so four tests close that:
//
//   * `The_Comparison_Baseline_Is_The_Full_Access_Agents_Own_Ruleset` — the
//     right-hand side is `PermissionRuleset.Default` and is non-empty, so a
//     broken derivation cannot quietly reduce the baseline to nothing and make
//     every subset trivially hold.
//   * `The_Derived_Agent_And_Rule_Sets_Are_Found_By_Declaration` — the
//     read-only agents and the bash allow-rules are discovered, not listed.
//   * `The_Rejected_Rule_Is_Reported_Not_Swallowed` — a planted
//     `("bash", "git *")` on top of the REAL `PlanDefault` is reported by name,
//     so the rule is shown to fire on the shape it exists to catch and not to
//     pass because it finds nothing.
//   * `AMutatating_Git_Verb_Is_Denied_And_Its_Read_Only_Sibling_Is_Not` — the
//     behavioural restatement, so a future change to the pattern semantics
//     (making `git *` literal, say) cannot leave the rule green while the hole
//     reopens underneath it.

using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Permissions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #798: a read-only agent must not <see cref="PermissionAction.Allow" /> a shell
///     pattern the full-access <c>code</c> agent is not itself allowed unprompted.
/// </summary>
public sealed class ReadOnlyAgentShellAllowRule
{
    /// <summary>
    ///     The builtin agents, read out of the three factories. Named here because an agent is a
    ///     policy, not a derivation — the same reasoning <c>ToolNameListRule.PolicyExemptions</c>
    ///     gives for keeping per-agent rules written down.
    /// </summary>
    private static readonly AgentDefinition[] BuiltinAgents =
    [
        AgentDefinition.CodeDefault("probe-model", "probe-provider"),
        AgentDefinition.PlanDefault("probe-model", "probe-provider"),
        AgentDefinition.ExploreDefault("probe-model", "probe-provider"),
    ];

    /// <summary>
    ///     An agent is read-only when its own description says so — derived from the prose, which
    ///     is the declaration of intent the rule grades against, so a fourth read-only agent is
    ///     covered the day it is written. Same derivation as <c>ReadOnlyAgentPermissionRule</c>;
    ///     two files rather than one because they grade different axes (tool vs shell verb) and
    ///     this one is new.
    /// </summary>
    private static bool DeclaresItselfReadOnly(AgentDefinition agent)
    {
        string description = agent.Description;
        return description.Contains("read-only", StringComparison.OrdinalIgnoreCase)
               || description.Contains("cannot modify files", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The agents the rule governs, sorted for a stable failure message.</summary>
    private static IReadOnlyList<AgentDefinition> ReadOnlyAgents() =>
        [.. BuiltinAgents.Where(DeclaresItselfReadOnly).OrderBy(a => a.Name.Value, StringComparer.Ordinal)];

    /// <summary>
    ///     The bash allow-rules an agent holds, as <c>"tool" "pattern"</c> strings, sorted.
    /// </summary>
    /// <remarks>
    ///     Flattened to a string rather than kept as a tuple so the rule, its failure message and
    ///     its assertions all speak one shape — the offending rule is <em>named</em> in the report,
    ///     and a name is what a reader has to grep for.
    /// </remarks>
    private static IReadOnlyList<string> BashAllowRules(PermissionRuleset ruleset) =>
    [
        .. ruleset.Rules
            .Where(rule => rule.Action == PermissionAction.Allow
                           && rule.Permission.Equals("bash", StringComparison.OrdinalIgnoreCase))
            .Select(rule => "\"" + rule.Permission + "\" \"" + rule.Pattern + "\"")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>
    ///     The bash allow-rules a read-only agent holds that <see cref="PermissionRuleset.Default" />
    ///     does not, sorted for a stable failure message.
    /// </summary>
    private static IReadOnlyList<string> RulesBeyondFullAccess(PermissionRuleset readOnly)
    {
        var baseline = BashAllowRules(PermissionRuleset.Default).ToHashSet(StringComparer.Ordinal);

        return [.. BashAllowRules(readOnly).Where(rule => !baseline.Contains(rule))];
    }

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     THE RULE. A read-only agent may not allow a bash pattern the full-access <c>code</c>
    ///     agent does not also allow.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the assertion #798's own question needed and did not have. "Is
    ///         <c>bash git *</c> intended?" is unanswerable from the ruleset alone, because
    ///         <c>git *</c> reads like an enumeration and behaves like a wildcard. Stating the
    ///         relation to <c>Default</c> answers it without anyone having to re-derive which git
    ///         verbs mutate: <c>Default</c> already says, in the product, that three are read-only
    ///         and the rest are not, and the read-only agent may not exceed it.
    ///     </para>
    ///     <para>
    ///         Both sides come out of the product, so nothing here is a roster to maintain. If a
    ///         shell verb is added to <c>Default</c> the read-only agents inherit it silently; if
    ///         one is added to a read-only agent alone, this fails and names the rule.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task A_Read_Only_Agent_Does_Not_Allow_A_Shell_Pattern_The_Code_Agent_Does_Not()
    {
        var offenders = new List<string>();
        foreach (AgentDefinition agent in ReadOnlyAgents())
        {
            foreach (string rule in RulesBeyondFullAccess(agent.Permission))
            {
                offenders.Add($"{agent.Name.Value} allows {rule}, which "
                              + $"{nameof(PermissionRuleset)}.{nameof(PermissionRuleset.Default)} does not");
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("a read-only agent that reaches a shell verb the full-access code agent is not "
                   + "itself allowed unprompted is more permissive than the agent that can already "
                   + "write files — which is not a read-only policy, it is an inversion of one. The "
                   + "shape this exists for is a trailing '*', which BashArgMatcher treats as "
                   + "open-ended: (\"bash\", \"git *\") reads as an allow-list of git verbs and "
                   + "matches `git push --force`. Fix by inheriting the verb from Default rather "
                   + "than by widening this rule. Offenders: " + string.Join(" | ", offenders));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The right-hand side of the subset is <see cref="PermissionRuleset.Default" /> and it is
    ///     NOT empty. A subset rule against an empty baseline holds for every ruleset, including
    ///     a maximally permissive one, so a derivation that silently emptied the baseline would
    ///     turn the rule above into a permanent green.
    /// </summary>
    [Test]
    public async Task The_Comparison_Baseline_Is_The_Full_Access_Agents_Own_Ruleset()
    {
        IReadOnlyList<string> baseline = BashAllowRules(PermissionRuleset.Default);

        await Assert.That(baseline.Count).IsGreaterThan(0)
            .Because("PermissionRuleset.Default is the code agent's own policy and allows several "
                   + "read-only shell verbs. If reading it produced nothing, the rule above would "
                   + "compare every agent against an empty set and pass unconditionally — the "
                   + "failure mode that makes a subset rule worthless");

        await Assert.That(baseline.Any(rule => rule.Contains("git", StringComparison.Ordinal)))
            .IsTrue()
            .Because("Default enumerates read-only git verbs and this is the axis the hole lived "
                   + "on. If the baseline stopped carrying any git pattern the rule would no longer "
                   + "be able to see the shape it was written for");
    }

    /// <summary>
    ///     The agents and the bash allow-rules are DISCOVERED. A guard over an empty set is
    ///     indistinguishable from a guard that works.
    /// </summary>
    [Test]
    public async Task The_Derived_Agent_And_Rule_Sets_Are_Found_By_Declaration()
    {
        string[] agents = [.. ReadOnlyAgents().Select(a => a.Name.Value)];

        await Assert.That(agents).IsEquivalentTo(new[] { "explore", "plan" })
            .Because("`plan` and `explore` both declare themselves read-only in their own "
                   + "Description and `code` does not. If this came back empty the rule above "
                   + "would loop over nothing and be vacuously green");

        foreach (AgentDefinition agent in ReadOnlyAgents())
        {
            await Assert.That(BashAllowRules(agent.Permission).Count).IsGreaterThan(0)
                .Because($"{agent.Name.Value} allows bash rules today; a read-only agent with no "
                       + "bash allow-rule at all would make the comparison trivially empty and the "
                       + "rule would stop being able to see the axis it grades");
        }
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The rule must REPORT the row it exists to catch. A planted
    ///     <c>("bash", "git *")</c> on top of the real <c>PlanDefault</c> — the exact historical
    ///     rule — must be named in the failure set, so the rule is shown to fire on that shape
    ///     rather than to pass because it finds nothing.
    /// </summary>
    [Test]
    public async Task The_Rejected_Rule_Is_Reported_Not_Swallowed()
    {
        AgentDefinition plan = AgentDefinition.PlanDefault("probe-model", "probe-provider");

        await Assert.That(RulesBeyondFullAccess(plan.Permission)).IsEmpty()
            .Because("the shipped PlanDefault inherits Default's git vocabulary and so holds "
                   + "nothing beyond it; this is the state the rule is asserting");

        // The historical rule, restored verbatim, must be reported — by name.
        var withHistoricalRule = new PermissionRuleset(
        [
            .. plan.Permission.Rules,
            new PermissionRule("bash", "git *", PermissionAction.Allow),
        ]);

        IReadOnlyList<string> offenders = RulesBeyondFullAccess(withHistoricalRule);

        await Assert.That(offenders).IsEquivalentTo(new[] { "\"bash\" \"git *\"" })
            .Because("`new(\"bash\", \"git *\", Allow)` is the rule this whole file exists to catch: "
                   + "a trailing '*' that reads as an enumeration and matches `git push --force`. "
                   + "The rule must name it, or it is not reporting the defect it was written for");
    }

    /// <summary>
    ///     THE BEHAVIOURAL RESTATEMENT. Whatever the pattern semantics do, the verdict for a
    ///     mutating git verb under a read-only agent must not be <see cref="PermissionAction.Allow" />,
    ///     and a read-only sibling must still be allowed. This is what stops the subset rule from
    ///     being green for the wrong reason if <c>MatchesPattern</c> or the prefix matcher ever
    ///     changes underneath it.
    /// </summary>
    [Test]
    public async Task AMutating_Git_Verb_Is_Not_Allowed_And_Its_Read_Only_Sibling_Still_Is()
    {
        AgentDefinition plan = AgentDefinition.PlanDefault("probe-model", "probe-provider");

        string[] mutating =
        [
            "git commit -m x",
            "git push origin main",
            "git push --force",
            "git reset --hard HEAD~5",
            "git checkout .",
            "git clean -fdx",
            "git rebase main",
            "git apply /tmp/evil.diff",
            "git remote add evil https://example.invalid/x.git",
        ];

        var allowed = new List<string>();
        foreach (string command in mutating)
        {
            if (plan.Permission.Evaluate("bash", command) == PermissionAction.Allow)
            {
                allowed.Add(command);
            }
        }

        await Assert.That(allowed).IsEmpty()
            .Because("`PlanDefault` documents itself as \"Read-only planning agent. Cannot modify "
                   + "files.\" Every command here either writes a commit, publishes one, or destroys "
                   + "work in the tree. An Allow verdict is not a narrower read-only policy, it is "
                   + "no policy at all — and it reaches a real shell, because ToolDispatcher "
                   + "resolves the called tool against the full registry rather than the agent's "
                   + "filtered list. Allowed anyway: " + string.Join(" | ", allowed));

        await Assert.That(plan.Permission.Evaluate("bash", "git status")).IsEqualTo(PermissionAction.Allow)
            .Because("the fix must narrow the rule, not delete it: a planner still needs to read "
                   + "the branch state, and `git status` is one of the three verbs Default itself "
                   + "allows unprompted. If this fails the read-only agent has lost a capability "
                   + "the full-access agent still has, which is the opposite defect");

        await Assert.That(plan.Permission.Evaluate("bash", "git log --oneline")).IsEqualTo(PermissionAction.Allow)
            .Because("`git log *` is the second of Default's three enumerated read-only git verbs, "
                   + "and reading history is most of what a planning agent does");
    }
}
