using Harbor.Abstractions.Env;
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
        if (string.IsNullOrEmpty(HarborEnv.Get(HarborEnv.LogLevel)))
            Environment.SetEnvironmentVariable(HarborEnv.LogLevel, "None");
        if (string.IsNullOrEmpty(HarborEnv.Get(HarborEnv.Mode)))
            Environment.SetEnvironmentVariable(HarborEnv.Mode, "ipc-client");

        IdeVerbLog.StartingIdeBridge(logger);
        using var host = HostBuilder.Build(args);
        return await IdeBridgeRunner.RunAsync(host.Services, args).ConfigureAwait(false);
    }
}

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="IdeVerb" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class IdeVerbLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting IDE bridge (attach mode)")]
    public static partial void StartingIdeBridge(ILogger logger);
}
