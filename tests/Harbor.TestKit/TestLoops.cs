using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.TestKit;

/// <summary>Factory for pre-wired <see cref="AgentLoop" /> instances.</summary>
public static class TestLoops
{
    /// <summary>Create an <see cref="AgentLoop" /> with all dependencies stubbed.</summary>
    public static AgentLoop Create(ILlmClient client, params ITool[] tools)
    {
        var toolsRegistry = new FakeToolRegistry(tools);
        var agents = new FakeAgentRegistry(TestAgents.AllowAll());
        var bus = new FakeEventBus();
        return new AgentLoop(
            new FakeProviderRegistry(client),
            toolsRegistry,
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
