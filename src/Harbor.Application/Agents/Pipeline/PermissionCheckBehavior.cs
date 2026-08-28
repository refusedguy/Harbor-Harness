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
            logger.LogError(
                "Refusing to run agent {Agent}: definition carries no permission ruleset",
                request.Agent.Name.Value);
            return Task.FromResult(CSharpFunctionalExtensions.Result.Failure(
                $"Agent '{request.Agent.Name.Value}' has no permission ruleset; refusing to run."));
        }

        return next(request, ct);
    }
}
