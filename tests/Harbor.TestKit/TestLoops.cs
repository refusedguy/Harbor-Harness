using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.TestKit;

public static class TestLoops
{
    public static AgentLoop Create(ILlmClient client, params ITool[] tools)
    {
        var providers = new FakeProviderRegistry(client);
        var toolRegistry = new FakeToolRegistry(tools);
        var agents = new FakeAgentRegistry(TestAgents.AllowAll());
        var bus = new FakeEventBus();
        var promptBuilder = new StubSystemPromptBuilder();
        var compaction = new FakeCompactionService();
        var tokenTracker = new FakeTokenTracker();
        var retryPolicy = new RetryPolicy();
        var permissions = new PermissionService(agents, NullLogger<PermissionService>.Instance);
        var converter = new MessageConverter();

        return new AgentLoop(
            providers,
            toolRegistry,
            agents,
            promptBuilder,
            compaction,
            tokenTracker,
            retryPolicy,
            bus,
            permissions,
            converter,
            NullLogger<AgentLoop>.Instance);
    }

    public static AgentLoop Create(
        ScriptedLlmClient client,
        FakeToolRegistry tools,
        ITokenTracker tracker,
        ICompactionService compaction,
        FakeEventBus bus,
        AgentDefinition? agent = null)
    {
        agent ??= TestAgents.AllowAll();
        var agents = new FakeAgentRegistry(agent);
        var providers = new FakeProviderRegistry(client);
        return new AgentLoop(
            providers,
            tools,
            agents,
            new StubSystemPromptBuilder(),
            compaction,
            tracker,
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    public static AgentLoop CreateWithDefaults(AgentDefinition? agent = null)
    {
        agent ??= TestAgents.AllowAll();
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(new ScriptedLlmClient([])),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }
}
