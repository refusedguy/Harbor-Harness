using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     MCP "Streamable HTTP" client transport (MCP spec 2025-03-26): every JSON-RPC
///     message is POSTed to the server endpoint; the response comes back either as a
///     single <c>application/json</c> body or as a <c>text/event-stream</c>.
///     Connections are lazy — nothing is opened until the first call. Transient
///     failures (network errors, 5xx, 408, client-side timeout) are retried with
///     exponential backoff. The server-assigned <c>Mcp-Session-Id</c> is captured on
///     the first response and replayed on later requests.
///     Authentication: an explicit <c>Authorization</c> header wins; otherwise an
///     OAuth token provider result is attached as <c>Bearer</c>. The provider is a
///     placeholder for the full OAuth2 authorization-code flow (deferred).
/// </summary>
public sealed class McpHttpTransport : IAsyncDisposable
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly Func<string?>? _oauthTokenProvider;
    private readonly ILogger? _logger;
    private readonly TimeSpan _requestTimeout;
    private HttpClient? _client;
    private string? _sessionId;
    private bool _disposed;

    public McpHttpTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<string?>? oauthTokenProvider = null,
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
    /// </summary>
    public async Task<JsonDocument?> RoundTripAsync(
        JsonElement request,
        int? expectedId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HttpClient client = GetClient();
        string body = request.GetRawText();
        int attempt = 1;

        while (true)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_requestTimeout);
            try
            {
                using HttpRequestMessage httpRequest = BuildRequest(body);
                using HttpResponseMessage httpResponse = await client
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);

                if (httpResponse.StatusCode == HttpStatusCode.Accepted)
                {
                    return null;
                }

                if ((int)httpResponse.StatusCode >= 500 || httpResponse.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    throw new HttpRequestException($"MCP endpoint returned {(int)httpResponse.StatusCode}.");
                }

                CaptureSession(httpResponse);
                return await ReadResponseAsync(httpResponse, expectedId, attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Client-side per-attempt timeout — retryable, unlike user cancellation.
                if (attempt >= MaxAttempts)
                {
                    throw new TimeoutException($"MCP endpoint did not respond within {_requestTimeout.TotalSeconds:F0}s.");
                }

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

    private HttpRequestMessage BuildRequest(string body)
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

        if (!hasAuthorization && _oauthTokenProvider?.Invoke() is { Length: > 0 } token)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private async Task<JsonDocument?> ReadResponseAsync(
        HttpResponseMessage response,
        int? expectedId,
        CancellationToken cancellationToken)
    {
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        string mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return JsonDocument.Parse(payload);
        }

        // SSE-bodied response: pick the first message frame answering expectedId.
        var reader = new SseEventReader();
        foreach (string line in payload.Split('\n'))
        {
            if (reader.Feed(line) is { Event: "message" } ev
                && McpSse.TryParseResponse(ev.Data, expectedId) is { } doc)
            {
                return doc;
            }
        }

        throw new HttpRequestException("MCP SSE response carried no matching JSON-RPC frame.");
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
