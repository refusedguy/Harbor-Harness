using Harbor.Abstractions.Permissions;
namespace Harbor.Abstractions.Tests;
public class PermissionRulesetTests
{
    [Test]
    public async Task Default_AllowsRead()
    {
        var action = PermissionRuleset.Default.Evaluate("read", "any-file.txt");
        await Assert.That(action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Default_Asks_ForWrite_RootLevel()
    {
        var action = PermissionRuleset.Default.Evaluate("write", "/etc/passwd");
        await Assert.That(action).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task Default_Denies_EnvFiles()
    {
        var action = PermissionRuleset.Default.Evaluate("edit", ".env");
        await Assert.That(action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task Default_Denies_RmRfRoot()
    {
        var action = PermissionRuleset.Default.Evaluate("bash", "rm -rf /");
        await Assert.That(action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task Default_Denies_Sudo()
    {
        var action = PermissionRuleset.Default.Evaluate("bash", "sudo rm file");
        await Assert.That(action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task Default_Allows_BashLs()
    {
        var action = PermissionRuleset.Default.Evaluate("bash", "ls -la");
        await Assert.That(action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Default_Allows_SrcWrite()
    {
        var action = PermissionRuleset.Default.Evaluate("write", "src/Program.cs");
        await Assert.That(action).IsEqualTo(PermissionAction.Allow);
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

    private sealed class DenyMyToolPolicy : IArgSafetyPolicy
    {
        public bool AppliesTo(string permission) =>
            permission.Equals("mytool", StringComparison.OrdinalIgnoreCase);

        public PermissionAction? PreEvaluate(string argPath) => PermissionAction.Deny;

        public IReadOnlyList<string>? GetExtraDenyTargets(string argPath) => null;

        public bool SuppressAllow(string rulePattern, string argPath) => false;
    }
}
