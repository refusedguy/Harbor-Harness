using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Tools;

namespace Harbor.Hosting.Tests;

/// <summary>
///     #2675 item 041: <c>amp.tools.disable</c> parity. The policy is the one
///     thing between a glob in <c>HARBOR_DISABLE_TOOLS</c> and the registry the
///     model sees, so these tests pin the source scoping that makes
///     <c>builtin:</c> safe for a same-named MCP tool.
/// </summary>
public class ToolDisablePolicyTests
{
    [Test]
    public async Task EmptySpec_DisablesNothing()
    {
        foreach (string? spec in new string?[] { null, "", "  ", ", ," })
        {
            var policy = ToolDisablePolicy.Parse(spec);

            await Assert.That(policy.Count).IsEqualTo(0);
            await Assert.That(policy.IsDisabled("read", ToolSourceKind.Builtin)).IsFalse();
        }
    }

    [Test]
    public async Task Glob_MatchesCaseInsensitively_WithWildcards()
    {
        var policy = ToolDisablePolicy.Parse("builtin:web_*,  GREP  , rea?");

        await Assert.That(policy.Count).IsEqualTo(3);
        await Assert.That(policy.IsDisabled("web_fetch", ToolSourceKind.Builtin)).IsTrue();
        await Assert.That(policy.IsDisabled("WEB_SEARCH", ToolSourceKind.Builtin)).IsTrue();
        await Assert.That(policy.IsDisabled("grep", ToolSourceKind.Builtin)).IsTrue();
        await Assert.That(policy.IsDisabled("read", ToolSourceKind.Builtin)).IsTrue();
        await Assert.That(policy.IsDisabled("bash", ToolSourceKind.Builtin)).IsFalse();
    }

    [Test]
    public async Task SourcePrefix_KeepsTheOtherFamily()
    {
        var policy = ToolDisablePolicy.Parse("builtin:fetch,mcp:mcp_server_*");

        await Assert.That(policy.IsDisabled("fetch", ToolSourceKind.Builtin)).IsTrue();
        await Assert.That(policy.IsDisabled("fetch", ToolSourceKind.Mcp)).IsFalse();
        await Assert.That(policy.IsDisabled("mcp_server_search", ToolSourceKind.Mcp)).IsTrue();
        await Assert.That(policy.IsDisabled("mcp_server_search", ToolSourceKind.Builtin)).IsFalse();
    }

    [Test]
    public async Task UnknownPrefix_IsPartOfThePattern_NotAnError()
    {
        var policy = ToolDisablePolicy.Parse("odd:prefix");

        await Assert.That(policy.Count).IsEqualTo(1);
        await Assert.That(policy.IsDisabled("odd:prefix", ToolSourceKind.Builtin)).IsTrue();
        await Assert.That(policy.IsDisabled("prefix", ToolSourceKind.Builtin)).IsFalse();
    }

    [Test]
    public async Task SourceOf_UsesTheMcpNamePrefix()
    {
        await Assert.That(ToolDisablePolicy.SourceOf("read")).IsEqualTo(ToolSourceKind.Builtin);
        await Assert.That(ToolDisablePolicy.SourceOf("mcp_github_list")).IsEqualTo(ToolSourceKind.Mcp);
        await Assert.That(ToolDisablePolicy.SourceOf("MCP_GITHUB_LIST")).IsEqualTo(ToolSourceKind.Mcp);
    }

    [Test]
    public async Task Apply_RemovesOnlyMatchingTools_AndLeavesTheRest()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("read"));
        registry.Register(new StubTool("bash"));
        registry.Register(new StubTool("mcp_server_read"));

        var policy = ToolDisablePolicy.Parse("builtin:read");
        int removed = policy.Apply(registry);

        await Assert.That(removed).IsEqualTo(1);
        await Assert.That(registry.GetAllTools().Select(t => t.Name.Value)).IsEquivalentTo(["bash", "mcp_server_read"]);
    }

    [Test]
    public async Task Apply_WithNoRules_DoesNotTouchTheRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("read"));

        int removed = ToolDisablePolicy.Parse(null).Apply(registry);

        await Assert.That(removed).IsEqualTo(0);
        await Assert.That(registry.GetAllTools().Count).IsEqualTo(1);
    }

    private sealed class StubTool(string name) : ITool
    {
        public ToolName Name => ToolName.Create(name);
        public string DisplayName => name;
        public string Description => "test double";
        public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
