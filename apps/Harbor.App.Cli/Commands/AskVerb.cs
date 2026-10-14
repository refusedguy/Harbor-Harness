using Harbor.App.Cli.Hosting;
using Harbor.App.Cli.Repl;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor ask &lt;prompt&gt;</c> — one-shot agent turn, extracted from
///     <c>Program</c> (#176). Behavior is 1:1 with the former
///     <c>Program.RunAskAsync</c>.
/// </summary>
internal static class AskVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args, string? scriptPath = null)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: harbor ask <prompt> [--script <path>]");
            return 1;
        }
        string prompt = string.Join(' ', CliArgs.StripLogArgs(args));
        AskVerbLog.StartingAsk(logger, prompt.Length);
        CliArgs.MarkApproverless(); // #52: one-shot, frame loop не крутится
        using var host = HostBuilder.Build(args);
        await CliInfrastructure.StartIpcAsync(host.Services, logger).ConfigureAwait(false);
        // Issue #23 slice 2: one-shot commands get the same best-effort
        // freshness snapshot (the opt-in panel only matters interactively).
        _ = SkillFreshnessStartup.SeedFromServices(host.Services);
        var scriptResult = await CliInfrastructure.RunStartupScriptAsync(host.Services, scriptPath, logger).ConfigureAwait(false);
        if (scriptResult.IsFailure)
        {
            AskVerbLog.StartupScriptFailed(logger, scriptResult.Error);
        }
        var runner = CliInfrastructure.CreateRunner(host.Services);
        int exitCode = await runner.RunAskAsync(prompt).ConfigureAwait(false);
        await CliInfrastructure.StopIpcAsync(host.Services, logger).ConfigureAwait(false);
        return exitCode;
    }
}

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="AskVerb" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AskVerbLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting ask command with prompt length {Length}")]
    public static partial void StartingAsk(ILogger logger, int length);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Startup script failed: {Error}")]
    public static partial void StartupScriptFailed(ILogger logger, string error);
}
