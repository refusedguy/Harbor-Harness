using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Ipc;
using Harbor.Ipc.Ide;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor ide --session &lt;id&gt;</c> — stdio NDJSON JSON-RPC bridge for
///     external editors. The spawning editor owns this process's stdin/stdout:
///     protocol frames are the ONLY traffic on those streams, so the caller must
///     keep console logging off (<c>HARBOR_LOGLEVEL=None</c>) and the renderer
///     uninitialized — this runner never touches the TUI.
/// </summary>
/// <remarks>
///     <para>
///         <b>Attach semantics:</b> the bridge talks to a live Harbor host
///         through <see cref="IHarborClient"/>. With
///         <c>HARBOR_MODE=ipc-client</c> (the default for this verb) it connects
///         to the running daemon/TUI, so an injected prompt streams in the
///         user's TUI in real time. With <c>inprocess</c> it drives its own
///         agent headlessly — useful for tests and scripting.
///     </para>
/// </remarks>
public static class IdeBridgeRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? sessionId = ParseSessionArg(args);
        if (sessionId is null)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor ide --session <id>
                                      Serves NDJSON JSON-RPC on stdin/stdout:
                                        list_sessions | inject_prompt | read_stream | stop_stream | abort
                                    """);
            return 2;
        }

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Harbor.App.Cli.IdeBridge");
        IHarborClient client = services.GetRequiredService<IHarborClient>();

        await client.ConnectAsync().ConfigureAwait(false);
        if (!client.IsConnected)
        {
            Console.Error.WriteLine("ide: unable to connect to the Harbor host (is a daemon running with HARBOR_MODE=ipc-server?).");
            return 1;
        }

        Result<Session> sessionResult = await client.GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            Console.Error.WriteLine($"ide: session '{sessionId}' not found: {sessionResult.Error}");
            return 1;
        }

        Result bind = await client.StartAgentAsync(sessionId, sessionResult.Value.Agent).ConfigureAwait(false);
        if (bind.IsFailure)
        {
            Console.Error.WriteLine($"ide: failed to bind agent to session '{sessionId}': {bind.Error}");
            return 1;
        }

        logger.LogInformation("IDE bridge attached to session {SessionId} (agent {Agent})", sessionId, sessionResult.Value.Agent);

        await using var bridge = new IdeSessionBridge(
            client,
            Console.In,
            Console.Out,
            sessionId,
            logger);

        await bridge.RunAsync(CancellationToken.None).ConfigureAwait(false);

        logger.LogInformation("Editor closed stdin — IDE bridge exiting");
        await client.DisconnectAsync().ConfigureAwait(false);
        return 0;
    }

    private static string? ParseSessionArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--session" or "-s") return args[i + 1];
        }

        // --session=<id> inline form.
        foreach (string a in args)
        {
            if (a.StartsWith("--session=", StringComparison.OrdinalIgnoreCase))
                return a["--session=".Length..];
        }

        return null;
    }
}
