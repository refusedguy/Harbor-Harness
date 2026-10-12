using System.Diagnostics;
using Harbor.Application.Resources;
using Microsoft.Extensions.Logging;
namespace Harbor.Application.Agents.Pipeline;
/// <summary>
///     Logs the run lifecycle around the rest of the pipeline: the localized
///     "AgentLoopStarting" entry (moved verbatim out of <c>AgentLoop.RunAsync</c>,
///     audit v2 §3.5) plus a Debug-level duration line when the run settles.
/// </summary>
public sealed class LoggingBehavior(ILogger logger) : IPipelineBehavior
{
    /// <inheritdoc />
    public async Task<CSharpFunctionalExtensions.Result> HandleAsync(
        PromptRequest request, PipelineNext next, CancellationToken ct)
    {
        LoggingBehaviorLog.AgentLoopStarting(logger, request.Agent.Name.Value);
        long start = Stopwatch.GetTimestamp();
        try
        {
            return await next(request, ct).ConfigureAwait(false);
        }
        finally
        {
            // GetElapsedTime avoids allocating a Stopwatch per run on the hot path.
            LoggingBehaviorLog.AgentRunSettled(logger, request.Session.Session.Id, request.Agent.Name.Value, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="LoggingBehavior" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class LoggingBehaviorLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Agent loop starting: agent={Agent}")]
    public static partial void AgentLoopStarting(ILogger logger, string agent);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Agent run settled: session={SessionId} agent={Agent} elapsed={ElapsedMs}ms")]
    public static partial void AgentRunSettled(ILogger logger, string sessionId, string agent, double elapsedMs);

}
