using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Tools;
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
    /// <remarks>
    ///     The transport appends <c>.sock</c> and binds the name as a Unix
    ///     domain socket path under the OS temp dir. macOS caps socket paths
    ///     at 104 chars and its default TMPDIR already eats ~49, so the old
    ///     full 32-hex GUID suffix threw ArgumentOutOfRangeException on
    ///     macos-latest. An 8-hex suffix (4·10⁹ space) is collision-proof
    ///     for a test run.
    /// </remarks>
    public static string UniquePipeName(string prefix)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        return $"{prefix}-{id}".ToLowerInvariant();
    }
}
