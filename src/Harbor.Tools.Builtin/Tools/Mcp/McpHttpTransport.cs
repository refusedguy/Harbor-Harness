using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Round-trip contract shared by the remote MCP transports (streamable HTTP
///     and legacy SSE). Public since #477 so a host can plug a third transport
///     in through <see cref="IMcpTransportFactory" /> — it was internal, which
///     made the existing seam unusable from outside this assembly.
/// </summary>
public interface IMcpRemoteTransport : IAsyncDisposable
{
    /// <summary>Send one JSON-RPC request and return the matching response (caller disposes), or null when none arrived.</summary>
    Task<JsonDocument?> RoundTripAsync(
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
    private const int MaxAttempts = 3;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly Func<CancellationToken, Task<string?>>? _oauthTokenProvider;
    private readonly ILogger? _logger;
    private readonly TimeSpan _requestTimeout;
    private HttpClient? _client;
    private string? _sessionId;
    private bool _disposed;

    public McpHttpTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<CancellationToken, Task<string?>>? oauthTokenProvider = null,
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
    ///     POST one JSON-RPC request and return the response document (caller disposes),
    ///     or null when the server answered <c>202 Accepted</c> (notification-style).
    ///     When the response is an SSE stream, the first <c>message</c> frame matching
    ///     <paramref name="expectedId" /> is returned.
    ///     Compat wrapper over <see cref="TryRoundTripAsync" />: terminal transport
    ///     failures map to null (callers treat that as a transport failure), so this
    ///     method only throws on user cancellation or disposal.
    /// </summary>
    public async Task<JsonDocument?> RoundTripAsync(
        JsonElement request,
        int? expectedId = null,
        CancellationToken cancellationToken = default)
    {
        Result<Maybe<JsonDocument>> roundTrip =
            await TryRoundTripAsync(request, expectedId, cancellationToken).ConfigureAwait(false);
        return roundTrip.Match(static doc => doc.HasValue ? doc.Value : null, _ => null);
    }

    /// <summary>
    ///     Result railway for the round-trip (#201 C4): expected network failures
    ///     (5xx/408, client-side timeout, no-frame SSE body, unreachable endpoint)
    ///     surface as <c>Failure(endpoint + attempts + latency + cause)</c> instead of
    ///     throwing. Retry/timeout policy stays inside; user cancellation and
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
        Result<Maybe<string>> oauth = await TryGetOAuthTokenAsync(cancellationToken).ConfigureAwait(false);
        if (oauth.IsFailure)
            return Fail<Maybe<JsonDocument>>(sw, 0, oauth.Error);
        Maybe<string> oauthTokenMaybe = oauth.Value;
        string? oauthToken = oauthTokenMaybe.HasValue ? oauthTokenMaybe.Value : null;
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

                if ((int)httpResponse.StatusCode >= 500 || httpResponse.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    string cause = $"MCP endpoint returned {(int)httpResponse.StatusCode}.";
                    if (attempt >= MaxAttempts)
                        return Fail<Maybe<JsonDocument>>(sw, attempt, cause);
                    _logger?.LogWarning("MCP HTTP request to {Endpoint} failed (attempt {Attempt}/{Max}): {Cause}; retrying",
                        _endpoint, attempt, MaxAttempts, cause);
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
                string cause = $"MCP endpoint did not respond within {_requestTimeout.TotalSeconds:F0}s.";
                if (attempt >= MaxAttempts)
                    return Fail<Maybe<JsonDocument>>(sw, attempt, cause);

                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger?.LogWarning(ex, "MCP HTTP request to {Endpoint} failed (attempt {Attempt}/{Max}); retrying",
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

    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or IOException or TimeoutException;

    private async Task BackoffAsync(int attempt, CancellationToken cancellationToken)
        => await Task.Delay(FirstRetryDelay * (1 << (attempt - 1)), cancellationToken).ConfigureAwait(false);

    private HttpRequestMessage BuildRequest(string body, string? oauthToken)
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

        if (!hasAuthorization && oauthToken is { Length: > 0 })
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
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
            _logger?.LogDebug("MCP HTTP session captured: {SessionId}", sessionId);
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
                return Result.Failure<Maybe<JsonDocument>>($"MCP response was not valid JSON: {ex.Message}");
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

        return Result.Failure<Maybe<JsonDocument>>("MCP SSE response carried no matching JSON-RPC frame.");
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

    private async Task<Result<Maybe<string>>> TryGetOAuthTokenAsync(CancellationToken cancellationToken)
    {
        if (_oauthTokenProvider is null)
            return Result.Success(Maybe<string>.None);
        try
        {
            return Result.Success(Maybe<string>.From(await _oauthTokenProvider(cancellationToken).ConfigureAwait(false)));
        }
        catch (McpOAuthLoginRequiredException ex)
        {
            return Result.Failure<Maybe<string>>(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<Maybe<string>>($"MCP OAuth token failed: {ex.Message}");
        }
    }

    private Result<T> Fail<T>(Stopwatch sw, int attempts, string cause)
    {
        string error = $"MCP HTTP round-trip to {_endpoint} failed after {attempts} attempt(s) in {sw.Elapsed.TotalMilliseconds:0}ms: {cause}";
        _logger?.LogError("MCP HTTP transport failure: {Error}", error);
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
