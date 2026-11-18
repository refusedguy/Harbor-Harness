using System.Diagnostics;
using Harbor.Abstractions.Resilience;

namespace Harbor.Tools.Mcp;

/// <summary>
///     The one owner of the remote-MCP retry loop (#2599). Streamable HTTP and
///     legacy SSE used to carry byte-identical attempt counters, linked
///     cancellation sources, <c>CancelAfter</c>, backoffs, failure formatting
///     and exception filters. A third transport would have copied the loop a
///     third time and a policy change would have had to land in every copy.
/// </summary>
/// <remarks>
///     <para>
///         This is composition, not inheritance, for the reason recorded in
///         <see cref="McpHttpClientFactory" /> (#1835): the transports are
///         public and this owner is internal, so a base class would either be
///         illegal (CS0060) or would add a public type no consumer can use.
///         Each transport holds one of these and hands it a <c>tryOnce</c>
///         delegate for the wire work it alone knows how to do.
///     </para>
///     <para>
///         The transport-specific logging delegates are passed in rather than
///         generalized: the two transports word their retry warnings
///         differently ("request ... retrying" vs "round-trip ...
///         reconnecting"), and #2599 is a duplication fix, not a log-format
///         change. The retry policy itself — attempt budget, transient set,
///         backoff — is read from <see cref="TransientFailurePolicy" /> here,
///         once, so the knobs and the verdict have a single home.
///     </para>
/// </remarks>
internal sealed class McpRoundTripTemplate
{
    /// <summary>Logs a retryable failure while the response is in hand.</summary>
    internal delegate void LogFailed(int attempt, string cause);

    /// <summary>Logs a transient exception that is about to be retried.</summary>
    internal delegate void LogRetrying(int attempt, Exception exception);

    /// <summary>Logs the terminal failure the caller will see.</summary>
    internal delegate void LogFailure(string error);

    private readonly TimeSpan _requestTimeout;
    private readonly string _kind;
    private readonly Uri _endpoint;
    private readonly LogFailed? _onFailed;
    private readonly LogRetrying? _onRetrying;
    private readonly LogFailure? _onFailure;

    internal McpRoundTripTemplate(
        Uri endpoint,
        TimeSpan requestTimeout,
        string kind,
        LogFailed? onFailed,
        LogRetrying? onRetrying,
        LogFailure? onFailure)
    {
        _endpoint = endpoint;
        _requestTimeout = requestTimeout;
        _kind = kind;
        _onFailed = onFailed;
        _onRetrying = onRetrying;
        _onFailure = onFailure;
    }

    /// <summary>
    ///     The one place a transport failure is spelled out (#587). One line, in
    ///     the order an operator reads it — which link, how many tries, how long,
    ///     and then the server's own words. The kind ("HTTP" / "SSE") replaces
    ///     the leading "MCP" every caller already says in its own message.
    /// </summary>
    internal Result<T> Fail<T>(Stopwatch sw, int attempts, string cause)
    {
        string error = $"{_kind} {_endpoint} failed after {attempts} attempt(s) in {sw.Elapsed.TotalMilliseconds:0}ms: {cause}";
        _onFailure?.Invoke(error);
        return Result.Failure<T>(error);
    }

    /// <summary>
    ///     Run <paramref name="tryOnce" /> under the shared retry policy. The
    ///     delegate receives an attempt-scoped token (linked to
    ///     <paramref name="cancellationToken" /> and bounded by the request
    ///     timeout); user cancellation and disposal still propagate as throws.
    /// </summary>
    internal async Task<Result<Maybe<JsonElement>>> RunAsync(
        Func<CancellationToken, Task<McpAttempt>> tryOnce,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        int attempt = 1;
        while (true)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_requestTimeout);
            try
            {
                McpAttempt once = await tryOnce(attemptCts.Token).ConfigureAwait(false);
                if (once.Outcome.IsFailure)
                {
                    // #714: not every failure is a blip. A refused channel, a
                    // missing key or a rejected grant fails identically on
                    // attempt two, so retrying it is latency in front of a
                    // guaranteed error — and for 401 three more chances for the
                    // provider to flag the key.
                    if (!once.Retryable || attempt >= TransientFailurePolicy.DefaultMaxAttempts)
                        return Fail<Maybe<JsonElement>>(sw, attempt, once.Outcome.Error);

                    _onFailed?.Invoke(attempt, once.Outcome.Error);
                    await Task.Delay(TransientFailurePolicy.BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                return once.Outcome;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-attempt timeout, not user cancellation: retryable.
                string cause = $"server did not respond within {_requestTimeout.TotalSeconds:F0}s";
                if (attempt >= TransientFailurePolicy.DefaultMaxAttempts)
                    return Fail<Maybe<JsonElement>>(sw, attempt, cause);

                await Task.Delay(TransientFailurePolicy.BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (TransientFailurePolicy.ShouldRetry(ex)
                                       && attempt < TransientFailurePolicy.DefaultMaxAttempts)
            {
                _onRetrying?.Invoke(attempt, ex);
                await Task.Delay(TransientFailurePolicy.BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (TransientFailurePolicy.ShouldRetry(ex))
            {
                return Fail<Maybe<JsonElement>>(sw, attempt, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not ObjectDisposedException
                && !cancellationToken.IsCancellationRequested)
            {
                // Expected wire failure outside the transient set (e.g. a
                // malformed body): terminal, observable, never a throw.
                return Fail<Maybe<JsonElement>>(sw, attempt, ex.Message);
            }
        }
    }
}

/// <summary>
///     One attempt's outcome plus whether its cause earns another attempt
///     (#714). The verdict rides beside the <see cref="Result{T}" /> because
///     <c>Failure</c> stores one string and a caller that wants to branch on
///     401-vs-503 has nothing but prose to parse once it crosses the boundary.
/// </summary>
internal readonly record struct McpAttempt(Result<Maybe<JsonElement>> Outcome, bool Retryable)
{
    /// <summary>A failure worth another attempt: a dropped socket, a closed stream, a 5xx.</summary>
    internal static McpAttempt Transient(Result<Maybe<JsonElement>> outcome) => new(outcome, true);

    /// <summary>A failure that will repeat verbatim: a refusal, a bad grant, a wrong URL.</summary>
    internal static McpAttempt Terminal(Result<Maybe<JsonElement>> outcome) => new(outcome, false);

    /// <summary>The success case — there is no cause to weigh.</summary>
    internal static McpAttempt Succeeded(Result<Maybe<JsonElement>> outcome) => new(outcome, false);
}
