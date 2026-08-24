using Harbor.Ipc.Client;
using Harbor.Ipc.InProcess;
using Harbor.Ipc.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting;

internal static class IpcModule
{
    /// <summary>HARBOR_MODE dispatcher: inprocess / ipc-server / ipc-client.</summary>
    internal static IServiceCollection AddHarborIpc(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        string mode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        ctx.Logger.LogInformation("HARBOR_MODE = {Mode}", mode);

        string pipeName = Environment.GetEnvironmentVariable("HARBOR_IPC_PIPE") ?? "harbor-ipc";

        switch (mode.ToLowerInvariant())
        {
            case "inprocess":
                services.UseInProcessHarborClient();
                break;
            case "ipc-server":
                services.UseInProcessHarborClient();
                services.UseHarborIpcServer(pipeName);
                break;
            case "ipc-client":
                services.UseIpcHarborClient(pipeName);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown HARBOR_MODE: '{mode}'. Expected one of: inprocess, ipc-server, ipc-client.");
        }
        return services;
    }
}
