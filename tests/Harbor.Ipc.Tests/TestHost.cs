using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Tools;
using Harbor.Storage.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Test helpers: build a minimal in-process service provider wired
///     with all the application-layer services that IHarborClient needs.
/// </summary>
internal static class TestHost
{
    /// <summary>
    ///     Build a service provider with InMemorySessionStore, AgentRegistry,
    ///     ToolRegistry (empty), ProviderRegistry (empty), InMemoryEventBus,
    ///     and a no-op IAgent stub. Used by tests that need a working
    ///     IHarborClient without a real LLM provider.
    /// </summary>
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<ISessionStore, MemorySessionStore>();
        services.AddSingleton<IEventBus>(sp => new InMemoryEventBus(sp.GetRequiredService<ILogger<InMemoryEventBus>>()));
        services.AddSingleton<IAgentRegistry, AgentRegistry>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IProviderRegistry>(sp => new ProviderRegistry(sp.GetRequiredService<ILogger<ProviderRegistry>>()));
        services.AddSingleton<IAgent, StubAgent>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Build a unique pipe name per test to avoid collisions when tests
    ///     run in parallel.
    /// </summary>
    public static string UniquePipeName(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
}
