using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Ipc;
using Harbor.Ipc.Protocol;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.AnsiPlain;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Shared host/IPC/script composition for CLI verbs, extracted from
///     <c>Program</c> (#176). Every verb runner builds its host via
///     <see cref="Harbor.App.Cli.Hosting.HostBuilder" /> and composes the REPL graph here — the
///     runner itself never touches the container twice.
/// </summary>
internal static class CliInfrastructure
{
    /// <summary>
    ///     Composition root for the REPL graph (#63): every ReplRunner
    ///     singleton resolves HERE, once — the runner itself never touches
    ///     the container (except the host provider forwarded to interactive
    ///     renderers, whose public contract demands it).
    /// </summary>
    internal static ReplRunner CreateRunner(IServiceProvider services)
    {
        // Plugin hot-reload: resolving the FS watcher glue starts it.
        // Its disposal rides on the host container teardown.
        _ = services.GetService<Harbor.Hosting.PluginAutoReloader>();

        // Deferred CellForge screens (see CellForgeScreens): stdin/screens
        // resolve only when CellForge mode is actually entered.
        CellForgeScreens Screens()
        {
            var chatScreen = services.GetRequiredService<ChatScreen>();
            // #384: default-on skill-freshness aggregate in the status line. The
            // detail panel stays opt-in (HARBOR_SKILL_FRESHNESS=1) so the pinned
            // 9-panel Alt+1..9 slot order is untouched; the pill lives in the
            // footer row, which has no slot order. The panel polls the model by
            // revision, so `/skills update` updates it in place.
            chatScreen.Status.ProjectedSkills = services.GetService<SkillFreshnessModel>();
            return new(
                services.GetRequiredService<ScreenSession>(),
                chatScreen,
                services.GetRequiredService<ChatScreenBridge>(),
                services.GetRequiredService<TerminalInputSource>(),
                services.GetRequiredService<ITerminalBackend>(),
                services.GetRequiredService<IApprovalCoordinator>());
        };

        return new ReplRunner(
            services.GetRequiredService<ILogger<ReplRunner>>(),
            services.GetRequiredService<IConfigStore>(),
            services.GetRequiredService<AuthStore>(),
            services.GetRequiredService<OnboardingWizard>(),
            services.GetRequiredService<ITuiRenderer>(),
            services.GetRequiredService<IEventBus>(),
            services.GetRequiredService<IAgent>(),
            services.GetRequiredService<ISessionStore>(),
            services.GetRequiredService<IAgentRegistry>(),
            services.GetRequiredService<IProviderRegistry>(),
            services.GetRequiredService<IToolRegistry>(),
            services.GetRequiredService<IPermissionService>(),
            services.GetRequiredService<ILoggerFactory>(),
            services.GetService<Harbor.Hosting.PluginReloadService>(),
            services.GetService<Harbor.Hosting.Rendering.IRendererPipeline>(),
            services.GetService<ITokenTracker>(),
            Screens,
            services,
            SkillFreshnessStartup.RefreshCommand(services),
            SkillFreshnessStartup.UpdateCommand(services),
            services.GetService<IProviderHealthCheck>());
    }

    /// <summary>
    ///     Start the IPC layer based on the active HARBOR_MODE:
    ///     <list type="bullet">
    ///         <item><c>inprocess</c> — no-op (InProcessHarborClient has no transport).</item>
    ///         <item><c>ipc-server</c> — bind <c>IHarborServer</c> and start accepting clients.</item>
    ///         <item><c>ipc-client</c> — call <c>IHarborClient.ConnectAsync</c> to open the pipe/socket.</item>
    ///     </list>
    ///     Silently skips when the relevant service is not registered (e.g. tests
    ///     that build a partial host).
    /// </summary>
    internal static async Task StartIpcAsync(IServiceProvider services, ILogger logger)
    {
        string mode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        if (string.Equals(mode, "ipc-server", StringComparison.OrdinalIgnoreCase))
        {
            // Every registered server starts: the local pipe/UDS listener and
            // — when HARBOR_LISTEN is configured — the networked TCP one.
            foreach (var server in services.GetServices<IHarborServer>())
            {
                logger.LogInformation("Starting IPC server at {Endpoint}", server.Endpoint);
                await server.StartAsync().ConfigureAwait(false);
            }
        }
        else if (string.Equals(mode, "ipc-client", StringComparison.OrdinalIgnoreCase))
        {
            var client = services.GetService<IHarborClient>();
            if (client is not null)
            {
                logger.LogInformation("Connecting IPC client");
                await client.ConnectAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Stop the IPC layer (mirror of <see cref="StartIpcAsync" />).
    /// </summary>
    internal static async Task StopIpcAsync(IServiceProvider services, ILogger logger)
    {
        string mode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        if (string.Equals(mode, "ipc-server", StringComparison.OrdinalIgnoreCase))
        {
            var server = services.GetService<IHarborServer>();
            if (server is not null)
            {
                logger.LogInformation("Stopping IPC server");
                await server.StopAsync().ConfigureAwait(false);
            }
        }
        else if (string.Equals(mode, "ipc-client", StringComparison.OrdinalIgnoreCase))
        {
            var client = services.GetService<IHarborClient>();
            if (client is not null)
            {
                logger.LogInformation("Disconnecting IPC client");
                await client.DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Run a script file at startup. Scripting moved to contrib/scripting
    ///     (sprint 2) — the main CLI reports --script as unsupported.
    /// </summary>
    /// <returns>Success, or failure with an error message. Never throws for expected script failures.</returns>
    internal static async Task<Result> RunStartupScriptAsync(IServiceProvider services, string? scriptPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(scriptPath))
        {
            return Result.Success();
        }

        // Scripting moved to contrib/scripting (sprint 2) — the main CLI no
        // longer ships Harbor.Scripting.*. --script is reported as unsupported
        // rather than silently ignored.
        _ = services;
        logger.LogWarning("--script flag ignored: scripting lives in contrib/scripting and is not part of the main CLI build");
        return CSharpFunctionalExtensions.Result.Failure(
            "Scripting is not available in this build. Build contrib/Contrib.slnx for the scripting-enabled projects.");
    }

    /// <summary>
    ///     When a networked listener is configured, print the pairing block:
    ///     the canonical harbor:// pairing code plus its QR (address follows
    ///     tailscale &gt; lan &gt; loopback priority, so peers outside the
    ///     LAN get the tailnet address — never eth0). Best-effort: a QR
    ///     failure never blocks daemon startup.
    /// </summary>
    internal static void PrintPairingBlock(IServiceProvider services, ILogger logger)
    {
        var pairing = services.GetService<DaemonPairingInfo>();
        if (pairing is null) return;

        Console.WriteLine();
        Console.WriteLine("Remote pairing:");
        Console.WriteLine($"  {pairing.Code}");
        Console.WriteLine($"  PSK file: {PskStore.DefaultPath}");
        try
        {
            Console.WriteLine(TerminalQrRenderer.Render(new Uri(pairing.Code)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QR rendering failed; the text pairing code above remains authoritative");
        }
    }
}
