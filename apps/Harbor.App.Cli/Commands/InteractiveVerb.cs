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
        InteractiveVerbLog.StartingInteractive(logger);
        using var host = HostBuilder.Build(args);
        await CliInfrastructure.StartIpcAsync(host.Services, logger).ConfigureAwait(false);
        // Issue #23 slice 2: seed skill freshness once (best-effort) and
        // register the opt-in CellForge panel before the renderer initializes.
        int seeded = SkillFreshnessStartup.SeedFromServices(host.Services);
        InteractiveVerbLog.SkillFreshnessSeeded(logger, seeded);
        SkillFreshnessStartup.TryRegisterPanel(host.Services);
        var scriptResult = await CliInfrastructure.RunStartupScriptAsync(host.Services, scriptPath, logger).ConfigureAwait(false);
        if (scriptResult.IsFailure)
        {
            InteractiveVerbLog.StartupScriptFailed(logger, scriptResult.Error);
        }
        var runner = CliInfrastructure.CreateRunner(host.Services);
        int exitCode = await runner.RunInteractiveAsync().ConfigureAwait(false);
        InteractiveVerbLog.InteractiveEnded(logger, exitCode);
        await CliInfrastructure.StopIpcAsync(host.Services, logger).ConfigureAwait(false);
        return exitCode;
    }
}

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="InteractiveVerb" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class InteractiveVerbLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting interactive mode")]
    public static partial void StartingInteractive(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Skill freshness seeded: {Count} entries")]
    public static partial void SkillFreshnessSeeded(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Startup script failed: {Error}")]
    public static partial void StartupScriptFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Interactive mode ended with exit code {ExitCode}")]
    public static partial void InteractiveEnded(ILogger logger, int exitCode);
}
