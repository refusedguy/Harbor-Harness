using Microsoft.Extensions.Logging;
namespace Harbor.Application.Agents.Pipeline;
/// <summary>
///     Run-level permission pre-flight (audit v2 §3.5 concern #1). The per-call
///     gating itself stays in <c>ToolDispatcher</c> (it needs the raw arguments);
///     this behavior is the run-level seam: it refuses to start a run whose
///     definition carries no permission ruleset — a null ruleset would otherwise
///     surface as an NRE mid-turn inside tool resolution instead of a clean
///     failure before any LLM call.
/// </summary>
public sealed class PermissionCheckBehavior(ILogger logger) : IPipelineBehavior
{
    /// <inheritdoc />
    public Task<CSharpFunctionalExtensions.Result> HandleAsync(
        PromptRequest request, PipelineNext next, CancellationToken ct)
    {
        if (request.Agent.Permission is null)
        {
            PermissionCheckBehaviorLog.RefusingNoRuleset(logger, request.Agent.Name.Value);
            return Task.FromResult(CSharpFunctionalExtensions.Result.Failure(
                $"Agent '{request.Agent.Name.Value}' has no permission ruleset; refusing to run."));
        }

        return next(request, ct);
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="PermissionCheckBehavior" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class PermissionCheckBehaviorLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Refusing to run agent {Agent}: definition carries no permission ruleset")]
    public static partial void RefusingNoRuleset(ILogger logger, string agent);

}
