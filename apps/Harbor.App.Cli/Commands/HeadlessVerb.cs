using System.Runtime.InteropServices;
using Harbor.App.Cli.Hosting;
using Harbor.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Headless daemon mode (<c>harbor --headless</c>, also used by
///     <c>harbor daemon start</c>): run the full agent host (EventBus,
///     tools, providers, storage) plus the IPC server — no UI, no REPL,
///     no console interaction — and block until SIGINT/SIGTERM. Remote
///     clients connect over IPC; the spawning parent typically redirects
///     stdin/stdout. Extracted from <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class HeadlessVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args)
    {
        logger.LogInformation("Starting headless daemon mode");

        // A headless host without a transport serves nobody: default to
        // ipc-server unless the operator pinned another mode explicitly.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARBOR_MODE")))
        {
            Environment.SetEnvironmentVariable("HARBOR_MODE", "ipc-server");
        }

        using var host = HostBuilder.Build(args);
        var server = host.Services.GetService<IHarborServer>();
        if (server is null)
        {
            logger.LogError(
                "Headless mode requires an IPC server, but no IHarborServer is registered (HARBOR_MODE={Mode})",
                Environment.GetEnvironmentVariable("HARBOR_MODE"));
            Console.Error.WriteLine("daemon: IPC server unavailable — cannot run headless.");
            return 1;
        }

        await CliInfrastructure.StartIpcAsync(host.Services, logger).ConfigureAwait(false);
        Console.WriteLine($"harbor daemon listening on '{server.Endpoint}'");
        CliInfrastructure.PrintPairingBlock(host.Services, logger);
        logger.LogInformation("Daemon ready on {Endpoint} — waiting for clients or shutdown signal", server.Endpoint);

        using var shutdownCts = new CancellationTokenSource();

        // Ctrl+C (SIGINT): cancel the wait but stay alive long enough for the
        // graceful IPC stop + host dispose below.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdownCts.Cancel();
        };

        // SIGTERM (`kill`, `harbor daemon stop`). Registration is best-effort:
        // without it the process still exits on SIGTERM, just ungracefully.
        try
        {
            using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM, _ => shutdownCts.Cancel());
            await WaitForShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException ex)
        {
            logger.LogWarning(ex, "POSIX signal handling unavailable — falling back to SIGINT only");
            await WaitForShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
        }

        logger.LogInformation("Shutdown requested — stopping IPC server");
        await CliInfrastructure.StopIpcAsync(host.Services, logger).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    ///     Park the caller until the shutdown token fires. An infinite delay
    ///     arms no timer — the await completes only via cancellation.
    /// </summary>
    internal static async Task WaitForShutdownAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown path — the token is cancelled by SIGINT/SIGTERM.
        }
    }
}
