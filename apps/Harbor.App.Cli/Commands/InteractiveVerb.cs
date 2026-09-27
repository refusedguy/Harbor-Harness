using Harbor.App.Cli.Hosting;
using Harbor.App.Cli.Repl;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Interactive REPL entry, extracted from <c>Program</c> (#176).
///     Behavior is 1:1 with the former <c>Program.RunInteractiveAsync</c>.
/// </summary>
internal static class InteractiveVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args, string? scriptPath = null)
    {
        logger.LogInformation("Starting interactive mode");
        using var host = HostBuilder.Build(args);
        await CliInfrastructure.StartIpcAsync(host.Services, logger).ConfigureAwait(false);
        // Issue #23 slice 2: seed skill freshness once (best-effort) and
        // register the opt-in CellForge panel before the renderer initializes.
        int seeded = SkillFreshnessStartup.SeedFromServices(host.Services);
        logger.LogDebug("Skill freshness seeded: {Count} entries", seeded);
        SkillFreshnessStartup.TryRegisterPanel(host.Services);
        var scriptResult = await CliInfrastructure.RunStartupScriptAsync(host.Services, scriptPath, logger).ConfigureAwait(false);
        if (scriptResult.IsFailure)
        {
            logger.LogWarning("Startup script failed: {Error}", scriptResult.Error);
        }
        var runner = CliInfrastructure.CreateRunner(host.Services);
        int exitCode = await runner.RunInteractiveAsync().ConfigureAwait(false);
        logger.LogInformation("Interactive mode ended with exit code {ExitCode}", exitCode);
        await CliInfrastructure.StopIpcAsync(host.Services, logger).ConfigureAwait(false);
        return exitCode;
    }
}
