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
    ///     #595: every tool that REGISTERS declares a category. The table used to
    ///     hold 13 of 20, so seven tools matched no category rule at all while the
    ///     class doc promised "category rules gate whole classes".
    /// </summary>
    /// <remarks>
    ///     The two plugin rows — <c>session_broadcast</c> and <c>session_inbox</c> —
    ///     are declared without a category on purpose and are the only exception.
    ///     They never register in the builtin host, and classifying them changes a
    ///     verdict rather than describing one: a category match makes a rule about
    ///     the class fire against the tool, and CI caught that when
    ///     <c>session_broadcast</c> was briefly Mcp and an earlier Ask rule then
    ///     beat its explicit Allow. The exemption is asserted here so that adding a
    ///     third unclassified tool is a deliberate edit to this list.
    ///     <para>
    ///         #1738 adds the fourth: <c>plugin_manager</c>, which DOES register
    ///         in the builtin host. It is unclassified for the same reason the
    ///         other three are: a category match makes a rule about the class fire
    ///         against the tool, and the write-category Ask in the default ruleset
    ///         would then ask the user about every plugin_manager call — including
    ///         the read-only listing — on top of the tool's own per-change
    ///         question. Two questions about one call is not a policy, it is a
    ///         duplicate, and the tool's own question is the one that names the
    ///         file, the scope and the effect.
    ///     <para>
    ///         The two plugin rows — <c>session_broadcast</c> and <c>session_inbox</c> —
    ///         are declared without a category on purpose and are the only exception.
    ///         They never register in the builtin host, and classifying them changes a
    ///         verdict rather than describing one: a category match makes a rule about
    ///         the class fire against the tool, and CI caught that when
    ///         <c>session_broadcast</c> was briefly Mcp and an earlier Ask rule then
    ///         beat its explicit Allow. The exemption is asserted here so that adding a
    ///         third unclassified tool is a deliberate edit to this list.
    ///     </para>
    ///     <para>
    ///         <b>#1761 — the third row is the <c>goal</c> tool, and it DOES register.</b>
    ///         It is in no class because it is in none of them: it writes the
    ///         session's goal record, which is not a workspace file, not a read, not
    ///         a shell, not the network and not MCP. Calling it Write would be a
    ///         lie about what it does, and the lie has a price: a user's
    ///         <c>new("write", "*", Allow)</c> would then authorise the model's
    ///         verdict on its own work. The fail-closed reading is the right one
    ///         here — a write-category rule must not decide whether the agent may
    ///         declare its goal met.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Every_Builtin_Tool_That_Registers_Declares_A_Category()
    {
        string[] declaredWithoutCategory = ["session_broadcast", "session_inbox", "plugin_manager"];
        string[] declaredWithoutCategory = ["session_broadcast", "session_inbox", "goal"];

        var unclassified = BuiltinToolSafetyProfiles.All
            .Where(d => d.Category is null && !declaredWithoutCategory.Contains(d.ToolName))
            .Select(d => d.ToolName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await Assert.That(unclassified).IsEmpty()
            .Because("a builtin that registers with no category matches no category rule, so a "
                   + "user's new(\"write\", \"*\", Allow) silently stops applying to it the day "
                   + "it is added. Declare the category on the tool instead (#595). If the tool "
                   + "genuinely belongs to no class, add it to the declared exception above "
                   + "with the reason — an unlisted omission is the drift this guards.");

        // And the exceptions are real rows, not names invented by the test.
        await Assert.That(BuiltinToolSafetyProfiles.All
                .Count(d => declaredWithoutCategory.Contains(d.ToolName)))
            .IsEqualTo(declaredWithoutCategory.Length)
            .Because("the unclassified exception names tools that must still appear in the "
                   + "declaration table — a test asserting an exception for a row that does not "
                   + "exist is asserting nothing");
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
