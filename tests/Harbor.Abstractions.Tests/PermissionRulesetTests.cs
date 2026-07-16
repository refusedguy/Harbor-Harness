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
        var action = merged.Evaluate("write", "/etc/passwd");
        await Assert.That(action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Evaluate_UnmatchedPermission_ReturnsAsk()
    {
        var ruleset = new PermissionRuleset(Array.Empty<PermissionRule>());
        var action = ruleset.Evaluate("custom_tool", "*");
        await Assert.That(action).IsEqualTo(PermissionAction.Ask);
    }
}
