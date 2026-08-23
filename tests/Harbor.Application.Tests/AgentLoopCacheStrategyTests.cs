using Harbor.Application.Tests.Fakes;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Core.Agents;
using Harbor.Core.Permissions;
using Harbor.Core.Resilience;
using Harbor.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     A1: every main-loop <see cref="LlmRequest" /> carries
///     <see cref="CacheStrategy.Ephemeral" /> while the system prompt is
///     non-empty — the Anthropic client's cache_control branch was dead
///     before because both request sites left the strategy at None.
/// </summary>
public class AgentLoopCacheStrategyTests
{
    private static AgentDefinition AllowAllAgent() => new(
        AgentName.Create("code"),
        "Code",
        "Cache-strategy harness agent",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static AgentLoop CreateLoop(ScriptedLlmClient client) => new(
        new FakeProviderRegistry(client),
        new FakeToolRegistry(),
        new FakeAgentRegistry(AllowAllAgent()),
        new StubSystemPromptBuilder(),
        new FakeCompactionService(),
        new FakeTokenTracker(),
        new RetryPolicy(),
        new FakeEventBus(),
        new PermissionService(
            new FakeAgentRegistry(AllowAllAgent()),
            NullLogger<PermissionService>.Instance),
        new MessageConverter(),
        NullLogger<AgentLoop>.Instance);

    [Test]
    public async Task RunAsync_TwoTurnRunWithSameTools_RequestsCarryEphemeralCacheStrategy()
    {
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", "{}"),
                new StepFinishEvent(0, "tool_use", new Usage(4, 2))
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(1, 1))
            }
        ]);
        var loop = CreateLoop(client);
        var session = new Fakes.TestSessionContext(
            Session.Create("/tmp/harbor-cache-strategy-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, AllowAllAgent());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Requests.Count).IsEqualTo(2);
        await Assert.That(client.Requests[0].CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
        // The acceptance criterion: the SECOND turn with the SAME tools still
        // goes out as a cache candidate (stable prompt prefix).
        await Assert.That(client.Requests[1].CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
    }
}
