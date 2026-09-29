using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     MCP legacy "HTTP + SSE" client transport: a long-lived
///     <c>GET /sse</c> event stream carries server→client frames; the first
///     <c>endpoint</c> event names the URL that requests are POSTed to.
///     Connections are lazy and per round-trip — the SSE channel is opened on
///     demand, and any transient failure (stream closed early, POST error,
///     timeout) reconnects and retries the whole round-trip with exponential
///     backoff. Note: unlike streamable HTTP, a retried request may reach the
///     server twice — the legacy transport has no idempotency guarantee.
///     Authentication mirrors <see cref="McpHttpTransport" />: explicit
///     <c>Authorization</c> header wins, else the OAuth token provider result
///     is attached as <c>Bearer</c>.
/// </summary>
public sealed class McpSseTransport : IMcpRemoteTransport
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(200);

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
                Result<Maybe<JsonDocument>> once =
                    await TryRoundTripOnceAsync(client, body, expectedId, attemptCts.Token).ConfigureAwait(false);
                if (once.IsFailure)
                {
                    if (attempt >= MaxAttempts)
                        return Fail<Maybe<JsonDocument>>(sw, attempt, once.Error);
                    _logger?.LogWarning("MCP SSE round-trip to {Endpoint} failed (attempt {Attempt}/{Max}): {Cause}; reconnecting",
                        _endpoint, attempt, MaxAttempts, once.Error);
                    await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                return once;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                string cause = $"server did not respond within {_requestTimeout.TotalSeconds:F0}s";
                if (attempt >= MaxAttempts)
                    return Fail<Maybe<JsonDocument>>(sw, attempt, cause);

                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger?.LogWarning(ex, "MCP SSE round-trip to {Endpoint} failed (attempt {Attempt}/{Max}); reconnecting",
                    _endpoint, attempt, MaxAttempts);
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (IsTransient(ex))
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
    ///     Single SSE round-trip on the Result railway: a closed stream or a
    ///     non-OK endpoint is a <c>Failure</c> (was: <c>IOException</c>/
    ///     <c>HttpRequestException</c> throws). Mid-stream transport exceptions
    ///     still propagate to the retry loop above, which owns the policy.
    /// </summary>
    private async Task<Result<Maybe<JsonDocument>>> TryRoundTripOnceAsync(
        HttpClient client,
        string body,
        int? expectedId,
        CancellationToken cancellationToken)
    {
        // 1. GET the SSE channel and wait for the endpoint announcement.
        Result<Maybe<string>> oauth = await TryGetOAuthTokenAsync(cancellationToken).ConfigureAwait(false);
        if (oauth.IsFailure)
            return oauth.ConvertFailure<Maybe<JsonDocument>>();
        Maybe<string> oauthToken = oauth.Value;
        using HttpRequestMessage sseRequest = new(HttpMethod.Get, _endpoint);
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(sseRequest, oauthToken);
        using HttpResponseMessage sseResponse = await client
            .SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        Result getOk = EnsureSuccessResult(sseResponse, "SSE channel");
        if (getOk.IsFailure)
            return getOk.ConvertFailure<Maybe<JsonDocument>>();

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
                return Result.Failure<Maybe<JsonDocument>>("SSE stream closed before announcing an endpoint.");
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
            return postOk.ConvertFailure<Maybe<JsonDocument>>();

        // 3. Keep reading the SSE channel for the response frame.
        while (true)
        {
            string? line = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return Result.Failure<Maybe<JsonDocument>>("SSE stream closed before a response arrived.");
            }

            if (reader.Feed(line) is { Event: "message" } ev
                && McpSse.TryParseResponse(ev.Data, expectedId) is { } doc)
            {
                return Result.Success(Maybe<JsonDocument>.From(doc));
            }
        }
    }

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
        _logger?.LogError("MCP SSE transport failure: {Error}", error);
        return Result.Failure<T>(error);
    }

    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or IOException or TimeoutException;

    private async Task BackoffAsync(int attempt, CancellationToken cancellationToken)
        => await Task.Delay(FirstRetryDelay * (1 << (attempt - 1)), cancellationToken).ConfigureAwait(false);

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
