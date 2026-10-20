// RetryLlmClientDecorator.cs — Decorator over ILlmClient: transient-retry for
// the provider stream path (#1502).
//
// WHY A DECORATOR (issue #1502): every provider client streams through
// `SsePump` (linked source in Harbor.Providers.Shared), whose contract is
// "transport failures surface as terminal ErrorEvents" — the pump itself never
// retries, so a transient failure (DNS, connection reset, HTTP 429, 5xx,
// provider timeout) ends the stream on the first occurrence at the provider
// seam. The classification machinery already existed and had two owners in the
// tree — `RetryPolicy`/`IExceptionClassifier` and the tool-side
// `IToolRetryDecider` loop — but nothing on the ILlmClient seam.
//
// WHY THIS FILE LIVES IN Harbor.AgentRuntime (and not in Harbor.Application):
// the decorator news up the concrete `RetryPolicy` in its wiring helper, and
// #1623 moved the resilience IMPLEMENTATIONS (`RetryPolicy`,
// `RetryPolicyExtensions`, `DefaultToolRetryDecider`) out of
// `Harbor.Application` into `Harbor.AgentRuntime` — a one-way edge
// (AgentRuntime → Application). A file in `Harbor.Application` therefore
// cannot name `RetryPolicy` any more: Application binds no moved type, and
// adding the reverse reference would close the cycle the extraction opened on
// purpose. The contracts the decorator consumes (`IRetryPolicy`,
// `RetryOptions`) stayed in `Harbor.Application`, so the decorator needs
// nothing else from it.
//
// The namespace stays `Harbor.Application.Resilience` — the same
// "namespaces unchanged" convention the #1623 extraction used for the files
// it moved (see src/Harbor.AgentRuntime/README.md), so every existing
// `using` keeps compiling.
//
// What this decorator is NOT, deliberately: a second copy of the retry loop
// itself. The first event of the stream (the "preflight", see below) is driven
// through `IRetryPolicy.ExecuteAsync` verbatim, so the budget, the classifier
// chain (`IExceptionClassifier`), the rate-limit budget split (#270) and the
// Retry-After side-channel are all inherited, not restated.
//
// STREAMING-SEMANTICS GUARD (the hard constraint this decorator exists for):
// retrying a stream is only safe while NOTHING has been delivered to the
// consumer. A provider failure that arrives after the first event has already
// been yielded must NEVER be silently restarted — the consumer would observe a
// duplicate prefix (text deltas / tool-call deltas replayed) under a stream
// that looks like one response. Therefore the retry window is exactly the
// preflight: MoveNextAsync #1 of the inner stream. Mid-stream failures pass
// through untouched; whether those get retried remains the decision of the
// caller above (the agent turn path does so in TurnRunner, #270/C7).

using System.Runtime.CompilerServices;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Resilience;

/// <summary>
///     Wraps an <see cref="ILlmClient" /> so a transient provider failure
///     <b>before the first delivered event</b> is retried under the shared
///     <see cref="IRetryPolicy" /> — the same policy, budget split and
///     Retry-After handling the tools and the turn runner already use (#270,
///     C7). Failure semantics after the first delivered event are unchanged:
///     mid-stream errors flow to the consumer exactly as the inner client
///     reports them, and no attempt is made to replay a partially delivered
///     stream.
/// </summary>
/// <remarks>
///     <para>
///         <b>The preflight.</b> The first <c>MoveNextAsync</c> of the inner
///         stream carries the connection: HTTP send, status check, and (for a
///         normal pump) the terminal <see cref="ErrorEvent" /> of any failure
///         that happened before the provider emitted anything. That single
///         step is retried — through <see cref="IRetryPolicy.ExecuteAsync" />,
///         with a transient <see cref="ErrorEvent" /> converted into
///         <see cref="LlmStreamErrorException" />, the exception shape the
///         policy's <c>StreamErrorClassifier</c> already classifies (ROP-A
///         ПР.5). A failed preflight leaves NOTHING delivered, so each retry
///         attempt is indistinguishable from the consumer's point of view —
///         no duplicate prefix, no mid-stream restart.
///     </para>
///     <para>
///         <b>Exhaustion preserves the delivery contract:</b> the rethrow of
///         the budgeted-out failure is converted back into the single
///         <see cref="ErrorEvent" /> the provider path always delivered, so a
///         consumer that pattern-matches events never learns this decorator
///         exists. <see cref="GetModelsAsync" /> is pass-through (result-only
///         contract, catalog-level retry is out of scope here).
///     </para>
///     <para>
///         <b>Not a second budget:</b> callers that already wrap the whole
///         stream consumption in <see cref="IRetryPolicy" /> (TurnRunner's
///         C7 budget, CompactionService's #1106 budget) keep doing so — the
///         decorator only shortens the blast radius of a transient connect
///         failure to the provider seam; the outer budgets continue to own
///         mid-stream and post-delivery replay.
///     </para>
/// </remarks>
public sealed class RetryLlmClientDecorator : ILlmClient
{
    /// <summary>
    ///     Default budget for the preflight retry: the same shape as the
    ///     turn-runner stream budget (3 general attempts, 1 s root), with a
    ///     smaller rate-limit budget — a 429 that survives 3 minutes-scale
    ///     waits at the connection seam belongs to the caller's own outer
    ///     budget (the turn runner's 10-attempt #270 budget), not to a
    ///     multiplication of it.
    /// </summary>
    internal static readonly RetryOptions DefaultProviderRetryOptions = new(
        MaxAttempts: 3,
        BaseDelay: TimeSpan.FromSeconds(1),
        UseJitter: true,
        MaxRateLimitAttempts: 3,
        RateLimitBaseDelay: TimeSpan.FromSeconds(5),
        MaxRetryAfter: TimeSpan.FromMinutes(5));

    private static readonly Preflight Empty = new(Enumerator: null, First: null);

    private readonly ILlmClient _inner;
    private readonly IRetryPolicy _retryPolicy;
    private readonly RetryOptions _options;
    private readonly ILogger<RetryLlmClientDecorator> _logger;

    /// <summary>
    ///     Construct with explicit policy and budget. Production wiring uses
    ///     <see cref="RetryLlmClientWiring.WithTransientRetry" />; tests inject
    ///     a recording clock-backed policy and shrink <paramref name="options" />.
    /// </summary>
    public RetryLlmClientDecorator(
        ILlmClient inner,
        IRetryPolicy retryPolicy,
        ILogger<RetryLlmClientDecorator>? logger = null,
        RetryOptions? options = null)
    {
        _inner = inner;
        _retryPolicy = retryPolicy;
        _options = options ?? DefaultProviderRetryOptions;
        _logger = logger ?? NullLogger<RetryLlmClientDecorator>.Instance;
    }

    /// <inheritdoc />
    public ProviderId ProviderId => _inner.ProviderId;

    /// <summary>
    ///     The guarded stream: retry window = first event only; after that the
    ///     inner stream is delivered verbatim, mid-stream errors included.
    /// </summary>
    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Preflight? preflight = null;
        LlmStreamErrorException? exhausted = null;
        try
        {
            // The retry loop itself is the canonical policy — budget split,
            // classifier chain and Retry-After handling are all inherited.
            preflight = await _retryPolicy.ExecuteAsync(
                attemptCt => PreflightFirstAsync(request, attemptCt),
                _options,
                (ex, attempt) => RetryLlmClientLog.TransientStreamRetry(
                    _logger, _inner.ProviderId.Value, attempt, ex.Message),
                cancellationToken).ConfigureAwait(false);
        }
        catch (LlmStreamErrorException lex)
        {
            // Transient budget exhausted: the consumer still receives exactly
            // one terminal ErrorEvent — the delivery contract of every pump —
            // instead of a thrown exception from StreamAsync.
            exhausted = lex;
        }

        if (exhausted is not null)
        {
            yield return new ErrorEvent(
                exhausted.Message, exhausted.Details, exhausted.Kind, exhausted.StatusCode);
            yield break;
        }

        if (preflight is null || preflight.Enumerator is null)
        {
            yield break; // Graceful empty stream (no events, no error).
        }

        // The preflight already consumed event #1; deliver it...
        IAsyncEnumerator<LlmEvent> source = preflight.Enumerator!;
        yield return preflight.First!;
        // ...then the rest of the stream VERBATIM — the first delivered event
        // is the end of the retry window, whatever fails after it.
        await using (source.ConfigureAwait(false))
        {
            while (await source.MoveNextAsync().ConfigureAwait(false))
            {
                yield return source.Current;
            }
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetModelsAsync(cancellationToken);

    /// <summary>
    ///     One preflight attempt: fresh inner stream, drive the first
    ///     <c>MoveNextAsync</c>. Outcome: (open enumerator + first event),
    ///     graceful-empty, transient —> <see cref="LlmStreamErrorException" />
    ///     (policy restarts this whole method), or pass-through (fatal error
    ///     event / raw exception: delivered as-is, nothing concealed).
    /// </summary>
    private async Task<Preflight> PreflightFirstAsync(LlmRequest request, CancellationToken ct)
    {
        IAsyncEnumerator<LlmEvent> source = _inner.StreamAsync(request, ct).GetAsyncEnumerator(ct);

        bool hasNext;
        try
        {
            hasNext = await source.MoveNextAsync().ConfigureAwait(false);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (!hasNext)
        {
            // Graceful end with no events at all — nothing delivered, nothing to retry.
            await source.DisposeAsync().ConfigureAwait(false);
            return Empty;
        }

        if (source.Current is ErrorEvent err && ProviderErrors.IsTransient(err.Kind))
        {
            // ROP-A ПР.5: the pump reports transport failures as typed error
            // events. Raise the canonical exception channel the policy's
            // StreamErrorClassifier knows — the ErrorEvent itself is NOT
            // delivered; the retry attempt (or the exhaustion handler) owns it.
            await source.DisposeAsync().ConfigureAwait(false);
            throw new LlmStreamErrorException(err);
        }

        // First real event obtained: the attempt succeeded and the *open*
        // enumerator travels out of the policy — the remaining stream continues
        // from this same enumeration, mid-stream errors and all.
        return new Preflight(source, source.Current);
    }

    /// <summary>Outcome of one preflight attempt travelling out of the retry policy.</summary>
    private sealed record Preflight(IAsyncEnumerator<LlmEvent>? Enumerator, LlmEvent? First);
}

/// <summary>
///     #1502 wiring seam: every provider-registration site wraps its freshly
///     constructed client so every resolved <see cref="ILlmClient" /> carries
///     the transient preflight retry. One shared policy instance —
///     <see cref="RetryPolicy" /> is stateless (locals only) and the budget is
///     per-stream by construction.
/// </summary>
public static class RetryLlmClientWiring
{
    private static readonly RetryPolicy SharedPolicy = new();

    /// <summary>
    ///     Decorate a raw provider client with the #1502 preflight retry using
    ///     the default <see cref="RetryLlmClientDecorator.DefaultProviderRetryOptions" />.
    /// </summary>
    public static ILlmClient WithTransientRetry(this ILlmClient client, ILoggerFactory loggerFactory) =>
        new RetryLlmClientDecorator(
            client,
            SharedPolicy,
            loggerFactory.CreateLogger<RetryLlmClientDecorator>());
}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for
///     <see cref="RetryLlmClientDecorator" />.
/// </summary>
internal static partial class RetryLlmClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Transient provider stream failure for {Provider} at attempt {Attempt} — retrying: {Message}")]
    public static partial void TransientStreamRetry(ILogger logger, string provider, int attempt, string message);
}
