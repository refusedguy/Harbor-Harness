using Harbor.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Ipc.Server;

/// <summary>
///     DI extensions for registering <see cref="HarborIpcServer" /> as a
///     hosted <see cref="IHarborServer" /> singleton.
/// </summary>
public static class HarborIpcServerExtensions
{
    /// <summary>
    ///     Register <see cref="HarborIpcServer" /> as a singleton
    ///     <see cref="IHarborServer" />. The caller must also register the
    ///     application-layer services (<c>IAgent</c>, <c>ISessionStore</c>,
    ///     <c>IProviderRegistry</c>, <c>IToolRegistry</c>, <c>IEventBus</c>,
    ///     <c>IAgentRegistry</c>) — these are normally wired by
    ///     <c>HostBuilder.Build()</c>.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="pipeName">Pipe name (Windows) or socket basename (Unix). Default <c>harbor-ipc</c>.</param>
    public static IServiceCollection UseHarborIpcServer(this IServiceCollection services, string pipeName = "harbor-ipc")
    {
        services.AddSingleton<IHarborServer>(sp =>
            new HarborIpcServer(sp, pipeName, sp.GetService<ILoggerFactory>()));
        return services;
    }
}
