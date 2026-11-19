// #3405. Behaviour tests for the marketplace-install gate in McpToolTool:
// the package source is validated by MarketplaceInstallPolicy BEFORE the
// registry call (the point where an install would actually start). A refused
// source must leave the registry completely untouched.

using System.Text.Json;
using Harbor.Abstractions.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

public class McpMarketplaceInstallGateTests
{
    [Test]
    [Arguments("../escape@1.0")]
    [Arguments("https://evil.example/mcp")]
    [Arguments("Uppercase")]
    [Arguments("with space")]
    [Arguments("a/b")]
    [Arguments("good@bad_version")]
    [Arguments("")]
    public async Task ForbiddenSource_RefusedBeforeRegistryInvoke(string source)
    {
        var registry = ScriptedMcpRegistry.Succeed("installed");
        var tool = NewTool(registry);
        string json = "{\"server\":\"marketplace\",\"method\":\"marketplace/install\",\"args\":{\"source\":"
            + JsonSerializer.Serialize(source) + "}}";
        var args = JsonDocument.Parse(json).RootElement;

        var result = await tool.ExecuteAsync(args, TestToolContext.Create());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Refused marketplace install");
        await Assert.That(registry.LastMethod).IsNull()
            .Because("a refused source must not reach the install registry call");
        await Assert.That(registry.LastArgsJson).IsNull();
    }

    [Test]
    public async Task MissingSource_RefusedBeforeRegistryInvoke()
    {
        var registry = ScriptedMcpRegistry.Succeed("installed");
        var tool = NewTool(registry);
        var args = JsonDocument.Parse(
            """{"server":"marketplace","method":"marketplace/install","args":{}}""").RootElement;

        var result = await tool.ExecuteAsync(args, TestToolContext.Create());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Refused marketplace install");
        await Assert.That(registry.LastMethod).IsNull();
    }

    [Test]
    public async Task SafeSource_IsForwardedToRegistry()
    {
        var registry = ScriptedMcpRegistry.Succeed("installed");
        var tool = NewTool(registry);
        var args = JsonDocument.Parse(
            """{"server":"marketplace","method":"marketplace/install","args":{"source":"git-tools@1.2.3"}}""").RootElement;

        var result = await tool.ExecuteAsync(args, TestToolContext.Create());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(registry.LastServer).IsEqualTo("marketplace");
        await Assert.That(registry.LastMethod).IsEqualTo("marketplace/install");
        await Assert.That(registry.LastArgsJson).Contains("git-tools@1.2.3");
    }

    [Test]
    public async Task NonInstallMethod_DoesNotInvokePolicyGate()
    {
        var registry = ScriptedMcpRegistry.Succeed("[]");
        var tool = NewTool(registry);
        var args = JsonDocument.Parse(
            """{"server":"fs","method":"tools/list","args":{}}""").RootElement;

        var result = await tool.ExecuteAsync(args, TestToolContext.Create());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(registry.LastMethod).IsEqualTo("tools/list");
    }

    private static McpToolTool NewTool(IMcpRegistry registry)
        => new(registry, NullLogger<McpToolTool>.Instance);
}
