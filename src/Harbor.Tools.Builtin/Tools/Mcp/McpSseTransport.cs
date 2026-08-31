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
///     is attached as <c>Bearer</c> (placeholder for the full OAuth2 flow).
/// </summary>
public sealed class McpSseTransport : IMcpRemoteTransport
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly Func<string?>? _oauthTokenProvider;
    private readonly ILogger? _logger;
    private readonly TimeSpan _requestTimeout;
    private HttpClient? _client;
    private bool _disposed;

    public McpSseTransport(
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
    ///     Open the SSE channel, POST one JSON-RPC request and return the first
    ///     <c>message</c> frame answering <paramref name="expectedId" /> (caller
    ///     disposes). Returns null when the stream ends without a matching frame
    ///     (callers treat that as a transport failure and may retry).
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
                return await RoundTripOnceAsync(client, body, expectedId, attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= MaxAttempts)
                {
                    throw new TimeoutException($"MCP SSE endpoint did not respond within {_requestTimeout.TotalSeconds:F0}s.");
                }

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

    private async Task<JsonDocument?> RoundTripOnceAsync(
        HttpClient client,
        string body,
        int? expectedId,
        CancellationToken cancellationToken)
    {
        // 1. GET the SSE channel and wait for the endpoint announcement.
        using HttpRequestMessage sseRequest = new(HttpMethod.Get, _endpoint);
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(sseRequest);
        using HttpResponseMessage sseResponse = await client
            .SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(sseResponse, "SSE channel");

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
                throw new IOException("MCP SSE stream closed before announcing an endpoint.");
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
        ApplyHeaders(postRequest);
        using HttpResponseMessage postResponse = await client
            .SendAsync(postRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(postResponse, "message endpoint");

        // 3. Keep reading the SSE channel for the response frame.
        while (true)
        {
            string? line = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new IOException("MCP SSE stream closed before a response arrived.");
            }

            if (reader.Feed(line) is { Event: "message" } ev
                && McpSse.TryParseResponse(ev.Data, expectedId) is { } doc)
            {
                return doc;
            }
        }
    }

    private void ApplyHeaders(HttpRequestMessage request)
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

        if (!hasAuthorization && _oauthTokenProvider?.Invoke() is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Accepted)
        {
            return;
        }

        throw new HttpRequestException($"MCP {what} returned {(int)response.StatusCode}.");
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
