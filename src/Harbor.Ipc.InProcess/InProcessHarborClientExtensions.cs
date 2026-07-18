using Harbor.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Ipc.InProcess;

/// <summary>
///     DI extensions for registering <see cref="InProcessHarborClient" /> as
///     the active <see cref="IHarborClient" />.
/// </summary>
public static class InProcessHarborClientExtensions
{
    /// <summary>
    ///     Register <see cref="InProcessHarborClient" /> as a singleton
    ///     <see cref="IHarborClient" />. The caller is responsible for
    ///     registering <c>IAgent</c>, <c>IAgentRegistry</c>,
    ///     <c>ISessionStore</c>, <c>IProviderRegistry</c>, <c>IToolRegistry</c>
    ///     and <c>IEventBus</c> separately (they are the application-layer
    ///     services already wired by HostBuilder / AppHost).
    /// </summary>
    public static IServiceCollection UseInProcessHarborClient(this IServiceCollection services)
    {
        services.AddSingleton<IHarborClient, InProcessHarborClient>();
        return services;
    }
}
