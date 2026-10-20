using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests;

/// <summary>
///     The <see cref="AgentLoop" /> under test, wired to the doubles the suites use.
/// </summary>
/// <remarks>
///     <para>
///         Two files built the same loop — one with an injected clock, one without —
///         and each wrote the eleven constructor arguments out again. The
///         constructor is the product's, so the helper is the one place that knows
///         its shape.
///     </para>
/// </remarks>
internal static class AgentLoopFixtures
{
    internal static AgentLoop LoopWithClock(ScriptedLlmClient client, FakeEventBus bus, TimeProvider clock)
    {
        var agent = TestAgents.AllowAll();
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(clock),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    internal static AgentLoop Loop(ILlmClient client, ITool tool, FakeEventBus bus, AgentDefinition agent)
    {
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(tool),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }
}
