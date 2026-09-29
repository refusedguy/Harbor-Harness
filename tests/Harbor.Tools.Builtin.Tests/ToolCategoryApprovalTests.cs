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
        // A tool that declares no category matches no category rule, so it falls
        // through to the Ask default. "mcp_prompt" is used here rather than "task"
        // because #595 put both on the declaration: `task` is Exec-class and
        // `mcp_prompt` is MCP-class, so neither is unclassified any more. The
        // unclassified case is now a PLUGIN tool, which is what it always was in
        // practice — the builtin table used to under-report, not over-report.
        await Assert.That(Ruleset.Evaluate("a_plugin_tool", "*")).IsEqualTo(PermissionAction.Ask);

        // And the categories that ARE declared still gate their members.
        await Assert.That(Ruleset.Evaluate("mcp_prompt", "*")).IsEqualTo(PermissionAction.Ask);
    }

    /// <summary>
    ///     #595: every registered builtin now declares a category. The table used
    ///     to hold 13 of 20, so seven tools matched no category rule at all while
    ///     the class doc promised "category rules gate whole classes".
    /// </summary>
    [Test]
    public async Task Every_Declared_Builtin_Tool_Has_A_Category()
    {
        var unclassified = BuiltinToolSafetyProfiles.All
            .Where(d => d.Category is null)
            .Select(d => d.ToolName)
            .ToArray();

        await Assert.That(unclassified).IsEmpty()
            .Because("a builtin with no category matches no category rule, so a user's "
                   + "new(\"write\", \"*\", Allow) silently stops applying to it the day it "
                   + "is added. Declare the category on the tool instead (#595).");
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
