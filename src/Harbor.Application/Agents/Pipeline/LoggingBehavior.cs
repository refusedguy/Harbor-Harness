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
        logger.LogInformation(CoreResources.GetLog("AgentLoopStarting"), request.Agent.Name.Value);
        long start = Stopwatch.GetTimestamp();
        try
        {
            return await next(request, ct).ConfigureAwait(false);
        }
        finally
        {
            // GetElapsedTime avoids allocating a Stopwatch per run on the hot path.
            logger.LogDebug(
                "Agent run settled: session={SessionId} agent={Agent} elapsed={ElapsedMs}ms",
                request.Session.Session.Id,
                request.Agent.Name.Value,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}
