using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Harbor.Abstractions.Resilience;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Round-trip contract shared by the remote MCP transports (streamable HTTP
///     and legacy SSE). Public since #477 so a host can plug a third transport
///     in through <see cref="IMcpTransportFactory" /> — it was internal, which
///     made the existing seam unusable from outside this assembly.
/// </summary>
/// <remarks>
///     The seam carries <c>Result&lt;Maybe&lt;JsonDocument&gt;&gt;</c> and nothing
///     else (#587). Three outcomes have to stay tellable apart, and a nullable
///     return collapses the first two into one <c>null</c>:
///     <list type="bullet">
///     <item><c>Failure</c> — the transport failed, and the error names the endpoint,
///     the HTTP status, the attempt count and the latency.</item>
///     <item><c>Success(Maybe.None)</c> — the server answered, and the answer
///     legitimately carries no document (<c>202 Accepted</c>, empty body).</item>
///     <item><c>Success(Maybe.From(doc))</c> — the response (caller disposes).</item>
///     </list>
///     A nullable-returning member is banned here by
///     <c>tests/Harbor.Architecture.Tests/RemoteTransportResultRules.cs</c>, which
///     reflects over this interface and fails if one reappears.
/// </remarks>
public interface IMcpRemoteTransport : IAsyncDisposable
{
    /// <summary>
    ///     Send one JSON-RPC request and return the matching response (caller
    ///     disposes). <c>Maybe.None</c> is a successful round-trip whose answer
    ///     carries no document — not a failure. User cancellation and disposal
    ///     still throw; every other wire failure is a <c>Failure</c>.
    /// </summary>
    Task<Result<Maybe<JsonDocument>>> TryRoundTripAsync(
        JsonElement request,
        int? expectedId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     MCP "Streamable HTTP" client transport (MCP spec 2025-03-26): every JSON-RPC
///     message is POSTed to the server endpoint; the response comes back either as a
///     single <c>application/json</c> body or as a <c>text/event-stream</c>.
///     Connections are lazy — nothing is opened until the first call. Transient
///     failures (network errors, 5xx, 408, client-side timeout) are retried with
///     exponential backoff. The server-assigned <c>Mcp-Session-Id</c> is captured on
///     the first response and replayed on later requests.
///     Authentication: an explicit <c>Authorization</c> header wins; otherwise an
///     OAuth token provider result is attached as <c>Bearer</c> (backed by
///     <see cref="McpOAuthHandler" /> when the server has an <c>auth</c> config,
///     else the <c>HARBOR_MCP_OAUTH_TOKEN</c> environment fallback).
/// </summary>
public sealed class McpHttpTransport : IMcpRemoteTransport
{
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly Func<CancellationToken, Task<Result<Maybe<string>>>>? _oauthTokenProvider;
    private readonly ILogger? _logger;
    private readonly TimeSpan _requestTimeout;
    private HttpClient? _client;
    private string? _sessionId;
    private bool _disposed;

    public McpHttpTransport(
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
    ///     #587): expected network failures (5xx/408, client-side timeout, no-frame
    ///     SSE body, unreachable endpoint) surface as
    ///     <c>Failure(endpoint + attempts + latency + cause)</c> instead of throwing.
    ///     Retry/timeout policy stays inside; user cancellation and disposal still
    ///     throw.
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
        Result<Maybe<string>> oauth = await TryGetOAuthTokenAsync(cancellationToken).ConfigureAwait(false);
        if (oauth.IsFailure)
            return Fail<Maybe<JsonDocument>>(sw, 0, oauth.Error);
        Maybe<string> oauthToken = oauth.Value;
        int attempt = 1;

        while (true)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_requestTimeout);
            try
            {
                using HttpRequestMessage httpRequest = BuildRequest(body, oauthToken);
                using HttpResponseMessage httpResponse = await client
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);

                if (httpResponse.StatusCode == HttpStatusCode.Accepted)
                {
                    return Result.Success(Maybe<JsonDocument>.None);
                }

                if (IsTransientStatus(httpResponse.StatusCode))
                {
                    string cause = $"server returned {(int)httpResponse.StatusCode}";
                    if (attempt >= TransientFailurePolicy.DefaultMaxAttempts)
                        return Fail<Maybe<JsonDocument>>(sw, attempt, cause);
                    if (_logger is not null) McpHttpTransportLog.RequestFailed(_logger, _endpoint, attempt, TransientFailurePolicy.DefaultMaxAttempts, cause);
                    await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                CaptureSession(httpResponse);
                Result<Maybe<JsonDocument>> read =
                    await TryReadResponseAsync(httpResponse, expectedId, attemptCts.Token).ConfigureAwait(false);
                if (read.IsFailure)
                {
                    // The response was consumed: a no-frame SSE body is terminal.
                    return Fail<Maybe<JsonDocument>>(sw, attempt, read.Error);
                }

                return read;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Client-side per-attempt timeout — retryable, unlike user cancellation.
                string cause = $"server did not respond within {_requestTimeout.TotalSeconds:F0}s";
                if (attempt >= TransientFailurePolicy.DefaultMaxAttempts)
                    return Fail<Maybe<JsonDocument>>(sw, attempt, cause);

                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (TransientFailurePolicy.ShouldRetry(ex)
                                       && attempt < TransientFailurePolicy.DefaultMaxAttempts)
            {
                if (_logger is not null) McpHttpTransportLog.RequestFailedRetrying(_logger, ex, _endpoint, attempt, TransientFailurePolicy.DefaultMaxAttempts);
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
                // Expected wire failure outside the transient set (e.g. malformed
                // JSON body): terminal, observable, never a throw.
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
    ///     Weights a refused HTTP response, while the response is still in hand
    ///     (#714). Transient is exactly the set
    ///     <see cref="McpSseTransport.AttemptFor" /> already retries — 5xx and
    ///     408 — so the two transports cannot disagree about what a server
    ///     hiccup is. Everything else (401/403/404, and 429, which both
    ///     transports also treat as terminal) is an answer rather than a blip.
    ///     <para>
    ///         A named method, not an inline <c>if</c> in the loop above. Not for
    ///         readability: the verdict is a duplicated answer, and
    ///         <c>TransportRetryOwnershipRules</c> can only hold the two copies
    ///         to each other if it can call both. <c>Assert_NoTransportDeclaresIts
    ///         OwnRetryClassifier</c> matches <c>(Exception) -&gt; bool</c> and so
    ///         cannot see this question at all — which is how the duplication
    ///         outlived #572, whose rule was written for the exception-shaped
    ///         half. A verdict with no name has nothing to compare against.
    ///     </para>
    /// </summary>
    private static bool IsTransientStatus(HttpStatusCode status)
        => (int)status >= 500 || status == HttpStatusCode.RequestTimeout;

    private static Task BackoffAsync(int attempt, CancellationToken cancellationToken)
        => Task.Delay(TransientFailurePolicy.BackoffDelay(attempt), cancellationToken);

    private HttpRequestMessage BuildRequest(string body, Maybe<string> oauthToken)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        bool hasAuthorization = false;
        if (_headers is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> header in _headers)
            {
                if (httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    hasAuthorization |= string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        if (!hasAuthorization && oauthToken.HasValue && oauthToken.Value.Length > 0)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken.Value);
        }

        if (_sessionId is not null)
        {
            httpRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        return httpRequest;
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (_sessionId is null
            && response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values)
            && values.FirstOrDefault() is { Length: > 0 } sessionId)
        {
            _sessionId = sessionId;
            if (_logger is not null) McpHttpTransportLog.SessionCaptured(_logger, sessionId);
        }
    }

    /// <summary>
    ///     Read one response body on the Result railway (#201 A7): an empty payload
    ///     is a valid notification-style <c>Success(Maybe.None)</c>; an SSE body without
    ///     a matching frame is a terminal <c>Failure</c> (was: throw in the same method
    ///     that returned null — nulls and throws no longer share one signature).
    /// </summary>
    private async Task<Result<Maybe<JsonDocument>>> TryReadResponseAsync(
        HttpResponseMessage response,
        int? expectedId,
        CancellationToken cancellationToken)
    {
        // #180: read bytes and parse from UTF-8 directly — the JSON branch
        // never materializes the body as a string.
        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (IsBlank(payload))
        {
            return Result.Success(Maybe<JsonDocument>.None);
        }

        string mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Result.Success(Maybe<JsonDocument>.From(JsonDocument.Parse(payload)));
            }
            catch (JsonException ex)
            {
                return Result.Failure<Maybe<JsonDocument>>($"response was not valid JSON: {ex.Message}");
            }
        }

        // SSE-bodied response: pick the first message frame answering expectedId.
        string text = Encoding.UTF8.GetString(payload);
        var reader = new SseEventReader();
        foreach (string line in text.Split('\n'))
        {
            if (reader.Feed(line) is { Event: "message" } ev
                && McpSse.TryParseResponse(ev.Data, expectedId) is { } doc)
            {
                return Result.Success(Maybe<JsonDocument>.From(doc));
            }
        }

        return Result.Failure<Maybe<JsonDocument>>("SSE response carried no matching JSON-RPC frame.");
    }

    /// <summary>
    /// Empty-or-ASCII-whitespace body — mirrors the old
    /// <c>string.IsNullOrWhiteSpace</c> check for every payload JSON admits
    /// (JSON whitespace is space/tab/CR/LF only).
    /// </summary>
    private static bool IsBlank(ReadOnlySpan<byte> payload)
    {
        foreach (byte b in payload)
        {
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
                return false;
        }

        return true;
    }

    /// <summary>
    ///     The provider's outcome, verbatim. The seam speaks
    ///     <c>Result&lt;Maybe&lt;string&gt;&gt;</c> end to end, so "no token yet"
    ///     and "the grant was rejected" arrive as two distinct states instead of
    ///     both arriving as the <c>null</c> this method used to manufacture out of
    ///     either one (#566). The catch-all stays as a floor for a hand-written
    ///     provider that throws; it is not a shape this transport produces.
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

    /// <summary>
    ///     The one place a transport failure is spelled out (#587). One line, in the
    ///     order an operator reads it — which link, how many tries, how long, and
    ///     then the server's own words. The leading <c>MCP</c> is dropped because
    ///     every caller reaches this through a message that already names MCP
    ///     (<c>MCP server 'x': …</c>), and repeating it only made the line longer.
    /// </summary>
    private Result<T> Fail<T>(Stopwatch sw, int attempts, string cause)
    {
        string error = $"HTTP {_endpoint} failed after {attempts} attempt(s) in {sw.Elapsed.TotalMilliseconds:0}ms: {cause}";
        if (_logger is not null) McpHttpTransportLog.TransportFailure(_logger, error);
        return Result.Failure<T>(error);
    }

    private HttpClient GetClient()
    {
        if (_client is { } existing)
        {
            return existing;
        }

        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler)
        {
            Timeout = _requestTimeout,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        _client = client;
        return client;
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="McpHttpTransport" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls
///     (including the skip-when-null semantics of the optional logger).
/// </summary>
internal static partial class McpHttpTransportLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "MCP HTTP request to {Endpoint} failed (attempt {Attempt}/{Max}): {Cause}; retrying")]
    public static partial void RequestFailed(ILogger logger, Uri endpoint, int attempt, int max, string cause);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "MCP HTTP request to {Endpoint} failed (attempt {Attempt}/{Max}); retrying")]
    public static partial void RequestFailedRetrying(ILogger logger, Exception ex, Uri endpoint, int attempt, int max);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "MCP HTTP session captured: {SessionId}")]
    public static partial void SessionCaptured(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "MCP HTTP transport failure: {Error}")]
    public static partial void TransportFailure(ILogger logger, string error);
}
