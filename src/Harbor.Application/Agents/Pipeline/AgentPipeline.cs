using CSharpFunctionalExtensions;
namespace Harbor.Application.Agents.Pipeline;
/// <summary>
///     Continuation of the behavior chain: invoke the rest of the pipeline
///     (ultimately the agent loop core) with the given request.
/// </summary>
public delegate Task<Result> PipelineNext(PromptRequest request, CancellationToken ct);

/// <summary>
///     One cross-cutting concern wrapped around the agent run (MediatR-style
///     pipeline behavior, audit v2 §3.5). Behaviors may short-circuit by returning
///     a failure without calling the next delegate, observe or time the
///     downstream work, or transform the request. Implementations MUST be safe to
///     share across concurrent runs — keep per-run state local to
///     <see cref="HandleAsync" />.
/// </summary>
public interface IPipelineBehavior
{
    /// <summary>Wrap the downstream pipeline for one run.</summary>
    /// <param name="request">The run entering this behavior.</param>
    /// <param name="next">The rest of the pipeline (core loop last).</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The run outcome, or failure when short-circuited.</returns>
    Task<Result> HandleAsync(PromptRequest request, PipelineNext next, CancellationToken ct);
}

/// <summary>
///     Immutable ordered composition of behaviors around a terminal handler.
///     The chain is built once per composition root; each invocation threads the
///     request through every behavior in order, with the terminal handler last.
/// </summary>
public sealed class AgentPipeline
{
    private readonly IPipelineBehavior[] _behaviors;

    public AgentPipeline(IEnumerable<IPipelineBehavior> behaviors)
    {
        _behaviors = [.. behaviors];
    }

    /// <summary>
    ///     Execute the chain: behaviors in registration order, terminal handler last.
    /// </summary>
    public async Task<Result> HandleAsync(PromptRequest request, PipelineNext terminal, CancellationToken ct)
    {
        PipelineNext chain = terminal;
        // Compose right-to-left so behavior[0] runs outermost. Locals capture the
        // current chain — the loop variable itself must not leak into closures.
        for (int i = _behaviors.Length - 1; i >= 0; i--)
        {
            IPipelineBehavior behavior = _behaviors[i];
            PipelineNext next = chain;
            chain = (req, token) => behavior.HandleAsync(req, next, token);
        }

        return await chain(request, ct).ConfigureAwait(false);
    }
}
