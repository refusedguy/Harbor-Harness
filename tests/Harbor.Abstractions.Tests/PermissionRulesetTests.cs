using Harbor.Abstractions.Permissions;
namespace Harbor.Abstractions.Tests;
public class PermissionRulesetTests
{
    // #1770: the default-ruleset vectors used to be one copy-pasted method
    // each. The assert never changed — only the (tool, argPath, action) triple
    // did. Every row keeps its original test name as the case display name.
    [Test]
    [Arguments("read", "any-file.txt", PermissionAction.Allow, DisplayName = "Default_AllowsRead")]
    [Arguments("write", "/etc/passwd", PermissionAction.Ask, DisplayName = "Default_Asks_ForWrite_RootLevel")]
    [Arguments("edit", ".env", PermissionAction.Deny, DisplayName = "Default_Denies_EnvFiles")]
    [Arguments("bash", "rm -rf /", PermissionAction.Deny, DisplayName = "Default_Denies_RmRfRoot")]
    [Arguments("bash", "sudo rm file", PermissionAction.Deny, DisplayName = "Default_Denies_Sudo")]
    [Arguments("bash", "ls -la", PermissionAction.Allow, DisplayName = "Default_Allows_BashLs")]
    [Arguments("write", "src/Program.cs", PermissionAction.Allow, DisplayName = "Default_Allows_SrcWrite")]
    public async Task Default_Evaluate_MatchesTheExpectedAction(string tool, string argPath, PermissionAction expected)
    {
        var action = PermissionRuleset.Default.Evaluate(tool, argPath);
        await Assert.That(action).IsEqualTo(expected);
    }

    [Test]
    public async Task Merge_UserRulesOverride()
    {
        var userRules = new PermissionRuleset(new[]
        {
            new PermissionRule("write", "*", PermissionAction.Allow)
        });

        var merged = PermissionRuleset.Default.Merge(userRules);
        // Relative workspace path: user Allow override applies.
        var relative = merged.Evaluate("write", "src/Program.cs");
        await Assert.That(relative).IsEqualTo(PermissionAction.Allow);

        // A1 (security hardening): rooted paths have no workspace-relative meaning,
        // so glob Allow rules are skipped and the decision falls through to Ask.
        var absolute = merged.Evaluate("write", "/etc/passwd");
        await Assert.That(absolute).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task Evaluate_UnmatchedPermission_ReturnsAsk()
    {
        var ruleset = new PermissionRuleset(Array.Empty<PermissionRule>());
        var action = ruleset.Evaluate("custom_tool", "*");
        await Assert.That(action).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task Default_And_Empty_Are_Cached_Singletons()
    {
        await Assert.That(ReferenceEquals(PermissionRuleset.Default, PermissionRuleset.Default)).IsTrue();
        await Assert.That(ReferenceEquals(PermissionRuleset.Empty, PermissionRuleset.Empty)).IsTrue();
    }

    [Test]
    public async Task Identical_Rulesets_Are_Equal_With_Same_Hash()
    {
        var a = new PermissionRuleset(new[] { new PermissionRule("read", "*", PermissionAction.Allow) });
        var b = new PermissionRuleset(new[] { new PermissionRule("read", "*", PermissionAction.Allow) });
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(b.Equals(a)).IsTrue();
        await Assert.That(a == b).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task Rule_Order_Does_Not_Affect_Equality()
    {
        var a = new PermissionRuleset(new[]
        {
            new PermissionRule("read", "*", PermissionAction.Allow),
            new PermissionRule("edit", "*.env", PermissionAction.Deny)
        });
        var b = new PermissionRuleset(new[]
        {
            new PermissionRule("edit", "*.env", PermissionAction.Deny),
            new PermissionRule("read", "*", PermissionAction.Allow)
        });
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task Different_Rules_Are_Not_Equal()
    {
        var a = new PermissionRuleset(new[] { new PermissionRule("read", "*", PermissionAction.Allow) });
        var b = new PermissionRuleset(new[] { new PermissionRule("read", "*", PermissionAction.Ask) });
        await Assert.That(a.Equals(b)).IsFalse();
        await Assert.That(a == b).IsFalse();
    }

    [Test]
    public async Task Injected_Policy_Guards_New_Tool_Without_Contract_Edit()
    {
        var ruleset = new PermissionRuleset(
            new[] { new PermissionRule("mytool", "*", PermissionAction.Allow) },
            new IArgSafetyPolicy[] { new DenyMyToolPolicy() });
        await Assert.That(ruleset.Evaluate("mytool", "anything")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task Custom_PathGuard_Set_Suppresses_Traversal_Allow()
    {
        var ruleset = new PermissionRuleset(
            new[] { new PermissionRule("mytool", "src/*", PermissionAction.Allow) },
            new IArgSafetyPolicy[] { new PathGuardSafetyPolicy(new[] { "mytool" }) });
        await Assert.That(ruleset.Evaluate("mytool", "src/../../etc/passwd")).IsEqualTo(PermissionAction.Ask);
        await Assert.That(ruleset.Evaluate("mytool", "src/Program.cs")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task MatchesPattern_BackslashInArgPath_MatchesSlashPattern()
    {
        var rule = new PermissionRule("read", "src/*", PermissionAction.Allow);
        await Assert.That(rule.MatchesPattern("src\\a.cs")).IsTrue();
    }

    [Test]
    public async Task MatchesPattern_BackslashInPattern_MatchesSlashArg()
    {
        var rule = new PermissionRule("read", "src\\*", PermissionAction.Allow);
        await Assert.That(rule.MatchesPattern("src/a.cs")).IsTrue();
    }

    [Test]
    public async Task MatchesPattern_MixedSeparators_Match()
    {
        var rule = new PermissionRule("read", "src\\sub/*", PermissionAction.Allow);
        await Assert.That(rule.MatchesPattern("src/sub\\a.cs")).IsTrue();
        await Assert.That(rule.MatchesPattern("src\\sub/a.cs")).IsTrue();
    }

    [Test]
    public async Task MatchesPattern_SeparatorClass_DoesNotOverMatch()
    {
        var rule = new PermissionRule("read", "src/*.cs", PermissionAction.Allow);
        await Assert.That(rule.MatchesPattern("src/a.cs")).IsTrue();
        await Assert.That(rule.MatchesPattern("srcx/a.cs")).IsFalse();
        await Assert.That(rule.MatchesPattern("other/a.cs")).IsFalse();
    }

    private sealed class DenyMyToolPolicy : IArgSafetyPolicy
    {
        public bool AppliesTo(string permission) =>
            permission.Equals("mytool", StringComparison.OrdinalIgnoreCase);

        public PermissionAction? PreEvaluate(string argPath) => PermissionAction.Deny;

        public IReadOnlyList<string>? GetExtraDenyTargets(string argPath) => null;

        public bool SuppressAllow(string rulePattern, string argPath) => false;
    }
}
