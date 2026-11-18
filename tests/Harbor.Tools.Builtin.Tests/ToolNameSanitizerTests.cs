using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using Harbor.Tools.Mcp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Issue #2677 item 2: an MCP tool name that reaches the model must fit the
///     provider's <c>[a-zA-Z0-9_-]</c> / 64-character rules, and two different
///     remote tools that sanitize to the same text must not become the same name.
/// </summary>
public class ToolNameSanitizerTests
{
    [Test]
    public async Task AlreadyValidName_KeepsTheLegacyShape()
    {
        // Compatibility: the overwhelming majority of names are already valid;
        // changing them would invalidate stored permission rules for no reason.
        await Assert.That(McpToolAdapter.BuildName("srv", "echo").Value).IsEqualTo("mcp_srv_echo");
        await Assert.That(McpToolAdapter.BuildName("db", "query_v2").Value).IsEqualTo("mcp_db_query_v2");
    }

    [Test]
    public async Task SanitizedName_IsStableAndDistinctForCollidingOriginals()
    {
        var dotted = McpToolAdapter.BuildName("db", "query.v2");
        var dashed = McpToolAdapter.BuildName("db", "query-v2");

        await Assert.That(dotted.Value).StartsWith("mcp_db_query_v2_");
        await Assert.That(dashed.Value).StartsWith("mcp_db_query_v2_");
        await Assert.That(dotted.Value).IsNotEqualTo(dashed.Value);
        await Assert.That(McpToolAdapter.BuildName("db", "query.v2").Value).IsEqualTo(dotted.Value);

        // The NUL-separated digest must not merge ("ab", "c") and ("a", "bc").
        await Assert.That(McpToolAdapter.BuildName("ab", "c").Value)
            .IsNotEqualTo(McpToolAdapter.BuildName("a", "bc").Value);
    }

    [Test]
    public async Task UppercaseInput_IsNormalizedButStillGetsItsOwnName()
    {
        var upper = McpToolAdapter.BuildName("Srv", "Echo");
        var lower = McpToolAdapter.BuildName("srv", "echo");

        await Assert.That(upper.Value).StartsWith("mcp_srv_echo_");
        await Assert.That(upper.Value).IsNotEqualTo(lower.Value);
    }

    [Test]
    public async Task LongName_IsCappedAtTheProviderLimit()
    {
        string longName = new string('a', 200);
        var name = McpToolAdapter.BuildName("server", longName);

        await Assert.That(name.Value.Length).IsLessThanOrEqualTo(ToolNameSanitizer.MaxNameLength);
        await Assert.That(name.Value).StartsWith("mcp_server_");
        await Assert.That(name.Value.All(c =>
            (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')).IsTrue();
    }

    [Test]
    public async Task SanitizedName_MapsBackToOriginalRemoteNameOnInvocation()
    {
        // The model sees the sanitized identifier, but tools/call must still use
        // the exact name the server advertised.
        var registry = ScriptedMcpRegistry.Succeed("""{"result":{"content":[]}}""");
        var tool = new McpToolAdapter(registry, "srv", "db.query.v2");
        using var args = JsonDocument.Parse("""{"arguments":{"q":1}}""");

        ToolResult result = await tool.ExecuteAsync(args.RootElement, TestToolContext.Create());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(tool.Name.Value).StartsWith("mcp_srv_db_query_v2_");
        await Assert.That(registry.LastArgsJson).IsEqualTo("""{"name":"db.query.v2","arguments":{"q":1}}""");
    }
}
