using Harbor.TestKit;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
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
///     A1: every main-loop <see cref="LlmRequest" /> carries
///     <see cref="CacheStrategy.Ephemeral" /> while the system prompt is
///     non-empty — the Anthropic client's cache_control branch was dead
///     before because both request sites left the strategy at None.
/// </summary>
public class AgentLoopCacheStrategyTests
{
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
        var loop = TestLoops.Create(client);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-cache-strategy-tests", "code", "test", "test-model"));
        var result = await loop.RunAsync(session, TestAgents.AllowAll());
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Requests.Count).IsEqualTo(2);
        await Assert.That(client.Requests[0].CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
        // The acceptance criterion: the SECOND turn with the SAME tools still
        // goes out as a cache candidate (stable prompt prefix).
        await Assert.That(client.Requests[1].CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
    }
}
