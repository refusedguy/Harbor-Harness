using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Registries.Agents;
using Harbor.Registries.Events;
using Harbor.Registries.Providers;
using Harbor.Registries.Scopes;
using Harbor.Registries.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

using FakeLlmClientBase = Harbor.TestKit.Generated.FakeLlmClient; // #1769: generated double

namespace Harbor.Core.Tests;

/// <summary>
///     Tests for the scope-isolated provider lookup inside the agent loop
///     (#1736): one agent may run with its OWN LLM client while every other
///     agent keeps the process-wide one.
/// </summary>
/// <remarks>
///     The three tests below are the three claims the feature makes, and they
///     are checked through the public agent loop — the product code under test
///     is the wiring, not the resolver:
///     <list type="number">
///         <item>a scope that declares a client gets THAT client;</item>
///         <item>an agent no scope is declared for keeps the process-wide client;</item>
///         <item>a test that wants its own client declares a scope and binds one — it
///         does not edit the product's wiring to get it.</item>
///     </list>
/// </remarks>
public class ScopedProviderIsolationTests
{
    private static readonly ModelInfo TestModel = new(
        "test-model",
        "test",
        "Test Model",
        200_000,
        4096,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static readonly ProviderId TestProvider = ProviderId.Create("test");

    [Test]
    public async Task RunAsync_ForAnAgentBoundToAScope_UsesTheScopedClient()
    {
        var global = new CountingLlmClient();
        var scoped = new CountingLlmClient();
        var providers = RegistryWith(global);
        var resolver = new ScopedProviderResolver(providers);
        resolver.BindProvider(ServiceScope.Create("ci"), TestProvider, scoped);
        resolver.BindScope(AgentName.Create("code"), ServiceScope.Create("ci"));

        AgentLoop loop = CreateLoop(providers, resolver);
        Result result = await loop.RunAsync(CreateSession(), AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scoped.StreamCallCount).IsEqualTo(1);
        await Assert.That(global.StreamCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_ForAnAgentWithoutAScope_UsesTheProcessWideClient()
    {
        var global = new CountingLlmClient();
        var scoped = new CountingLlmClient();
        var providers = RegistryWith(global);
        var resolver = new ScopedProviderResolver(providers);
        resolver.BindProvider(ServiceScope.Create("ci"), TestProvider, scoped);

        AgentLoop loop = CreateLoop(providers, resolver);
        Result result = await loop.RunAsync(CreateSession(), AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(global.StreamCallCount).IsEqualTo(1);
        await Assert.That(scoped.StreamCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_WithoutAScopedResolver_UsesTheProcessWideClient()
    {
        var global = new CountingLlmClient();
        var providers = RegistryWith(global);

        AgentLoop loop = CreateLoop(providers, scopedProviders: null);
        Result result = await loop.RunAsync(CreateSession(), AgentDefinition.CodeDefault("test-model", "test"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(global.StreamCallCount).IsEqualTo(1);
    }

    /// <summary>
    ///     The loop under test, wired the way the composition root wires it.
    /// </summary>
    private static AgentLoop CreateLoop(ProviderRegistry providers, IScopedProviderResolver? scopedProviders)
    {
        var tools = new ToolRegistry();
        tools.Freeze();

        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("test-model", "test"));

        var compaction = new CompactionService(
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance);
        var permissions = new PermissionService(agents, NullLogger<PermissionService>.Instance);

        return new AgentLoop(
            providers,
            tools,
            agents,
            new SystemPromptBuilder(),
            compaction,
            new TokenTracker(),
            new RetryPolicy(),
            new InMemoryEventBus(),
            permissions,
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance,
            scopedProviders: scopedProviders);
    }

    /// <summary>
    ///     The process-wide registry the resolver falls back to.
    /// </summary>
    private static ProviderRegistry RegistryWith(ILlmClient client)
    {
        var registry = new ProviderRegistry();
        registry.Register(TestProvider, () => client);
        return registry;
    }

    private static Harbor.TestKit.TestSessionContext CreateSession()
    {
        var session = Session.Create(
            Harbor.TestKit.TestTempDirs.NewDirectory("harbor-scoped-provider"),
            "code",
            "test",
            "test-model");
        return new Harbor.TestKit.TestSessionContext(session, Array.Empty<AgentMessage>());
    }

    /// <summary>
    ///     A client that counts the streams it served — the observable that
    ///     says WHICH client the run used.
    /// </summary>
    private sealed class CountingLlmClient : FakeLlmClientBase
    {
        public CountingLlmClient()
        {
            ProviderId = TestProvider;
            GetModelsAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>([TestModel]);
        }

        public int StreamCallCount { get; private set; }

        public override async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCallCount++;
            yield return TestEvents.TextDelta("0", "scoped answer");
            await Task.Yield();
            yield return TestEvents.StepFinish(usage: new Usage(10, 5));
        }
    }
}
