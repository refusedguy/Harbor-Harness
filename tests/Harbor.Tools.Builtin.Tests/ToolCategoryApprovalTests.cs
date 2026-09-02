using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     C2 (sprint 6): granular approvals — a rule whose permission field
///     names a ToolCategory gates every tool of that category, while unknown
///     (plugin) tools stay outside every category.
/// </summary>
public class ToolCategoryApprovalTests
{
    private static readonly PermissionRuleset Ruleset = new(new PermissionRule[]
    {
        new("read", "*", PermissionAction.Allow),
        new("exec", "*", PermissionAction.Ask),
        new("network", "*", PermissionAction.Deny)
    });

    [Test]
    public async Task CategoryRule_GatesEveryMemberTool()
    {
        // exec category: bash is asked even though no "bash" rule exists.
        await Assert.That(Ruleset.Evaluate("bash", "ls")).IsEqualTo(PermissionAction.Ask);

        // read category allow: all readers allowed...
        await Assert.That(Ruleset.Evaluate("read", "any/path")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(Ruleset.Evaluate("grep", "*.cs")).IsEqualTo(PermissionAction.Allow);

        // network category deny: webfetch denied without its own rule.
        await Assert.That(Ruleset.Evaluate("webfetch", "https://example.com")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task ToolsWithoutCategory_AreUnaffectedByCategoryRules()
    {
        // "task" belongs to NO category and has no exact rule → Ask fallback;
        // it must NOT be caught by the exec/network/read category rules.
        await Assert.That(Ruleset.Evaluate("task", "*")).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task SpecificToolRule_WinsOverCategoryRule()
    {
        var ruleset = new PermissionRuleset(new PermissionRule[]
        {
            new("bash", "*", PermissionAction.Ask),   // category-level ask…
            new("bash", "git status", PermissionAction.Allow) // …tool-level exception
        });

        await Assert.That(ruleset.Evaluate("bash", "git status")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(ruleset.Evaluate("bash", "rm file")).IsEqualTo(PermissionAction.Ask);
    }

    [Test]
    public async Task UnknownCategoryName_NeverMatches()
    {
        var ruleset = new PermissionRuleset(new[]
        {
            new PermissionRule("kernel", "*", PermissionAction.Deny)
        });

        await Assert.That(ruleset.Evaluate("bash", "anything")).IsEqualTo(PermissionAction.Ask);
        await Assert.That(ruleset.Evaluate("read", "x")).IsEqualTo(PermissionAction.Ask);
    }
}
