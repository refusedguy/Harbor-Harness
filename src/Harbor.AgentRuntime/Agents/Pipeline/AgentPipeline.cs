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
///     The composition is built once per instance; each invocation applies it to
///     the caller's terminal handler, threading the request through every
///     behavior in order, with the terminal handler last.
/// </summary>
public sealed class AgentPipeline
{
    private readonly Func<PipelineNext, PipelineNext> _chain;

    public AgentPipeline(IEnumerable<IPipelineBehavior> behaviors)
    {
        // Fold the behaviors into one composition function at construction time.
        // Applying the accumulated chain OUTSIDE each new wrapper leaves
        // behavior[0] outermost, matching the registration order on HandleAsync.
        // HandleAsync only applies this function to its per-run terminal handler.
        Func<PipelineNext, PipelineNext> chain = static terminal => terminal;
        foreach (IPipelineBehavior behavior in behaviors)
        {
            Func<PipelineNext, PipelineNext> wrap = next =>
                (req, token) => behavior.HandleAsync(req, next, token);
            Func<PipelineNext, PipelineNext> outer = chain;
            chain = terminal => outer(wrap(terminal));
        }
        _chain = chain;
    }

    /// <summary>
    ///     Execute the chain: behaviors in registration order, terminal handler last.
    /// </summary>
    public async Task<Result> HandleAsync(PromptRequest request, PipelineNext terminal, CancellationToken ct)
    {
        return await _chain(terminal)(request, ct).ConfigureAwait(false);
    }
}
