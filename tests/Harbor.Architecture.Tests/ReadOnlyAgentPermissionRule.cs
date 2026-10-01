// ReadOnlyAgentPermissionRule.cs — the guard for issue #798.
//
// WHAT #798 REPORTED, AND WHAT IS ACTUALLY THERE
// ----------------------------------------------
// #798 is a careful, well-evidenced issue. Its central claim is verified in full
// and its conclusion is the opposite of what it predicted:
//
//   "`patch` under `plan` resolves to `Ask`, not `Deny`. ... neither sub-agent
//    copy mentions `patch` at all, so the walk falls through to the `:281`
//    default."
//
// The premise is right — `PlanDefault` and `ExploreDefault` really do not name
// `patch` — and the inference is wrong, because it read `Evaluate`'s rule loop
// without reading `PermissionRule.MatchesPermission`, which has THREE disjuncts:
//
//     Permission == "*"
//     || Permission.Equals(permission, OrdinalIgnoreCase)
//     || ToolCategories.CategoryMatches(Permission, permission)     ← this one
//
// The third is the load-bearing one. A rule's permission field may name a
// CATEGORY, not just a tool, and `patch` declares `ToolCategory.Write` in
// `BuiltinToolSafetyProfiles`. `PlanDefault` already carries
// `new("write", "*", Deny)` — a rule that is simultaneously the tool `write` and
// the class `Write`. The evaluator reaches it while walking for `patch`,
// `MatchesPattern("*")` matches, and the verdict is Deny.
//
// So the hole does not exist, and it has not existed since #724 (2026-09-29)
// made the classification derive from the declarations. The issue was measured
// at `e46e0048`, which is a DESCENDANT of that commit (checked with
// `git merge-base --is-ancestor 40d4a261 e46e0048`), so the category rule was
// already live at the moment of measurement. Nothing regressed; the claim was
// never true on the tree it was measured against.
//
// The sibling claim is wrong the same way, and for the same reason:
//
//   "`bash rg` / `bash find` / `bash grep` are `Ask` under `plan` even though
//    `Default:147-149` allows them."
//
// `PlanDefault` ends with `new("bash", "*", Deny)` — a hard deny, not an ask.
// `bash rg -n foo` matches no allow rule and matches that one, so the verdict is
// Deny. Carrying `Default`'s shape (which ends in `bash * Ask`) into the
// read-only rulesets is what produced the phantom. The REAL plan/explore
// difference is that `ExploreDefault` allows `bash find *` and `bash rg *` and
// `PlanDefault` does not — a policy difference between two agents with different
// jobs, written down in both rulesets, not a drift.
//
// WHY A GUARD ANYWAY
// ------------------
// The reason the issue was written is the reason this file is worth landing: the
// invariant "a read-only agent denies every write-class tool" is real, it is
// load-bearing for the prose ("Cannot modify files."), and NOTHING in the
// repository asserted it. `ToolNameListRule` grades these rulesets but
// deliberately lets them through as POLICY — it polices whether a per-tool fact
// is DERIVED, never whether a policy is correct. The invariant was carried
// entirely by one reader having to know that "write" is both a tool and a class.
//
// So the hole is not in the ruleset; it is in what the ruleset SAYS, and this
// file says it out loud and grades it.
//
// THE RULE
// --------
// For every builtin agent whose own description declares it read-only, and for
// every builtin tool whose declared category is `Write`, the verdict must be
// `Deny`. Both sets are read out of the product — the agents' own `Description`
// and `BuiltinToolSafetyProfiles` — so a fourth read-only agent or a fifth
// write-class tool is picked up without editing this file. A written roster would
// rot exactly the way the lists this file exists to protect have been accused of.
//
//   WHAT `Merge` CAN AND CANNOT DO — the other half of #798
// -------------------------------------------------------
// #798 asks whether `Default.Merge(deny-write-and-edit)` is the right
// composition, and concludes it is not. That conclusion is correct, and for a
// sharper reason than the one given. It is not only that `Merge` widens both
// read-only agents; it is that `Merge` CANNOT express the deny at all, and the
// failure is silent in the dangerous direction:
//
//     Default.Merge(new PermissionRuleset([new("patch", "*", Deny)]))
//
// keys on `permission:pattern`, so the merge overwrites `patch:*` (Ask) with
// `patch:*` (Deny) — and leaves `patch:src/*` (Allow) untouched, because that is
// a different key. `PatternSpecificity("src/*")` is 3 and `PatternSpecificity("*")`
// is 0, so the Allow is walked FIRST and matches `src/Program.cs`. The merged
// ruleset denies `patch` in `docs/` and ALLOWS it in `src/` — which is the
// opposite of a read-only policy, reached by a rule that reads like one.
//
// The same holds for `write` and `edit`, which is why this is a property of
// `Merge` and not of `patch`. Fixing it needs an operation that is not
// subtraction-by-key (a deny must be able to outrank a more specific allow), and
// feature freeze #555 forbids inventing one here. So it is CHARACTERISED below,
// not fixed: the test pins today's behaviour and names the hazard, so the next
// person to reach for `Default.Merge(deny)` finds the reason in a red-capable
// assertion instead of re-deriving it.
//
// NON-VACUITY
// -----------
// The main rule asserts a `Deny` that is, on the current tree, simply TRUE. A
// guard that is green because the evaluator returns Deny by default would be
// indistinguishable from one that works, so three things close that:
//
//   * `The_Deny_Verdict_Is_Earned_Not_The_Evaluators_Default` strips the
//     write-denying rules from the REAL `PlanDefault` and asserts the same call
//     then returns `Ask`. That is #798's exact claimed symptom, produced, in
//     three lines — so the green above is caused by the ruleset and nothing else.
//   * `The_Read_Only_Agents_And_Write_Tools_Are_Found_By_Declaration` asserts
//     both derived sets are non-empty and contain the agents/tools the rule is
//     about, so a broken derivation cannot quietly empty the loop above.
//   * the rule prints the offending agent, tool and verdict, so a green run and
//     a blind run are distinguishable in the log.

using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Permissions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #798: an agent that declares itself read-only must deny every tool
///     that declares itself write-class, and <see cref="PermissionRuleset.Merge" />
///     must not be mistaken for a way to say so.
/// </summary>
public sealed class ReadOnlyAgentPermissionRule
{
    /// <summary>
    ///     Paths the rule is asserted over. Both are relative and traversal-free on
    ///     purpose: an absolute or <c>..</c>-bearing argument is denied by
    ///     <c>PathGuardSafetyPolicy.PreEvaluate</c> before the rule walk, so a
    ///     Deny on such a path would be earned by the guard and not by the
    ///     ruleset — the rule would pass for the wrong reason.
    /// </summary>
    private static readonly string[] ProbePaths = ["src/Program.cs", "docs/notes.md"];

    /// <summary>
    ///     The builtin agents, read out of the three factories. Named here because
    ///     an agent is a policy, not a derivation — the same reasoning
    ///     <c>ToolNameListRule.PolicyExemptions</c> gives for keeping per-agent
    ///     rules written down.
    /// </summary>
    private static readonly AgentDefinition[] BuiltinAgents =
    [
        AgentDefinition.CodeDefault("probe-model", "probe-provider"),
        AgentDefinition.PlanDefault("probe-model", "probe-provider"),
        AgentDefinition.ExploreDefault("probe-model", "probe-provider"),
    ];

    /// <summary>
    ///     An agent is read-only when its own description says so. Derived, not
    ///     listed: the prose is the declaration of intent this rule grades against,
    ///     so a fourth read-only agent is covered the day it is written.
    /// </summary>
    private static bool DeclaresItselfReadOnly(AgentDefinition agent)
    {
        string description = agent.Description;
        return description.Contains("read-only", StringComparison.OrdinalIgnoreCase)
               || description.Contains("cannot modify files", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The agents the rule governs, sorted for a stable failure message.
    /// </summary>
    private static IReadOnlyList<AgentDefinition> ReadOnlyAgents() =>
        [.. BuiltinAgents.Where(DeclaresItselfReadOnly).OrderBy(a => a.Name.Value, StringComparer.Ordinal)];

    /// <summary>
    ///     The builtin tools that declare themselves write-class, sorted for a
    ///     stable failure message. Read off the declarations so a new write-class
    ///     tool re-points this rule instead of escaping it.
    /// </summary>
    private static IReadOnlyList<string> WriteClassTools() =>
    [
        .. BuiltinToolSafetyProfiles.All
            .Where(d => d.Category == ToolCategory.Write)
            .Select(d => d.ToolName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     THE RULE. Every write-class tool is Denied for every agent that declares
    ///     itself read-only, on every probe path.
    /// </summary>
    /// <remarks>
    ///     <c>patch</c> is the tool #798 reported, and it is covered by derivation
    ///     rather than by name: it is a <see cref="ToolCategory.Write" /> tool, so
    ///     the <c>new("write", "*", Deny)</c> rule both read-only agents already
    ///     carry reaches it through <c>PermissionRule.MatchesPermission</c>. Adding
    ///     a second write-class tool tomorrow grades the same way with no edit here.
    /// </remarks>
    [Test]
    public async Task Every_Read_Only_Agent_Denies_Every_Write_Class_Tool()
    {
        IReadOnlyList<AgentDefinition> agents = ReadOnlyAgents();
        IReadOnlyList<string> tools = WriteClassTools();

        var offenders = new List<string>();
        foreach (AgentDefinition agent in agents)
        {
            foreach (string tool in tools)
            {
                foreach (string path in ProbePaths)
                {
                    PermissionAction action = agent.Permission.Evaluate(tool, path);
                    if (action == PermissionAction.Deny)
                    {
                        continue;
                    }

                    offenders.Add($"{agent.Name.Value} / {tool} / {path} → {action}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("an agent whose own description says it cannot modify files must not reach a "
                   + "write-class tool on a verdict weaker than Deny. Ask is not a weaker read-only "
                   + "policy, it is a prompt: the user is asked about an answer the agent was "
                   + "already told it could not have. Offenders: " + string.Join(" | ", offenders));
    }

    // =====================================================================
    // 2. The `Merge` characterisation.
    // =====================================================================

    /// <summary>
    ///     `Merge` cannot express "forbid this tool". It is last-wins on
    ///     <c>permission:pattern</c>, so a wildcard deny overwrites the wildcard rule
    ///     and leaves every MORE SPECIFIC allow for the same tool standing — and
    ///     <see cref="PermissionRuleset" /> walks the more specific pattern first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is a characterisation, not an assertion of what should be. It is
    ///         green on the current tree and is meant to stay green: it records
    ///         today's semantics so the next reader of #798's
    ///         <c>Default.Merge(deny-write-and-edit)</c> proposal sees the reason it
    ///         cannot work, in a test, rather than re-deriving it from the sort order.
    ///     </para>
    ///     <para>
    ///         The fix needs an operation that lets a deny outrank a more specific
    ///         allow. That is a new operation over rules, which feature freeze #555
    ///         rules out here, so it is recorded in #798 instead of written.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Merge_Leaves_A_More_Specific_Allow_Standing_Under_A_Wildcard_Deny()
    {
        var denyPatch = new PermissionRuleset(
            [new PermissionRule("patch", "*", PermissionAction.Deny)]);

        PermissionRuleset merged = PermissionRuleset.Default.Merge(denyPatch);

        await Assert.That(merged.Evaluate("patch", "docs/notes.md")).IsEqualTo(PermissionAction.Deny)
            .Because("the wildcard key `patch:*` IS the one the merge overwrites, so outside src/ the "
                   + "deny does take effect — which is what makes the rule below look correct");

        await Assert.That(merged.Evaluate("patch", "src/Program.cs")).IsEqualTo(PermissionAction.Allow)
            .Because("`patch:src/*` (Allow) is a DIFFERENT key from `patch:*`, so Merge leaves it "
                   + "alone, and PatternSpecificity(\"src/*\")=3 beats PatternSpecificity(\"*\")=0 so "
                   + "the allow is walked first. A ruleset that reads as 'patch is denied' and "
                   + "permits patching anything under src/ is the failure mode #798's proposal "
                   + "would have shipped, and the same shape holds for write and edit");
    }

    // =====================================================================
    // 3. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     THE CONTROL. #798's symptom, reproduced. Remove the rules that deny
    ///     writes from the REAL <c>PlanDefault</c> and the same call that returns
    ///     Deny above returns Ask — so the Deny is earned by the ruleset under test
    ///     and not by the evaluator's fall-through.
    /// </summary>
    /// <remarks>
    ///     Without this, the main rule is a green assertion that a future change to
    ///     <c>Evaluate</c>'s default (return Deny instead of Ask, say) would keep
    ///     passing while the policy it claims to grade had quietly stopped existing.
    /// </remarks>
    [Test]
    public async Task The_Deny_Verdict_Is_Earned_Not_The_Evaluators_Default()
    {
        AgentDefinition plan = AgentDefinition.PlanDefault("probe-model", "probe-provider");

        await Assert.That(plan.Permission.Evaluate("patch", "src/Program.cs"))
            .IsEqualTo(PermissionAction.Deny)
            .Because("the claim this whole file exists to check: `patch` under `plan` is Deny, "
                   + "because `new(\"write\", \"*\", Deny)` names the Write CLASS as well as the "
                   + "write tool, and MatchesPermission's third disjunct matches `patch` against it");

        PermissionRule[] withoutWriteDenies =
        [
            .. plan.Permission.Rules.Where(rule =>
                rule.Action == PermissionAction.Deny
                && !rule.Permission.Equals("write", StringComparison.OrdinalIgnoreCase)
                && !ToolCategories.CategoryMatches(rule.Permission, "patch")),
        ];

        await Assert.That(withoutWriteDenies).IsNotEmpty()
            .Because("the control is only meaningful if the real ruleset actually carries rules "
                   + "this filter removes; if the filter removed nothing, the assertion below would "
                   + "be testing an untouched ruleset and would pass for the wrong reason");

        var weakened = new PermissionRuleset(withoutWriteDenies);

        await Assert.That(weakened.Evaluate("patch", "src/Program.cs")).IsEqualTo(PermissionAction.Ask)
            .Because("with the write-class deny gone, `patch` matches nothing and falls through to "
                   + "Evaluate's default of Ask. This is exactly the verdict #798 reported, which is "
                   + "how we know the Deny above comes from the ruleset and not from the evaluator");
    }

    /// <summary>
    ///     Both derived sets must be real, or the rule above is a loop over nothing.
    /// </summary>
    [Test]
    public async Task The_Read_Only_Agents_And_Write_Tools_Are_Found_By_Declaration()
    {
        string[] agents = [.. ReadOnlyAgents().Select(a => a.Name.Value)];
        IReadOnlyList<string> tools = WriteClassTools();

        await Assert.That(agents).IsEquivalentTo(new[] { "explore", "plan" })
            .Because("`plan` and `explore` both declare themselves read-only in their own "
                   + "Description, and `code` does not. If this set ever comes back empty the "
                   + "derivation is broken and the rule above is vacuously green");

        await Assert.That(tools).IsEquivalentTo(new[] { "edit", "notebook", "patch", "session_steer", "write" })
            .Because("these are the builtin tools declaring ToolCategory.Write, read off the "
                   + "declarations. `patch` is here because #798 is about it, and the rest are "
                   + "here because the same category rule covers them — the rule grades the class, "
                   + "not the one tool the issue happened to name");

        await Assert.That(ToolCategories.CategoryMatches("write", "patch")).IsTrue()
            .Because("this single fact is what makes the read-only rulesets deny `patch` without "
                   + "naming it. If it were false, the main rule would have gone red, and this "
                   + "assertion says which link in the chain broke");
    }
}
