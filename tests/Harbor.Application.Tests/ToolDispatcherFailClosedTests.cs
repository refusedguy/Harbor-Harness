using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Application.Tests.Fakes;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     G3 (deep2-core): the permission dispatcher used to be fail-open —
///     when <c>PermissionService.CheckAsync</c> returned <c>Failure</c>
///     (agent not registered, invalid agent name), the tool executed anyway.
///     A permission-subsystem failure must deny, never execute.
/// </summary>
public class ToolDispatcherFailClosedTests
{
    private static AgentDefinition CodeAgent() => new(
        AgentName.Create("code"),
        "Code",
        "fail-closed harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static (AgentLoop Loop, ScriptedLlmClient Client, CountingTool Tool) CreateLoop(
        AgentDefinition agent,
        IAgentRegistry permissionRegistry)
    {
        var tool = new CountingTool();
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":1}"""),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "after tool"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        // NOTE: the loop registry contains the agent; the PERMISSION registry is
        // passed separately so tests can simulate an unregistered/diverged agent.
        var agents = new FakeAgentRegistry(agent);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(tool),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(permissionRegistry, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
        return (loop, client, tool);
    }

    private static Fakes.TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-fail-closed-tests", "code", "test", "test-model"));

    [Test]
    public async Task RunAsync_PermissionSubsystemFailure_ToolNeverExecutes()
    {
        AgentDefinition agent = CodeAgent();
        // Empty registry → CheckAsync returns Failure for every call.
        (AgentLoop loop, ScriptedLlmClient client, CountingTool tool) =
            CreateLoop(agent, new FakeAgentRegistry());

        var session = NewSession();
        var result = await loop.RunAsync(session, agent);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(tool.Executions).IsEqualTo(0);
        string nextRequestText = RenderText(client.Requests[1]);
        await Assert.That(nextRequestText).Contains("Permission check failed");
    }

    [Test]
    public async Task RunAsync_PermissionAllow_ToolStillExecutes()
    {
        // Control: a healthy permission subsystem with an Allow rule keeps working.
        AgentDefinition agent = CodeAgent();
        (AgentLoop loop, _, CountingTool tool) =
            CreateLoop(agent, new FakeAgentRegistry(agent));

        var session = NewSession();
        var result = await loop.RunAsync(session, agent);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(tool.Executions).IsEqualTo(1);
    }

    private static string RenderText(LlmRequest request)
    {
        var sb = new System.Text.StringBuilder();
        foreach (LlmMessage message in request.Messages)
        {
            if (message is LlmUserMessage user)
            {
                foreach (LlmContentBlock block in user.Content)
                {
                    if (block is LlmTextBlock text)
                    {
                        sb.Append(text.Text);
                    }
                }
            }
            else if (message is LlmToolResultMessage toolResult)
            {
                sb.Append(toolResult.Output);
            }
        }
        return sb.ToString();
    }
}
