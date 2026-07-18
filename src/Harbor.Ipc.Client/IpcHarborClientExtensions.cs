using Harbor.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Ipc.Client;

/// <summary>
///     DI extensions for registering <see cref="IpcHarborClient" /> as the
///     active <see cref="IHarborClient" />.
/// </summary>
public static class IpcHarborClientExtensions
{
    /// <summary>
    ///     Register <see cref="IpcHarborClient" /> as a singleton
    ///     <see cref="IHarborClient" />. The caller must call
    ///     <see cref="IHarborClient.ConnectAsync" /> before using the client.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="pipeName">Pipe name (Windows) or socket basename (Unix). Default <c>harbor-ipc</c>.</param>
    public static IServiceCollection UseIpcHarborClient(this IServiceCollection services, string pipeName = "harbor-ipc")
    {
        services.AddSingleton<IHarborClient>(sp =>
            new IpcHarborClient(pipeName, sp.GetService<ILoggerFactory>()?.CreateLogger<IpcHarborClient>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IpcHarborClient>.Instance));
        return services;
    }
}
