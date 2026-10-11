using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Harbor.Abstractions.Resilience;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     MCP legacy "HTTP + SSE" client transport: a long-lived
///     <c>GET /sse</c> event stream carries server→client frames; the first
///     <c>endpoint</c> event names the URL that requests are POSTed to.
///     Connections are lazy and per round-trip — the SSE channel is opened on
///     demand, and a <i>transient</i> failure (stream closed early, 5xx, timeout)
///     reconnects and retries the whole round-trip with exponential backoff.
///     A refusal is not transient and is not retried: a non-transient status
///     (401/403/404), a rejected OAuth grant or a closed stream costs exactly one
///     attempt, because repeating it cannot change the answer (#714). Note: unlike
///     streamable HTTP, a retried request may reach the server twice — the legacy
///     transport has no idempotency guarantee, which is the other reason the
///     transient set is kept this narrow.
///     Authentication mirrors <see cref="McpHttpTransport" />: explicit
///     <c>Authorization</c> header wins, else the OAuth token provider result
///     is attached as <c>Bearer</c>.
/// </summary>
public sealed class McpSseTransport : IMcpRemoteTransport
{
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly Func<CancellationToken, Task<Result<Maybe<string>>>>? _oauthTokenProvider;
    private readonly ILogger? _logger;
    private readonly TimeSpan _requestTimeout;
    private HttpClient? _client;
    private bool _disposed;

    public McpSseTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<CancellationToken, Task<Result<Maybe<string>>>>? oauthTokenProvider = null,
        ILogger? logger = null,
        TimeSpan? requestTimeout = null)
    {
        _endpoint = endpoint;
        _headers = headers;
        _oauthTokenProvider = oauthTokenProvider;
        _logger = logger;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    ///     Result railway for the round-trip (#201 C4, sealed to the interface in
    ///     #587): expected network failures (closed SSE stream, message-endpoint
    ///     errors, client-side timeout) surface as
    ///     <c>Failure(endpoint + attempts + latency + cause)</c> instead of throwing
    ///     (<c>IOException</c>/<c>HttpRequestException</c>/<c>TimeoutException</c> no
    ///     longer escape). Retry/timeout policy stays inside; user cancellation and
    ///     disposal still throw.
    ///     <c>Maybe.None</c> means "succeeded, and the answer legitimately carries no
    ///     document" (202 Accepted, empty body) — a different state from a failure,
    ///     which is why the value is a <see cref="Maybe{T}" /> and not a null.
    /// </summary>
    public async Task<Result<Maybe<JsonDocument>>> TryRoundTripAsync(
        JsonElement request,
        int? expectedId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();
        HttpClient client = GetClient();
        string body = request.GetRawText();
        int attempt = 1;

        while (true)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_requestTimeout);
            try
            {
                Attempt once =
                    await TryRoundTripOnceAsync(client, body, expectedId, attemptCts.Token).ConfigureAwait(false);
                if (once.Outcome.IsFailure)
                {
                    // #714: not every failure is a blip. A refused channel, a missing
                    // key or a rejected grant fails identically on attempt two, so
                    // retrying it is latency in front of a guaranteed error — and for
                    // 401 it is three more chances for the provider to flag the key.
                    // The verdict travels beside the Result because the status code
                    // cannot travel inside it (Failure is a string).
                    if (!once.Retryable || attempt >= TransientFailurePolicy.DefaultMaxAttempts)
                        return Fail<Maybe<JsonDocument>>(sw, attempt, once.Outcome.Error);
                    if (_logger is not null) McpSseTransportLog.RoundTripFailed(_logger, _endpoint, attempt, TransientFailurePolicy.DefaultMaxAttempts, once.Outcome.Error);
                    await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                return once.Outcome;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                string cause = $"server did not respond within {_requestTimeout.TotalSeconds:F0}s";
                if (attempt >= TransientFailurePolicy.DefaultMaxAttempts)
                    return Fail<Maybe<JsonDocument>>(sw, attempt, cause);

                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (TransientFailurePolicy.ShouldRetry(ex)
                                       && attempt < TransientFailurePolicy.DefaultMaxAttempts)
            {
                if (_logger is not null) McpSseTransportLog.RoundTripFailedRetrying(_logger, ex, _endpoint, attempt, TransientFailurePolicy.DefaultMaxAttempts);
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (TransientFailurePolicy.ShouldRetry(ex))
            {
                return Fail<Maybe<JsonDocument>>(sw, attempt, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not ObjectDisposedException
                && !cancellationToken.IsCancellationRequested)
            {
                return Fail<Maybe<JsonDocument>>(sw, attempt, ex.Message);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     One attempt's outcome plus whether its cause earns another attempt (#714).
    /// </summary>
    /// <remarks>
    ///     The verdict rides beside the <see cref="Result{T}" /> rather than inside
    ///     it. <c>Failure</c> is <c>CSharpFunctionalExtensions.Failure</c> — a struct
    ///     around one <see cref="string" /> — so by the time a status code is inside
    ///     a <c>Result</c> it is prose, and a caller that wants to branch on 401-vs-503
    ///     has nothing but string parsing. Minting a typed HTTP error here would answer
    ///     that for good, but it is a new error axis, and this fix does not need one:
    ///     the branch is decided one frame below, while the response is still in hand.
    ///     <para>
    ///         Deliberately transport-private. <see cref="TransientFailurePolicy" />
    ///         documents status classification as *not* its business ("the transports
    ///         classify their own status codes inline at the call site"), so the
    ///         predicate below mirrors <see cref="McpHttpTransport" /> rather than
    ///         being hoisted into a shared type the policy comment disclaims.
    ///     </para>
    /// </remarks>
    private readonly record struct Attempt(Result<Maybe<JsonDocument>> Outcome, bool Retryable)
    {
        /// <summary>A failure worth another attempt: a dropped socket, a closed stream, a 5xx.</summary>
        public static Attempt Transient(Result<Maybe<JsonDocument>> outcome) => new(outcome, true);

        /// <summary>A failure that will repeat verbatim: a refusal, a bad grant, a wrong URL.</summary>
        public static Attempt Terminal(Result<Maybe<JsonDocument>> outcome) => new(outcome, false);

        /// <summary>The success case — there is no cause to weigh.</summary>
        public static Attempt Succeeded(Result<Maybe<JsonDocument>> outcome) => new(outcome, false);
    }

    /// <summary>
    ///     Single SSE round-trip on the Result railway: a closed stream or a
    ///     non-OK endpoint is a <c>Failure</c> (was: <c>IOException</c>/
    ///     <c>HttpRequestException</c> throws). Mid-stream transport exceptions
    ///     still propagate to the retry loop above, which owns the policy.
    ///     <para>
    ///         Each failure also declares whether it is worth retrying (#714). A
    ///         closed stream is a blip and keeps its budget; a refused status and a
    ///         rejected grant do not, and are terminal on the first attempt.
    ///     </para>
    /// </summary>
    private async Task<Attempt> TryRoundTripOnceAsync(
        HttpClient client,
        string body,
        int? expectedId,
        CancellationToken cancellationToken)
    {
        // 1. GET the SSE channel and wait for the endpoint announcement.
        Result<Maybe<string>> oauth = await TryGetOAuthTokenAsync(cancellationToken).ConfigureAwait(false);
        if (oauth.IsFailure)
        {
            // A rejected grant is an answer, not an outage. McpHttpTransport already
            // gets this right by resolving the token before its loop; here the
            // resolution is per attempt, so the grant was asked for three times.
            return Attempt.Terminal(oauth.ConvertFailure<Maybe<JsonDocument>>());
        }

        Maybe<string> oauthToken = oauth.Value;
        using HttpRequestMessage sseRequest = new(HttpMethod.Get, _endpoint);
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(sseRequest, oauthToken);
        using HttpResponseMessage sseResponse = await client
            .SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        Result getOk = EnsureSuccessResult(sseResponse, "SSE channel");
        if (getOk.IsFailure)
        {
            return AttemptFor(sseResponse.StatusCode, getOk.ConvertFailure<Maybe<JsonDocument>>());
        }

        var reader = new SseEventReader();
        Uri? postEndpoint = null;
        using StreamReader streamReader = new(
            await sseResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        while (postEndpoint is null)
        {
            string? line = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // The channel opened and then closed: a blip, and the one failure on
                // this path that genuinely earns its retries.
                return Attempt.Transient(
                    Result.Failure<Maybe<JsonDocument>>("SSE stream closed before announcing an endpoint."));
            }

            if (reader.Feed(line) is { } ev && ev.Event == "endpoint")
            {
                postEndpoint = new Uri(_endpoint, ev.Data.Trim());
            }
        }

        // 2. POST the JSON-RPC request to the announced endpoint.
        using HttpRequestMessage postRequest = new(HttpMethod.Post, postEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(postRequest, oauthToken);
        using HttpResponseMessage postResponse = await client
            .SendAsync(postRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        Result postOk = EnsureSuccessResult(postResponse, "message endpoint");
        if (postOk.IsFailure)
        {
            return AttemptFor(postResponse.StatusCode, postOk.ConvertFailure<Maybe<JsonDocument>>());
        }

        // 3. Keep reading the SSE channel for the response frame.
        while (true)
        {
            string? line = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return Attempt.Transient(
                    Result.Failure<Maybe<JsonDocument>>("SSE stream closed before a response arrived."));
            }

            if (reader.Feed(line) is { Event: "message" } ev
                && McpSse.TryParseResponse(ev.Data, expectedId) is { } doc)
            {
                return Attempt.Succeeded(Result.Success(Maybe<JsonDocument>.From(doc)));
            }
        }
    }

    /// <summary>
    ///     Weights a refused HTTP response. Transient is exactly the set
    ///     <see cref="McpHttpTransport.IsTransientStatus" /> already retries — 5xx
    ///     and 408 — so the two transports cannot disagree about what a server
    ///     hiccup is. Everything else (401/403/404, and 429, which both transports
    ///     also treat as terminal) is an answer rather than a blip.
    /// </summary>
    private static Attempt AttemptFor(HttpStatusCode status, Result<Maybe<JsonDocument>> failure)
        => IsTransientStatus(status)
            ? Attempt.Transient(failure)
            : Attempt.Terminal(failure);

    /// <summary>
    ///     The same set <see cref="McpHttpTransport.IsTransientStatus" /> answers
    ///     for, named so the two are comparable. <see cref="AttemptFor" /> asks
    ///     this instead of restating the expression, so the duplication #822 left
    ///     behind is one expression the two files both name — and
    ///     <c>TransportRetryOwnershipRules</c> can hold the two to each other
    ///     instead of a reader holding them in their head. Same shape as the
    ///     exception-shaped half #572 already hoisted, on the side its rule could
    ///     not reach.
    /// </summary>
    private static bool IsTransientStatus(HttpStatusCode status)
        => (int)status >= 500 || status == HttpStatusCode.RequestTimeout;

    private void ApplyHeaders(HttpRequestMessage request, Maybe<string> oauthToken)
    {
        bool hasAuthorization = false;
        if (_headers is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> header in _headers)
            {
                if (request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    hasAuthorization |= string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        if (!hasAuthorization && oauthToken.HasValue && oauthToken.Value.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken.Value);
        }
    }

    private static Result EnsureSuccessResult(HttpResponseMessage response, string what)
    {
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Accepted)
        {
            return Result.Success();
        }

        return Result.Failure($"{what} returned {(int)response.StatusCode}");
    }

    /// <summary>
    ///     The provider's outcome, verbatim — see
    ///     <see cref="McpHttpTransport" /> for the contract. The nullable overload
    ///     this replaced forced the transport to manufacture the same
    ///     <c>Maybe</c> from a <c>null</c>, and the exception arm beside it could
    ///     never fire (#566).
    /// </summary>
    private async Task<Result<Maybe<string>>> TryGetOAuthTokenAsync(CancellationToken cancellationToken)
    {
        if (_oauthTokenProvider is null)
            return Result.Success(Maybe<string>.None);
        try
        {
            return await _oauthTokenProvider(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<Maybe<string>>($"MCP OAuth token failed: {ex.Message}");
        }
    }

    /// <summary>See <see cref="McpHttpTransport.Fail{T}" /> — same one-line contract, SSE wording.</summary>
    private Result<T> Fail<T>(Stopwatch sw, int attempts, string cause)
    {
        string error = $"SSE {_endpoint} failed after {attempts} attempt(s) in {sw.Elapsed.TotalMilliseconds:0}ms: {cause}";
        if (_logger is not null) McpSseTransportLog.TransportFailure(_logger, error);
        return Result.Failure<T>(error);
    }

    private static Task BackoffAsync(int attempt, CancellationToken cancellationToken)
        => Task.Delay(TransientFailurePolicy.BackoffDelay(attempt), cancellationToken);

    private HttpClient GetClient()
    {
        if (_client is { } existing)
        {
            return existing;
        }

        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan, // the GET stream outlives any single request; per-attempt CTS bounds it
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        _client = client;
        return client;
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="McpSseTransport" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls
///     (including the skip-when-null semantics of the optional logger).
/// </summary>
internal static partial class McpSseTransportLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "MCP SSE round-trip to {Endpoint} failed (attempt {Attempt}/{Max}): {Cause}; reconnecting")]
    public static partial void RoundTripFailed(ILogger logger, Uri endpoint, int attempt, int max, string cause);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "MCP SSE round-trip to {Endpoint} failed (attempt {Attempt}/{Max}); reconnecting")]
    public static partial void RoundTripFailedRetrying(ILogger logger, Exception ex, Uri endpoint, int attempt, int max);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "MCP SSE transport failure: {Error}")]
    public static partial void TransportFailure(ILogger logger, string error);
}
