using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="McpResourceTool" /> and <see cref="McpPromptTool" /> —
///     validation, JSON-RPC method routing, and payload extraction edge cases.
/// </summary>
public class McpResourcePromptToolTests
{
    [Test]
    public async Task ResourceTool_Name_IsReadMcpResource()
    {
        var tool = new McpResourceTool(ScriptedMcpRegistry.Fail("unused"), NullLogger<McpResourceTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("read_mcp_resource");
    }

    [Test]
    public async Task PromptTool_Name_IsMcpPrompt()
    {
        var tool = new McpPromptTool(ScriptedMcpRegistry.Fail("unused"), NullLogger<McpPromptTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("mcp_prompt");
    }

    [Test]
    public async Task ResourceTool_ValidateArguments_MissingUri_ReturnsFailure()
    {
        var tool = new McpResourceTool(ScriptedMcpRegistry.Fail("unused"), NullLogger<McpResourceTool>.Instance);
        var args = JsonDocument.Parse("""{"server":"db"}""").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("uri");
    }

    [Test]
    public async Task PromptTool_ValidateArguments_BadArguments_ReturnsFailure()
    {
        var tool = new McpPromptTool(ScriptedMcpRegistry.Fail("unused"), NullLogger<McpPromptTool>.Instance);
        var args = JsonDocument.Parse("""{"server":"s","name":"p","arguments":"nope"}""").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("arguments");
    }

    [Test]
    public async Task ResourceTool_ExecuteAsync_RoutesResourcesRead_AndReturnsText()
    {
        var registry = ScriptedMcpRegistry.Succeed(
            """{"contents":[{"uri":"file:///a.md","mimeType":"text/markdown","text":"# API\nbody"}]}""");
        var tool = new McpResourceTool(registry, NullLogger<McpResourceTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"server":"db","uri":"file:///a.md"}""").RootElement, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("# API");
        await Assert.That(registry.LastMethod).IsEqualTo("resources/read");
        await Assert.That(registry.LastServer).IsEqualTo("db");
        await Assert.That(registry.LastArgsJson).Contains("file:///a.md");
    }

    [Test]
    public async Task ResourceTool_ExecuteAsync_BlobContent_RejectedWithHint()
    {
        var registry = ScriptedMcpRegistry.Succeed(
            """{"contents":[{"uri":"file:///a.png","mimeType":"image/png","blob":"aGk="}]}""");
        var tool = new McpResourceTool(registry, NullLogger<McpResourceTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"server":"db","uri":"file:///a.png"}""").RootElement, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("binary");
    }

    [Test]
    public async Task ResourceTool_ExecuteAsync_RegistryFailure_SurfacesServerAndUri()
    {
        var tool = new McpResourceTool(ScriptedMcpRegistry.Fail("boom"), NullLogger<McpResourceTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"server":"db","uri":"file:///a.md"}""").RootElement, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("boom");
        await Assert.That(result.Output).Contains("file:///a.md");
    }

    [Test]
    public async Task PromptTool_ExecuteAsync_RoutesPromptsGet_AndRendersRoles()
    {
        var registry = ScriptedMcpRegistry.Succeed(
            """{"description":"Review","messages":[{"role":"user","content":{"type":"text","text":"Review this"}},{"role":"assistant","content":{"type":"text","text":"Sure"}}]}""");
        var tool = new McpPromptTool(registry, NullLogger<McpPromptTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"server":"s","name":"review","arguments":{"focus":"api"}}""").RootElement, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("[user]");
        await Assert.That(result.Output).Contains("Review this");
        await Assert.That(registry.LastMethod).IsEqualTo("prompts/get");
        await Assert.That(registry.LastArgsJson).Contains("review");
    }

    [Test]
    public async Task PromptTool_ExecuteAsync_NonTextParts_SkippedWithNote()
    {
        var registry = ScriptedMcpRegistry.Succeed(
            """{"messages":[{"role":"user","content":{"type":"image","data":"x"}},{"role":"user","content":{"type":"text","text":"Describe"}}]}""");
        var tool = new McpPromptTool(registry, NullLogger<McpPromptTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"server":"s","name":"p"}""").RootElement, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("Describe");
        await Assert.That(result.Output).Contains("omitted");
    }

    [Test]
    public async Task PromptTool_ExecuteAsync_EmptyMessages_ReturnsFailure()
    {
        var registry = ScriptedMcpRegistry.Succeed("""{"messages":[]}""");
        var tool = new McpPromptTool(registry, NullLogger<McpPromptTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"server":"s","name":"p"}""").RootElement, CreateContext());

        await Assert.That(result.IsError).IsTrue();
    }

    private static ToolContext CreateContext() => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);
}
