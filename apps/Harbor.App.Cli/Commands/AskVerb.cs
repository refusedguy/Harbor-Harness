using System.Diagnostics;
using Harbor.App.Cli.Hosting;
using Harbor.App.Cli.Repl;
using Harbor.Telemetry;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor ask &lt;prompt&gt;</c> — one-shot agent turn, extracted from
///     <c>Program</c> (#176). Behavior is 1:1 with the former
///     <c>Program.RunAskAsync</c>.
/// </summary>
/// <remarks>
///     #2675 item 022: <c>--timings</c> writes a phase breakdown (startup,
///     auth/connect, execution, cleanup) to stderr after the run. Fire-and-forget
///     work such as skill freshness seeding is listed separately and is not
///     added to total, so the phases stay comparable across runs.
/// </remarks>
internal static class AskVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args, string? scriptPath = null)
    {
        bool timings = CliArgs.ExtractTimingsArg(args, out string[] runArgs);
        if (runArgs.Length == 0)
        {
            Console.Error.WriteLine("Usage: harbor ask <prompt> [--script <path>]");
            return 1;
        }
        string prompt = string.Join(' ', CliArgs.StripLogArgs(runArgs));
        long started = Stopwatch.GetTimestamp();
        AskVerbLog.StartingAsk(logger, prompt.Length);
        CliArgs.MarkApproverless(); // #52: one-shot, frame loop не крутится
        using var host = HostBuilder.Build(runArgs);
        long built = Stopwatch.GetTimestamp();
        await CliInfrastructure.StartIpcAsync(host.Services, logger).ConfigureAwait(false);
        // Issue #23 slice 2: one-shot commands get the same best-effort
        // freshness snapshot (the opt-in panel only matters interactively).
        _ = SkillFreshnessStartup.SeedFromServices(host.Services);
        var scriptResult = await CliInfrastructure.RunStartupScriptAsync(host.Services, scriptPath, logger).ConfigureAwait(false);
        if (scriptResult.IsFailure)
        {
            AskVerbLog.StartupScriptFailed(logger, scriptResult.Error);
        }
        long connected = Stopwatch.GetTimestamp();
        var runner = CliInfrastructure.CreateRunner(host.Services);
        int exitCode = await runner.RunAskAsync(prompt).ConfigureAwait(false);
        long executed = Stopwatch.GetTimestamp();
        await CliInfrastructure.StopIpcAsync(host.Services, logger).ConfigureAwait(false);
        long stopped = Stopwatch.GetTimestamp();

        if (timings)
        {
            Console.Error.Write(TimingsReport.Format(
            [
                new TimingsReport.Phase("startup", TimingsReport.Elapsed(started, built)),
                new TimingsReport.Phase("auth/connect", TimingsReport.Elapsed(built, connected)),
                new TimingsReport.Phase("execution", TimingsReport.Elapsed(connected, executed)),
                new TimingsReport.Phase("cleanup", TimingsReport.Elapsed(executed, stopped)),
            ], "skill freshness seeding (fire-and-forget; overlaps execution)"));
        }

        // #2674 G1: every one-shot agent run reaches the canonical meter,
        // tagged with the verb that started it and its process exit status.
        new OrchestrationMetrics(MeterMetrics.Instance)
            .Run("ask", Stopwatch.GetElapsedTime(started), exitCode == 0);

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
