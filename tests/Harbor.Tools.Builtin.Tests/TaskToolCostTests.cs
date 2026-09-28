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
///     Tests for [UX6] #266 on the <c>task</c> result envelope: the child cost
///     suffix on success output, the resume-hint metadata on failures, and the
///     parent-message linkage passed through to the runner.
/// </summary>
public class TaskToolCostTests
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

    [Test]
    public async Task ExecuteAsync_Success_WithChildUsage_AppendsCostSuffix()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var runner = new FakeRunner(Result.Success(new SubAgentRunResult(
            SessionId: "sub-1",
            AgentName: "explore",
            FinalOutput: "found things",
            NewMessages: 2,
            ChildUsage: new Usage(120, 45))));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance, runner);

        var result = await tool.ExecuteAsync(Args(("agent", "explore"), ("prompt", "hi")), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("2 message(s), +120↑ 45↓");
        await Assert.That(result.Metadata).IsEqualTo((object)new Usage(120, 45));
    }

    [Test]
    public async Task ExecuteAsync_Success_WithoutChildUsage_KeepsLegacyShape()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var runner = new FakeRunner(Result.Success(new SubAgentRunResult("sub-1", "explore", "done", 2)));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance, runner);

        var result = await tool.ExecuteAsync(Args(("agent", "explore"), ("prompt", "hi")), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("2 message(s)]");
        await Assert.That(result.Output.Contains("↑")).IsFalse();
    }

    [Test]
    public async Task ExecuteAsync_ForwardsParentMessageLinkage()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var runner = new FakeRunner(Result.Success(new SubAgentRunResult("sub-1", "explore", "done", 2)));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance, runner);

        await tool.ExecuteAsync(Args(("agent", "explore"), ("prompt", "hi")), CreateContext());

        await Assert.That(runner.Calls.Count).IsEqualTo(1);
        await Assert.That(runner.Calls[0].Request.ParentSessionId).IsEqualTo("session-1");
        await Assert.That(runner.Calls[0].Request.ParentMessageId).IsEqualTo("message-1");
    }

    [Test]
    public async Task ExecuteAsync_Failure_WithResumeTrailer_AttachesHintMetadata()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var runner = new FakeRunner(Result.Failure<SubAgentRunResult>(
            "Sub-agent 'explore' failed: model exploded. Its partial history is preserved in session sub-9. [resume-session:sub-9]"));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance, runner);

        var result = await tool.ExecuteAsync(Args(("agent", "explore"), ("prompt", "hi")), CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("[resume-session:sub-9]");
        await Assert.That(result.Metadata).IsNotNull();
        var hint = (SubAgentResumeHint)result.Metadata!;
        await Assert.That(hint.SessionId).IsEqualTo("sub-9");
        await Assert.That(hint.AgentName).IsEqualTo("explore");
    }

    [Test]
    public async Task ExecuteAsync_Failure_WithoutTrailer_MetadataNull()
    {
        var agents = new AgentRegistry();
        agents.Register(SubAgent("explore"));
        var runner = new FakeRunner(Result.Failure<SubAgentRunResult>("model exploded"));
        var tool = new TaskTool(agents, NullLogger<TaskTool>.Instance, runner);

        var result = await tool.ExecuteAsync(Args(("agent", "explore"), ("prompt", "hi")), CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Metadata).IsNull();
    }

    [Test]
    [Arguments("boom [resume-session:abc123]", "abc123", true)]
    [Arguments("no trailer here", null, false)]
    [Arguments("", null, false)]
    [Arguments("dangling [resume-session:abc", null, false)]
    [Arguments("empty [resume-session:]", null, false)]
    [Arguments("spaced [resume-session:a b]", null, false)]
    public async Task ResumeHint_Theory(string error, string? expectedSession, bool expectHint)
    {
        var hint = SubAgentFailureFormat.TryExtractResumeHint(error, "explore");

        await Assert.That(hint is not null).IsEqualTo(expectHint);
        if (expectedSession is not null)
        {
            await Assert.That(hint!.SessionId).IsEqualTo(expectedSession);
            await Assert.That(hint.AgentName).IsEqualTo("explore");
        }
    }

    [Test]
    public async Task ResumeTrailer_RoundTripsThroughFormatter()
    {
        string message = SubAgentFailureFormat.WithResumeTrailer("Sub-agent 'plan' failed: bad day.", "sess42");

        await Assert.That(message).Contains("[resume-session:sess42]");
        var hint = SubAgentFailureFormat.TryExtractResumeHint(message, "plan");
        await Assert.That(hint).IsEqualTo(new SubAgentResumeHint("sess42", "plan"));
    }
}
