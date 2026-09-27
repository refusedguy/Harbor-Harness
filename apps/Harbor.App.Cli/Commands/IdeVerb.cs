using Harbor.App.Cli.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor ide --session &lt;id&gt;</c> — NDJSON JSON-RPC stdio bridge
///     for external editors. Protocol frames own stdout: console logging is
///     silenced (the file log keeps full Debug detail) and no renderer is
///     initialized. Defaults to <c>HARBOR_MODE=ipc-client</c> so the bridge
///     attaches to a running daemon/TUI instead of spawning its own agent.
///     Extracted from <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class IdeVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARBOR_LOGLEVEL")))
            Environment.SetEnvironmentVariable("HARBOR_LOGLEVEL", "None");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARBOR_MODE")))
            Environment.SetEnvironmentVariable("HARBOR_MODE", "ipc-client");

        logger.LogInformation("Starting IDE bridge (attach mode)");
        using var host = HostBuilder.Build(args);
        return await IdeBridgeRunner.RunAsync(host.Services, args).ConfigureAwait(false);
    }
}
