using System.Text.Json;
using CSharpFunctionalExtensions;
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
    public async Task ExecuteAsync_ValidSubAgent_NoRunnerWired_FailsHonest()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance);

        var args = Args(("agent", "explore"), ("prompt", "find all TODO comments in src/"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        // G4: with no runner wired the tool must fail explicitly — never fabricate
        // a run that did not happen.
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("unavailable");
        await Assert.That(result.Output).Contains("no runner wired");
    }

    [Test]
    public async Task ExecuteAsync_ValidSubAgent_RunnerExecutesAndReturnsFinalOutput()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var runner = new FakeRunner(Result.Success(new SubAgentRunResult(
            SessionId: "sub-1",
            AgentName: "explore",
            FinalOutput: "found 3 TODO comments in src/",
            NewMessages: 2)));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance, runner);

        var args = Args(("agent", "explore"), ("prompt", "find all TODOs"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("[sub-agent 'explore' finished");
        await Assert.That(result.Output).Contains("session sub-1");
        await Assert.That(result.Output).Contains("found 3 TODO comments in src/");
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
        await Assert.That(runner.Calls[0].Request.Prompt).IsEqualTo("find all TODOs");
        await Assert.That(runner.Calls[0].Request.ParentSessionId).IsEqualTo("session-1");
        await Assert.That(runner.Calls[0].Agent.Name.Value).IsEqualTo("explore");
    }

    [Test]
    public async Task ExecuteAsync_RunnerFailure_PropagatesAsToolError()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var runner = new FakeRunner(Result.Failure<SubAgentRunResult>("model exploded"));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance, runner);

        var args = Args(("agent", "explore"), ("prompt", "hi"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("model exploded");
    }

    [Test]
    public async Task ExecuteAsync_NestingGuardActive_RefusesBeforeRunnerCall()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var runner = new FakeRunner(Result.Success(new SubAgentRunResult("sub-1", "explore", "never", 0)), canSpawn: false);
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance, runner);

        var args = Args(("agent", "explore"), ("prompt", "hi"));
        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("cannot invoke 'task'");
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
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
    public async Task PromptGuidelines_DescribeDelegationContract()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        await Assert.That(tool.PromptGuidelines.Count).IsGreaterThan(0);
        // Guidelines must state the real contract: self-contained prompts and no
        // further nesting — they previously steered AWAY from the tool entirely.
        bool mentionsSelfContained = tool.PromptGuidelines.Any(g =>
            g.Contains("self-contained", StringComparison.OrdinalIgnoreCase));
        await Assert.That(mentionsSelfContained).IsTrue();
    }

    private sealed class FakeRunner(
        Result<SubAgentRunResult> outcome,
        bool canSpawn = true) : ISubAgentRunner
    {
        public List<(AgentDefinition Agent, SubAgentRunRequest Request)> Calls { get; } = [];

        public bool CanSpawn => canSpawn;

        public Task<Result<SubAgentRunResult>> RunAsync(
            AgentDefinition agent, SubAgentRunRequest request, CancellationToken ct = default)
        {
            Calls.Add((agent, request));
            return Task.FromResult(outcome);
        }
    }
}
