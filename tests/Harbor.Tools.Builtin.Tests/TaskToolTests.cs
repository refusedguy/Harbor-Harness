using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="TaskTool" /> — argument validation, unknown-agent errors,
///     non-sub-agent rejection, and the happy path message formatting.
/// </summary>
public class TaskToolTests
{
    private static AgentDefinition SubAgent(string name) => new(
        AgentName.Create(name),
        name,
        name,
        "test-model",
        "test",
        PermissionRuleset.Empty,
        20,
        IsSubAgent: true);

    private static AgentDefinition NonSubAgent(string name) => new(
        AgentName.Create(name),
        name,
        name,
        "test-model",
        "test",
        PermissionRuleset.Default);

    private static ToolContext CreateContext() => new(
        "session-1",
        "message-1",
        "call-1",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);

    private static JsonElement Args(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach ((string k, string v) in pairs)
            dict[k] = v;
        return JsonDocument.Parse(JsonSerializer.Serialize(dict)).RootElement.Clone();
    }

    [Test]
    public async Task Name_IsTask()
    {
        var agents = new AgentRegistry();
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("task");
    }

    [Test]
    public async Task ValidateArguments_MissingAgent_ReturnsFailure()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        var args = JsonDocument.Parse("""{"prompt":"hi"}""").RootElement;

        var result = tool.ValidateArguments(args);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("agent");
    }

    [Test]
    public async Task ValidateArguments_MissingPrompt_ReturnsFailure()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        var args = JsonDocument.Parse("""{"agent":"explore"}""").RootElement;

        var result = tool.ValidateArguments(args);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("prompt");
    }

    [Test]
    public async Task ValidateArguments_AgentNotString_ReturnsFailure()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        var args = JsonDocument.Parse("""{"agent":123,"prompt":"hi"}""").RootElement;

        var result = tool.ValidateArguments(args);

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task ValidateArguments_BothPresent_ReturnsSuccess()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        var args = Args(("agent", "explore"), ("prompt", "look around"));

        var result = tool.ValidateArguments(args);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_UnknownAgent_ReturnsErrorListingAvailable()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance);

        var args = Args(("agent", "nonexistent"), ("prompt", "hi"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Unknown sub-agent");
        await Assert.That(result.Output).Contains("explore");
    }

    [Test]
    public async Task ExecuteAsync_NonSubAgent_ReturnsError()
    {
        var agents = new AgentRegistry();
        agents.Register(NonSubAgent("code"));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance);

        var args = Args(("agent", "code"), ("prompt", "do work"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not a sub-agent");
        await Assert.That(result.Output).Contains("IsSubAgent");
    }

    [Test]
    public async Task ExecuteAsync_ValidSubAgent_ReturnsQueuedMessage()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance);

        var args = Args(("agent", "explore"), ("prompt", "find all TODO comments in src/"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("explore");
        await Assert.That(result.Output).Contains("queued");
    }

    [Test]
    public async Task ExecuteAsync_InvalidAgentName_ReturnsError()
    {
        var agents = new AgentRegistry();
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance);

        // AgentName.Create lowercases and accepts any non-empty string, so use a
        // clearly invalid one — empty string triggers the failure path.
        var args = Args(("agent", ""), ("prompt", "hi"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Invalid agent name");
    }

    [Test]
    public async Task ExecuteAsync_NoSubAgentsRegistered_ListsEmpty()
    {
        var agents = new AgentRegistry();
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance);

        var args = Args(("agent", "explore"), ("prompt", "hi"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Unknown sub-agent");
    }

    [Test]
    public async Task ExecutionMode_IsSequential()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
    }

    [Test]
    public async Task PromptSnippet_ContainsTaskKeyword()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        await Assert.That(tool.PromptSnippet).IsNotNull();
        await Assert.That(tool.PromptSnippet!).Contains("task");
    }

    [Test]
    public async Task PromptGuidelines_ContainsSubAgentExamples()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        await Assert.That(tool.PromptGuidelines.Count).IsGreaterThan(0);
        // The guidelines should mention at least one of the builtin sub-agents.
        bool mentionsExplore = tool.PromptGuidelines.Any(g => g.Contains("explore"));
        await Assert.That(mentionsExplore).IsTrue();
    }
}
