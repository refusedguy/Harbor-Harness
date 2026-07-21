using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="McpToolTool" /> — argument validation, missing-registry error
///     path, and the InMemoryMcpRegistry stub behaviour (registered-but-no-transport
///     returns a helpful failure rather than hanging or succeeding).
/// </summary>
public class McpToolToolTests
{
    [Test]
    public async Task Name_IsMcp()
    {
        var tool = NewTool(new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance));
        await Assert.That(tool.Name.Value).IsEqualTo("mcp");
    }

    [Test]
    public async Task ExecutionMode_IsSequential()
    {
        var tool = NewTool(new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance));
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
    }

    [Test]
    public async Task ValidateArguments_MissingServer_ReturnsFailure()
    {
        var tool = NewTool(new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance));
        var args = JsonDocument.Parse("""{"method":"tools/list"}""").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("server");
    }

    [Test]
    public async Task ValidateArguments_MissingMethod_ReturnsFailure()
    {
        var tool = NewTool(new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance));
        var args = JsonDocument.Parse("""{"server":"fs"}""").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("method");
    }

    [Test]
    public async Task ValidateArguments_ArgsNotObject_ReturnsFailure()
    {
        var tool = NewTool(new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance));
        var args = JsonDocument.Parse("""{"server":"fs","method":"tools/call","args":"not-an-object"}""").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("args");
    }

    [Test]
    public async Task ExecuteAsync_CallToNonRegisteredServer_ReturnsFailure()
    {
        var registry = new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance);
        var tool = NewTool(registry);

        var args = JsonDocument.Parse(
            """{"server":"nonexistent","method":"tools/list"}""").RootElement;
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("MCP call failed");
        await Assert.That(result.Output).Contains("nonexistent");
        await Assert.That(result.Output).Contains("not registered");
    }

    [Test]
    public async Task ExecuteAsync_CallToRegisteredStubServer_ReturnsFailureWithHelpfulMessage()
    {
        var registry = new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance);
        var registerResult = registry.Register("filesystem", "npx -y @modelcontextprotocol/server-filesystem /tmp");
        await Assert.That(registerResult.IsSuccess).IsTrue();
        var tool = NewTool(registry);

        var args = JsonDocument.Parse(
            """{"server":"filesystem","method":"tools/list","args":{}}""").RootElement;
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        // The stub registry returns Failure for InvokeAsync even on registered servers —
        // a real transport is a separate concern. The error should mention the transport.
        await Assert.That(result.Output).Contains("transport");
    }

    [Test]
    public async Task ExecuteAsync_RegistryFromCtor_IsUsedInPreferenceToServices()
    {
        // When the tool is constructed with an explicit registry, that registry wins
        // even if context.Services also has one (or is null).
        var registry = new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance);
        var tool = NewTool(registry);

        var args = JsonDocument.Parse(
            """{"server":"missing","method":"tools/list"}""").RootElement;
        // context.Services is null! — ctor-provided registry must still resolve.
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not registered");
    }

    [Test]
    public async Task Register_DuplicateServer_ReturnsFailure()
    {
        var registry = new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance);
        await Assert.That(registry.Register("fs", "cmd").IsSuccess).IsTrue();
        var second = registry.Register("fs", "cmd");
        await Assert.That(second.IsFailure).IsTrue();
        await Assert.That(second.Error).Contains("already registered");
    }

    [Test]
    public async Task Register_EmptyName_ReturnsFailure()
    {
        var registry = new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance);
        var result = registry.Register("", "cmd");
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Unregister_ThenInvoke_ReturnsNotRegistered()
    {
        var registry = new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance);
        registry.Register("fs", "cmd");
        await Assert.That(registry.Unregister("fs").IsSuccess).IsTrue();

        var invokeResult = await registry.InvokeAsync("fs", "tools/list", default);
        await Assert.That(invokeResult.IsFailure).IsTrue();
        await Assert.That(invokeResult.Error).Contains("not registered");
    }

    [Test]
    public async Task GetServerNames_ReturnsRegisteredList()
    {
        var registry = new InMemoryMcpRegistry(NullLogger<InMemoryMcpRegistry>.Instance);
        registry.Register("alpha", "cmd-a");
        registry.Register("beta", "cmd-b");
        var names = registry.GetServerNames();
        await Assert.That(names.Count).IsEqualTo(2);
        await Assert.That(names.Contains("alpha")).IsTrue();
        await Assert.That(names.Contains("beta")).IsTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static McpToolTool NewTool(IMcpRegistry registry)
        => new(registry, NullLogger<McpToolTool>.Instance);

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
